using DownKyi.Core.Aria2cNet.Client.Entity;

namespace DownKyi.Core.Aria2cNet.Client;

public sealed partial class AriaClient
{
    /// <summary>
    /// This method removes a completed/error/removed download denoted by gid from memory.
    /// This method returns OK for success.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaRemove> RemoveDownloadResultAsync(
        string gid,
        CancellationToken cancellationToken = default)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            gid
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.removeDownloadResult",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaRemove>(ariaSend, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns the version of aria2 and the list of enabled features.
    /// The response is a struct and contains following keys.
    /// </summary>
    /// <returns></returns>
    public async Task<AriaVersion> GetAriaVersionAsync(
        CancellationToken cancellationToken = default)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.getVersion",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaVersion>(ariaSend, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// This method shuts down aria2.
    /// This method returns OK.
    /// </summary>
    /// <returns></returns>
    public async Task<AriaShutdown> ShutdownAsync()
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.shutdown",
            Params = ariaParams
        };
        var re = await GetRpcResponseAsync<AriaShutdown>(ariaSend).ConfigureAwait(false);
        return re;
    }

    /// <summary>
    /// This method shuts down aria2().
    /// This method behaves like :func:'aria2.shutdown` without performing any actions which take time,
    /// such as contacting BitTorrent trackers to unregister downloads first.
    /// This method returns OK.
    /// </summary>
    /// <returns></returns>
    public async Task<AriaShutdown> ForceShutdownAsync()
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.forceShutdown",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaShutdown>(ariaSend).ConfigureAwait(false);
    }

}
