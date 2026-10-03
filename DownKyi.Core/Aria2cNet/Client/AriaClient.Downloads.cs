using DownKyi.Core.Aria2cNet.Client.Entity;

namespace DownKyi.Core.Aria2cNet.Client;

public sealed partial class AriaClient
{
    /// <summary>
    /// This method adds a new download.
    /// uris is an array of HTTP/FTP/SFTP/BitTorrent URIs (strings) pointing to the same resource.
    /// If you mix URIs pointing to different resources,
    /// then the download may fail or be corrupted without aria2 complaining.
    /// When adding BitTorrent Magnet URIs,
    /// uris must have only one element and it should be BitTorrent Magnet URI.
    /// options is a struct and its members are pairs of option name and value.
    /// See Options below for more details.
    /// If position is given, it must be an integer starting from 0.
    /// The new download will be inserted at position in the waiting queue.
    /// If position is omitted or position is larger than the current size of the queue,
    /// the new download is appended to the end of the queue.
    /// This method returns the GID of the newly registered download.
    /// </summary>
    /// <param name="uris"></param>
    /// <param name="dir"></param>
    /// <param name="outFile"></param>
    /// <returns></returns>
    public async Task<AriaAddUri> AddUriAsync(IReadOnlyList<string> uris, AriaSendOption option, int position = -1)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            uris,
            option
        };
        if (position > -1)
        {
            ariaParams.Add(position);
        }

        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.addUri",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaAddUri>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method removes the download denoted by gid (string).
    /// If the specified download is in progress, it is first stopped.
    /// The status of the removed download becomes removed.
    /// This method returns GID of removed download.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaRemove> RemoveAsync(
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
            Method = "aria2.remove",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaRemove>(ariaSend, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This method removes the download denoted by gid.
    /// This method behaves just like aria2.remove()
    /// except that this method removes the download without performing any actions which take time,
    /// such as contacting BitTorrent trackers to unregister the download first.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaRemove> ForceRemoveAsync(
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
            Method = "aria2.forceRemove",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaRemove>(ariaSend, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This method pauses the download denoted by gid (string).
    /// The status of paused download becomes paused.
    /// If the download was active, the download is placed in the front of waiting queue.
    /// While the status is paused, the download is not started.
    /// To change status to waiting, use the aria2.unpause() method.
    /// This method returns GID of paused download.
    /// </summary>
    /// <param name="gid"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AriaPause> PauseAsync(
        string gid,
        CancellationToken cancellationToken)
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
            Method = "aria2.pause",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaPause>(ariaSend, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This method is equal to calling aria2.pause() for every active/waiting download.
    /// This methods returns OK.
    /// </summary>
    /// <returns></returns>
    public async Task<AriaPause> PauseAllAsync()
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.pauseAll",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaPause>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method changes the status of the download denoted by gid (string) from paused to waiting,
    /// making the download eligible to be restarted.
    /// This method returns the GID of the unpaused download.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaPause> UnpauseAsync(string gid)
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
            Method = "aria2.unpause",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaPause>(ariaSend).ConfigureAwait(false);
    }

}
