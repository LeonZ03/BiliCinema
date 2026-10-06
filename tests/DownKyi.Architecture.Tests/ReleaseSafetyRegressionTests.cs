namespace DownKyi.Architecture.Tests;

public sealed class ReleaseSafetyRegressionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void BiliCinemaBuildPublishesOneSelfContainedWindowsExecutable()
    {
        var build = Read("script/build-bilicinema.ps1");

        Assert.Contains("'publish'", build, StringComparison.Ordinal);
        Assert.Contains("'-c', 'BiliCinema', '-r', 'win-x64'", build, StringComparison.Ordinal);
        Assert.Contains("'--self-contained', 'true'", build, StringComparison.Ordinal);
        Assert.Contains("'-p:PublishSingleFile=true'", build, StringComparison.Ordinal);
        Assert.Contains("'-p:IncludeAllContentForSelfExtract=true'", build, StringComparison.Ordinal);
        Assert.Contains("'-p:PublishTrimmed=false'", build, StringComparison.Ordinal);
        Assert.Contains("BiliCinema.exe", build, StringComparison.Ordinal);
        Assert.Contains("Unexpected loose publish files", build, StringComparison.Ordinal);
        Assert.DoesNotContain("Compress-Archive", build, StringComparison.Ordinal);
        Assert.DoesNotContain("7z", build, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleFileBuildStagesOnlyChecksumVerifiedRuntimeTools()
    {
        var build = Read("script/build-bilicinema.ps1");

        Assert.Contains("external-assets.json", build, StringComparison.Ordinal);
        Assert.Contains("Assert-Hash", build, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", build, StringComparison.Ordinal);
        Assert.Contains("src\\DownKyi.Desktop\\EmbeddedTools", build, StringComparison.Ordinal);
        Assert.Contains("cloudflaredHash", build, StringComparison.Ordinal);
        Assert.Contains("aria2c.exe", build, StringComparison.Ordinal);
        Assert.Contains("ffmpeg.exe", build, StringComparison.Ordinal);
        Assert.Contains("ffprobe.exe", build, StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequestQualityGateBuildsAndTestsTheCurrentSolution()
    {
        var workflow = Read(".github/workflows/quality.yml");
        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("./script/test-solution.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:AnalysisMode=All", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:TreatWarningsAsErrors=true", workflow, StringComparison.Ordinal);
        Assert.Contains("name: Package audit", workflow, StringComparison.Ordinal);
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
