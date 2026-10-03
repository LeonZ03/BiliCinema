using System.Reflection;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.Tests;

public sealed class AriaClientRpcContractTests
{
    [Fact]
    public async Task PublicRpcMethodsKeepTheirAria2WireMethodAndAuthenticationContract()
    {
        var cases = CreateCases();
        var publicRpcMethods = typeof(AriaClient)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            publicRpcMethods,
            cases.Select(testCase => testCase.MethodName)
                .Order(StringComparer.Ordinal));

        foreach (var testCase in cases)
        {
            string? capturedPayload = null;
            var captureSignal = new InvalidOperationException("RPC payload captured.");
            var client = new AriaClient(
                "https://aria-contract.example",
                35076,
                "contract-token",
                (_, payload) =>
                {
                    capturedPayload = payload;
                    return Task.FromException<string?>(captureSignal);
                });

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => testCase.Invoke(client));
            Assert.Same(captureSignal, thrown);

            var request = JObject.Parse(Assert.IsType<string>(capturedPayload));
            Assert.Equal("2.0", request["jsonrpc"]?.Value<string>());
            Assert.False(string.IsNullOrWhiteSpace(request["id"]?.Value<string>()));
            Assert.Equal(testCase.RpcMethod, request["method"]?.Value<string>());

            var parameters = request["params"] as JArray;
            if (testCase.RequiresToken)
            {
                Assert.NotNull(parameters);
                Assert.Equal("token:contract-token", parameters[0]?.Value<string>());
            }
            else
            {
                var firstParameter = parameters?.First;
                Assert.False(
                    firstParameter?.Type == JTokenType.String
                    && firstParameter.Value<string>() == "token:contract-token");
            }
        }
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("force-remove")]
    [InlineData("remove-result")]
    [InlineData("pause")]
    [InlineData("tell-status")]
    public async Task SelectedRpcMethodsPropagateCancellation(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedToken = CancellationToken.None;
        var client = new AriaClient(
            "https://aria-contract.example",
            35076,
            "contract-token",
            async (_, _, cancellationToken) =>
            {
                observedToken = cancellationToken;
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return null;
            });
        Task request = operation switch
        {
            "remove" => client.RemoveAsync("gid", cancellation.Token),
            "force-remove" => client.ForceRemoveAsync("gid", cancellation.Token),
            "remove-result" => client.RemoveDownloadResultAsync("gid", cancellation.Token),
            "pause" => client.PauseAsync("gid", cancellation.Token),
            "tell-status" => client.TellStatus("gid", cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(cancellation.Token, observedToken);
    }

    [Theory]
    [InlineData(nameof(AriaClient.PauseAsync))]
    [InlineData(nameof(AriaClient.TellStatus))]
    public void PauseCheckpointRpcMethodsRequireCancellationToken(string methodName)
    {
        var method = Assert.Single(typeof(AriaClient).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal));
        var cancellationParameter = Assert.Single(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(CancellationToken));

        Assert.False(cancellationParameter.IsOptional);
        Assert.False(cancellationParameter.HasDefaultValue);
    }

    [Fact]
    public async Task RpcClientPropagatesTransportFailureWithoutRetry()
    {
        var requestCount = 0;
        var failure = new HttpRequestException("RPC transport failed.");
        var client = new AriaClient(
            "https://aria-contract.example",
            35076,
            "contract-token",
            (_, _) =>
            {
                requestCount++;
                return Task.FromException<string?>(failure);
            });

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.AddUriAsync(
                ["https://media.example/video"],
                new AriaSendOption()));

        Assert.Same(failure, thrown);
        Assert.Equal(1, requestCount);
    }

    private static IReadOnlyList<RpcContractCase> CreateCases()
    {
        var sendOption = new AriaSendOption();
        return
        [
            new(nameof(AriaClient.AddUriAsync), "aria2.addUri", true, client => client.AddUriAsync(["https://media.example/video"], sendOption)),
            new(nameof(AriaClient.RemoveAsync), "aria2.remove", true, client => client.RemoveAsync("gid")),
            new(nameof(AriaClient.ForceRemoveAsync), "aria2.forceRemove", true, client => client.ForceRemoveAsync("gid")),
            new(nameof(AriaClient.PauseAsync), "aria2.pause", true, client => client.PauseAsync("gid", TestContext.Current.CancellationToken)),
            new(nameof(AriaClient.PauseAllAsync), "aria2.pauseAll", true, client => client.PauseAllAsync()),
            new(nameof(AriaClient.UnpauseAsync), "aria2.unpause", true, client => client.UnpauseAsync("gid")),
            new(nameof(AriaClient.TellStatus), "aria2.tellStatus", true, client => client.TellStatus("gid", TestContext.Current.CancellationToken)),
            new(nameof(AriaClient.GetUrisAsync), "aria2.getUris", true, client => client.GetUrisAsync("gid")),
            new(nameof(AriaClient.GetFilesAsync), "aria2.getFiles", true, client => client.GetFilesAsync("gid")),
            new(nameof(AriaClient.GetPeersAsync), "aria2.getPeers", true, client => client.GetPeersAsync("gid")),
            new(nameof(AriaClient.GetServersAsync), "aria2.getServers", true, client => client.GetServersAsync("gid")),
            new(nameof(AriaClient.TellActiveAsync), "aria2.tellActive", true, client => client.TellActiveAsync()),
            new(nameof(AriaClient.TellWaitingAsync), "aria2.tellWaiting", true, client => client.TellWaitingAsync(0, 10)),
            new(nameof(AriaClient.TellStoppedAsync), "aria2.tellStopped", true, client => client.TellStoppedAsync(0, 10)),
            new(nameof(AriaClient.ChangePositionAsync), "aria2.changePosition", true, client => client.ChangePositionAsync("gid", 0, HowChangePosition.PosSet)),
            new(nameof(AriaClient.ChangeUriAsync), "aria2.changeUri", true, client => client.ChangeUriAsync("gid", 1, [], ["https://media.example/video"])),
            new(nameof(AriaClient.ChangeOptionAsync), "aria2.changeOption", true, client => client.ChangeOptionAsync("gid", new { Split = "4" })),
            new(nameof(AriaClient.GetGlobalStatAsync), "aria2.getGlobalStat", true, client => client.GetGlobalStatAsync()),
            new(nameof(AriaClient.RemoveDownloadResultAsync), "aria2.removeDownloadResult", true, client => client.RemoveDownloadResultAsync("gid")),
            new(nameof(AriaClient.GetAriaVersionAsync), "aria2.getVersion", true, client => client.GetAriaVersionAsync()),
            new(nameof(AriaClient.ShutdownAsync), "aria2.shutdown", true, client => client.ShutdownAsync()),
            new(nameof(AriaClient.ForceShutdownAsync), "aria2.forceShutdown", true, client => client.ForceShutdownAsync())
        ];
    }

    private sealed record RpcContractCase(
        string MethodName,
        string RpcMethod,
        bool RequiresToken,
        Func<AriaClient, Task> Invoke);
}
