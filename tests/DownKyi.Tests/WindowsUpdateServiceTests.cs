using System.Net;
using System.Security.Cryptography;
using DownKyi.Application.Lifetime;
using DownKyi.Models;
using DownKyi.Services.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class WindowsUpdateServiceTests
{
    [Fact]
    public async Task CorruptPackageKeepsInstalledExecutableAndRemovesOwnedStaging()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bilicinema-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "BiliCinema.exe");
        byte[] original = [77, 90, 1, 2, 3];
        byte[] package = [77, 90, 4, 5, 6];
        try
        {
            await File.WriteAllBytesAsync(target, original, TestContext.Current.CancellationToken);
            using var handler = new PackageHandler(package, new string('0', 64));
            using var client = new HttpClient(handler);
            var lifecycle = new UnusedLifecycle();
            var service = new WindowsUpdateService(client, lifecycle, NullLogger<WindowsUpdateService>.Instance);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallCoreAsync(target,
                Release(package.Length), new Progress<UpdateProgress>(), TestContext.Current.CancellationToken));

            Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateDirectories(directory));
            Assert.Equal(0, lifecycle.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("dotnet.exe", "BiliCinema.dll", false)]
    [InlineData("BiliCinema.exe", "BiliCinema.dll", false)]
    [InlineData("testhost.exe", "", false)]
    [InlineData("BiliCinema.exe", "", true)]
    [InlineData("BiliCinema-1.5.1-win-x64.exe", "", true)]
    [InlineData("BiliCinema-1.5.1-win-x64.exe", "BiliCinema.dll", false)]
    [InlineData("BiliCinema-1.5.1-arm64.exe", "", false)]
    [InlineData("BiliCinema-01.5.1-win-x64.exe", "", false)]
    [InlineData("BiliCinema-1.5.1-win-x64.exe\n", "", false)]
    public void OnlyPublishedSingleExecutableCanBeReplaced(string processPath, string assemblyPath, bool expected)
    {
        Assert.Equal(expected, WindowsUpdateService.IsPublishedExecutable(processPath, assemblyPath));
    }

    [Fact]
    public void AssetMustBelongToSameReleaseAndRepository()
    {
        var release = Release(3);
        var asset = WindowsUpdateService.RequireExecutableAsset(release);
        Assert.Equal(asset.DownloadUrl, WindowsUpdateService.RequireReleaseAssetUri(release, asset).AbsoluteUri);
        asset.DownloadUrl = "https://github.com/other/repository/releases/download/bilicinema-v1.5.1/BiliCinema-1.5.1-win-x64.exe";
        Assert.Throws<InvalidDataException>(() => WindowsUpdateService.RequireReleaseAssetUri(release, asset));
    }

    [Theory]
    [InlineData("BiliCinema-1.5.1-win-x64.exe")]
    [InlineData("BiliCinema.exe")]
    public void ReleaseCanSelectVersionedAndLegacyPackages(string name)
    {
        var release = Release(3);
        release.Assets[0].Name = name;
        Assert.Same(release.Assets[0], WindowsUpdateService.RequireExecutableAsset(release));
    }

    [Fact]
    public void VersionedPackageMustMatchReleaseAndBeUnambiguous()
    {
        var release = Release(3);
        release.Assets[0].Name = "BiliCinema-1.4.0-win-x64.exe";
        Assert.Throws<InvalidDataException>(() => WindowsUpdateService.RequireExecutableAsset(release));
        release.Assets[0].Name = "BiliCinema-1.5.1-win-x64.exe";
        release.Assets = [.. release.Assets, release.Assets[0]];
        Assert.Throws<InvalidDataException>(() => WindowsUpdateService.RequireExecutableAsset(release));
    }

    [Fact]
    public void MissingOrDuplicateChecksumsFailClosed()
    {
        var hash = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
        Assert.Equal(hash, WindowsUpdateService.ParseChecksum($"{hash}  BiliCinema.exe\n", "BiliCinema.exe"));
        Assert.Throws<InvalidDataException>(() => WindowsUpdateService.ParseChecksum($"{hash}  other.exe", "BiliCinema.exe"));
        Assert.Throws<InvalidDataException>(() => WindowsUpdateService.ParseChecksum(
            $"{hash}  BiliCinema.exe\n{hash} *BiliCinema.exe\n", "BiliCinema.exe"));
    }

    private static GitHubRelease Release(int size) => new()
    {
        TagName = "bilicinema-v1.5.1",
        Assets =
        [
            new GitHubReleaseAsset
            {
                Name = "BiliCinema-1.5.1-win-x64.exe", Size = size,
                DownloadUrl = "https://github.com/LeonZ03/BiliCinema/releases/download/bilicinema-v1.5.1/BiliCinema-1.5.1-win-x64.exe"
            },
            new GitHubReleaseAsset
            {
                Name = "SHA256SUMS.txt",
                DownloadUrl = "https://github.com/LeonZ03/BiliCinema/releases/download/bilicinema-v1.5.1/SHA256SUMS.txt"
            }
        ]
    };

    private sealed class PackageHandler(byte[] package, string digest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                    ? new StringContent($"{digest}  BiliCinema-1.5.1-win-x64.exe\n")
                    : new ByteArrayContent(package)
            });
        }
    }

    private sealed class UnusedLifecycle : IApplicationLifecycle
    {
        public int Calls { get; private set; }
        public Task RequestShutdownAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Corrupt update must never shut down the application.");
        }

        public Task ExitAsync(CancellationToken cancellationToken = default) => RequestShutdownAsync(cancellationToken);
        public Task<bool> RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
