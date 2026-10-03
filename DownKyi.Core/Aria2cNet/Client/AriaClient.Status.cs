using DownKyi.Core.Aria2cNet.Client.Entity;

namespace DownKyi.Core.Aria2cNet.Client;

public sealed partial class AriaClient
{
    public async Task<AriaTellStatus> TellStatus(
        string gid,
        CancellationToken cancellationToken)
    {
        List<object> ariaParams =
        [
            "token:" + _token,
            gid
        ];
        AriaSendData ariaSend = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.tellStatus",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaTellStatus>(ariaSend, cancellationToken)
            .ConfigureAwait(false);
    }
}
