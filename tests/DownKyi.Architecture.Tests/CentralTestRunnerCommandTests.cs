using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerCommandTests
{
    [Fact]
    public void CommandOptionsRejectsRemovedPerProjectTimeout()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CommandOptions.Parse(["--timeout-seconds", "300"]));

        Assert.Contains("Unknown option", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSolutionRejectsEmptyProjectDiscovery()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => RunSolutionAsync(repositoryRoot));

            Assert.Contains("No runnable test projects", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunSolutionRejectsEmptyCurrentPlatformSelection()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            var unsupportedPlatform = OperatingSystem.IsWindows() ? "Linux" : "Windows";
            await WriteProjectAsync(repositoryRoot, "Fixture.Tests", unsupportedPlatform, failBuild: false);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => RunSolutionAsync(repositoryRoot));

            Assert.Contains("No test projects support", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunSolutionClearsEverySelectedTrxBeforeFirstBuildFailure()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            await WriteProjectAsync(
                repositoryRoot,
                "A.Tests",
                "Windows;Linux;macOS",
                failBuild: true);
            await WriteProjectAsync(
                repositoryRoot,
                "Z.Tests",
                "Windows;Linux;macOS",
                failBuild: false);
            var resultsDirectory = Path.Combine(repositoryRoot, "results");
            Directory.CreateDirectory(resultsDirectory);
            var firstTrx = Path.Combine(resultsDirectory, "A.Tests.trx");
            var laterTrx = Path.Combine(resultsDirectory, "Z.Tests.trx");
            await File.WriteAllTextAsync(firstTrx, "stale-pass", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(laterTrx, "stale-pass", TestContext.Current.CancellationToken);

            var exitCode = await RunSolutionAsync(repositoryRoot, resultsDirectory);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(firstTrx));
            Assert.False(File.Exists(laterTrx));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    private static Task<int> RunSolutionAsync(string repositoryRoot, string? resultsDirectory = null)
    {
        var arguments = new List<string>
        {
            "run-solution",
            "--repository-root", repositoryRoot,
            "--configuration", "Release",
            "--no-restore"
        };
        if (resultsDirectory is not null)
        {
            arguments.Add("--results-directory");
            arguments.Add(resultsDirectory);
        }

        return CentralTestCommand.RunAsync(arguments.ToArray(), TestContext.Current.CancellationToken);
    }

    private static async Task<string> CreateRepositoryAsync()
    {
        var repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-central-runner-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "tests"));
        var policyDirectory = Path.Combine(repositoryRoot, "docs", "testing");
        Directory.CreateDirectory(policyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(policyDirectory, "test-runner-policy.json"),
            """{"schemaVersion":1,"projects":[]}""",
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        return repositoryRoot;
    }

    private static Task WriteProjectAsync(
        string repositoryRoot,
        string projectName,
        string platforms,
        bool failBuild)
    {
        var projectDirectory = Path.Combine(repositoryRoot, "tests", projectName);
        Directory.CreateDirectory(projectDirectory);
        var buildTarget = failBuild
            ? "<Error Text=\"intentional first project failure\" />"
            : string.Empty;
        return File.WriteAllTextAsync(
            Path.Combine(projectDirectory, $"{projectName}.csproj"),
            $$"""
            <Project DefaultTargets="Build">
              <PropertyGroup>
                <DownKyiTestPlatforms>{{platforms}}</DownKyiTestPlatforms>
              </PropertyGroup>
              <Target Name="Build">
                {{buildTarget}}
              </Target>
            </Project>
            """,
            TestContext.Current.CancellationToken);
    }

}
