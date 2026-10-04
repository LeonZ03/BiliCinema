using System.Text;
using DownKyi.Services.Watch;

namespace DownKyi.Desktop.Tests;

public sealed class MpvIpcEncodingTests
{
    [Fact]
    public void FirstCommandStartsWithJsonObjectWithoutUtf8Bom()
    {
        using var pipe = new MemoryStream();
        using (var writer = MpvPlaybackSession.CreateCommandWriter(pipe))
        {
            writer.WriteLine("{\"command\":[\"get_property\",\"pause\"],\"request_id\":1}");
        }

        var bytes = pipe.ToArray();
        Assert.Equal((byte)'{', bytes[0]);
        Assert.StartsWith("{\"command\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }
}
