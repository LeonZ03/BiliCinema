using System.Text.Json;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class ReleaseWorkflowArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ExecutableUsesImplementationAssembliesWhenBuildingTheSolution()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var policy = props.Descendants().Single(element => element.Name.LocalName == "CompileUsingReferenceAssemblies");
        Assert.Equal("false", policy.Value);
    }

    [Fact]
    public void CoreDoesNotOwnRuntimeSpecificPackageAssets()
    {
        var projectPath = Path.Combine(RepositoryRoot, "DownKyi.Core", "DownKyi.Core.csproj");
        var project = XDocument.Load(projectPath);
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName == "RuntimeIdentifier");

        var source = File.ReadAllText(projectPath);
        Assert.DoesNotContain("DownKyiAssetRuntimeIdentifier", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeInformation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeAssetSelectionIsOwnedByTheExecutable()
    {
        var executable = Read("DownKyi/DownKyi.csproj");
        var desktop = Read("src/DownKyi.Desktop/DownKyi.Desktop.csproj");

        Assert.Contains("DownKyiAssetRuntimeIdentifier", executable, StringComparison.Ordinal);
        Assert.Contains("RuntimeInformation", executable, StringComparison.Ordinal);
        Assert.Contains("..\\DownKyi.Core\\Binary\\$(DownKyiAssetRuntimeIdentifier)\\aria2\\*", executable, StringComparison.Ordinal);
        Assert.Contains("..\\DownKyi.Core\\Binary\\$(DownKyiAssetRuntimeIdentifier)\\ffmpeg\\*", executable, StringComparison.Ordinal);
        Assert.DoesNotContain("DownKyiAssetRuntimeIdentifier", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalAssetManifestPinsUrlsAndSha256Digests()
    {
        using var manifest = JsonDocument.Parse(Read("script/assets/external-assets.json"));
        foreach (var tool in manifest.RootElement.EnumerateObject())
        {
            Assert.NotEmpty(tool.Value.GetProperty("version").GetString() ?? string.Empty);
            foreach (var asset in tool.Value.GetProperty("assets").EnumerateObject())
            {
                Assert.StartsWith("https://", asset.Value.GetProperty("url").GetString() ?? string.Empty, StringComparison.Ordinal);
                Assert.Matches("^[a-f0-9]{64}$", asset.Value.GetProperty("sha256").GetString() ?? string.Empty);
            }
        }
    }

    [Fact]
    public void ExternalAssetInstallersUseTheSharedRetryOwnerAndAvoidInsecureCurl()
    {
        var scripts = new[]
        {
            Read("script/ffmpeg.ps1"),
            Read("script/aria2.ps1"),
            Read("script/ffmpeg.sh"),
            Read("script/aria2.sh")
        };

        Assert.All(scripts, source =>
        {
            Assert.Contains("external-assets.json", source, StringComparison.Ordinal);
            Assert.DoesNotContain("curl -k", source, StringComparison.Ordinal);
            Assert.DoesNotContain("curl --insecure", source, StringComparison.Ordinal);
        });
        Assert.Contains("download-external-asset.ps1", scripts[0], StringComparison.Ordinal);
        Assert.Contains("download-external-asset.ps1", scripts[1], StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the BiliCinema repository root.");
    }
}
