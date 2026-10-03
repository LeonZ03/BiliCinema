using DownKyi.Core.Aria2cNet.Client.Entity;

namespace DownKyi.Core.Aria2cNet.Client;

public sealed partial class AriaClient
{
    /// <summary>
    /// This method changes options of the download denoted by gid (string) dynamically.
    /// options is a struct.
    /// The options listed in Input File subsection are available, except for following options:
    /// <br/>
    /// dry-run metalink-base-uri parameterized-uri pause piece-length rpc-save-upload-metadata
    /// <br/>
    /// Except for the following options,
    /// changing the other options of active download makes it restart
    /// (restart itself is managed by aria2, and no user intervention is required):
    /// <br/>
    /// bt-max-peers bt-request-peer-speed-limit bt-remove-unselected-file force-save max-download-limit max-upload-limit
    /// <br/>
    /// This method returns OK for success.
    /// </summary>
    /// <param name="gid"></param>
    /// <param name="option"></param>
    /// <returns></returns>
    public async Task<AriaChangeOption> ChangeOptionAsync(string gid, object option)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            gid,
            option
        };

        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.changeOption",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaChangeOption>(ariaSend).ConfigureAwait(false);
    }

}
