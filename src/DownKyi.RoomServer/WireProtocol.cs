using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownKyi.RoomServer;

internal sealed class RoomProtocolException : Exception
{
    public RoomProtocolException() : this("protocol_error") { }

    public RoomProtocolException(string code) : base(code) => Code = code;

    public RoomProtocolException(string code, Exception innerException) : base(code, innerException) => Code = code;

    public string Code { get; } = "protocol_error";
}

internal sealed record MediaIdentity(long? EpisodeId, long? Aid, string? Bvid, long? Cid);

internal sealed record WireMessage(
    string Type,
    string? RoomCode = null,
    string? ClientId = null,
    MediaIdentity? Media = null,
    double? PositionSeconds = null,
    double? Rate = null,
    bool? Ready = null,
    bool? Buffering = null,
    string? Nonce = null,
    long? ClientTimeUnixMs = null);

internal static class WireProtocol
{
    private const int MaxMessageBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static string Error(string code) => Encode(new { type = "error", code });

    public static async Task SendDirectErrorAsync(WebSocket socket, string code, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Error(code));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxMessageBytes);
        try
        {
            int used = 0;
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, used, MaxMessageBytes - used), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new RoomProtocolException("connection_closed");
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new RoomProtocolException("text_required");
                }

                used += result.Count;
                if (!result.EndOfMessage && used == MaxMessageBytes)
                {
                    throw new RoomProtocolException("message_too_large");
                }

                if (result.EndOfMessage)
                {
                    try
                    {
                        return JsonDocument.Parse(buffer.AsMemory(0, used), new JsonDocumentOptions
                        {
                            MaxDepth = 4,
                            CommentHandling = JsonCommentHandling.Disallow,
                            AllowTrailingCommas = false
                        });
                    }
                    catch (JsonException)
                    {
                        throw new RoomProtocolException("invalid_json");
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static WireMessage Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new RoomProtocolException("invalid_message");
        }

        string type = RequiredString(root, "type", 1, 24);
        switch (type)
        {
            case "create":
                RequireFields(root, "type");
                return new(type);
            case "join":
                RequireFields(root, "type", "roomCode", "clientId");
                string roomCode = RequiredSecret(root, "roomCode");
                string? clientId = OptionalSecret(root, "clientId");
                return new(type, roomCode, clientId);
            case "select":
                RequireFields(root, "type", "media");
                if (!root.TryGetProperty("media", out JsonElement media))
                {
                    throw new RoomProtocolException("invalid_media");
                }

                return new(type, Media: ParseMedia(media));
            case "seek":
                RequireFields(root, "type", "positionSeconds");
                return new(type, PositionSeconds: RequiredNumber(root, "positionSeconds", 0, 86400));
            case "rate":
                RequireFields(root, "type", "rate");
                return new(type, Rate: RequiredNumber(root, "rate", 0.25, 3));
            case "ready":
                RequireFields(root, "type", "ready");
                return new(type, Ready: RequiredBoolean(root, "ready"));
            case "buffering":
                RequireFields(root, "type", "buffering");
                return new(type, Buffering: RequiredBoolean(root, "buffering"));
            case "ping":
                RequireFields(root, "type", "nonce", "clientTimeUnixMs");
                return new(type,
                    Nonce: root.TryGetProperty("nonce", out _) ? RequiredString(root, "nonce", 1, 64) : null,
                    ClientTimeUnixMs: RequiredPositiveInteger(root, "clientTimeUnixMs"));
            case "play":
            case "pause":
            case "leave":
            case "close":
                RequireFields(root, "type");
                return new(type);
            default:
                throw new RoomProtocolException("unknown_type");
        }
    }

    private static MediaIdentity ParseMedia(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new RoomProtocolException("invalid_media");
        }

        RequireFields(root, "episodeId", "aid", "bvid", "cid");
        long? episodeId = OptionalPositiveInteger(root, "episodeId");
        long? aid = OptionalPositiveInteger(root, "aid");
        long? cid = OptionalPositiveInteger(root, "cid");
        string? bvid = null;
        if (root.TryGetProperty("bvid", out JsonElement bvidElement))
        {
            if (bvidElement.ValueKind != JsonValueKind.String)
            {
                throw new RoomProtocolException("invalid_media");
            }

            bvid = bvidElement.GetString();
            if (bvid is null || bvid.Length != 12 || !bvid.StartsWith("BV", StringComparison.Ordinal) ||
                !bvid.AsSpan(2).ToString().All(char.IsAsciiLetterOrDigit))
            {
                throw new RoomProtocolException("invalid_media");
            }
        }

        if (episodeId is null && aid is null && cid is null && bvid is null)
        {
            throw new RoomProtocolException("invalid_media");
        }

        return new(episodeId, aid, bvid, cid);
    }

    private static long? OptionalPositiveInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long value) || value <= 0)
        {
            throw new RoomProtocolException("invalid_media");
        }

        return value;
    }

    private static long RequiredPositiveInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt64(out long value) || value <= 0)
        {
            throw new RoomProtocolException("invalid_number");
        }

        return value;
    }

    private static double RequiredNumber(JsonElement root, string name, double minimum, double maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDouble(out double value) || !double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new RoomProtocolException("invalid_number");
        }

        return value;
    }

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new RoomProtocolException("invalid_boolean");
        }

        return element.GetBoolean();
    }

    private static string RequiredString(JsonElement root, string name, int minLength, int maxLength)
    {
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            throw new RoomProtocolException("invalid_string");
        }

        string? value = element.GetString();
        if (value is null || value.Length < minLength || value.Length > maxLength || value.Any(char.IsControl))
        {
            throw new RoomProtocolException("invalid_string");
        }

        return value;
    }

    private static string RequiredSecret(JsonElement root, string name)
    {
        string value = RequiredString(root, name, 22, 22);
        if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new RoomProtocolException("invalid_invite");
        }

        return value;
    }

    private static string? OptionalSecret(JsonElement root, string name) =>
        root.TryGetProperty(name, out _) ? RequiredSecret(root, name) : null;

    private static void RequireFields(JsonElement root, params string[] allowed)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new RoomProtocolException("unexpected_field");
            }
        }
    }
}
