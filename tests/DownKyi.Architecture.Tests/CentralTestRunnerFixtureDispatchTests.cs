using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerFixtureDispatchTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void FixtureCommandNamesRemainStable()
    {
        var runnerDirectory = Path.Combine(RepositoryRoot, "tools", "DownKyi.CentralTestRunner");
        var commands = Directory.EnumerateFiles(runnerDirectory, "*.cs")
            .SelectMany(File.ReadAllLines)
            .SelectMany(line => Regex.Matches(
                line,
                @"^\s*""(?<command>fixture-[a-z-]+)""\s*=>",
                RegexOptions.CultureInvariant))
            .Select(match => match.Groups["command"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "fixture-directory-lock",
                "fixture-dual-output",
                "fixture-exit-with-pipe-holder",
                "fixture-hold",
                "fixture-hold-marker",
                "fixture-large-launch",
                "fixture-long-line",
                "fixture-pass",
                "fixture-sensitive-hold",
                "fixture-stderr-hold",
                "fixture-tree-child",
                "fixture-tree-root",
                "fixture-windows-process-snapshot"
            ],
            commands);
    }

    [Fact]
    public async Task ImmediateFixtureAndCommandExitContractIsStable()
    {
        var pass = await RunAsync("fixture-pass").ConfigureAwait(true);
        Assert.Equal(0, pass.ExitCode);
        Assert.Matches("^fixture-pass pid=[0-9]+\\r?\\n$", pass.StandardOutput);
        Assert.Empty(pass.StandardError);

        const string marker = "fixture-output-marker";
        var longLine = await RunAsync("fixture-long-line", marker).ConfigureAwait(true);
        Assert.Equal(1, longLine.ExitCode);
        Assert.StartsWith($"token={marker}", longLine.StandardOutput, StringComparison.Ordinal);
        Assert.Equal($"token={marker}".Length + 32768, longLine.StandardOutput.Length);
        Assert.Empty(longLine.StandardError);

        var invalid = await RunAsync("not-a-runner-command").ConfigureAwait(true);
        Assert.Equal(2, invalid.ExitCode);
        Assert.Empty(invalid.StandardOutput);
        Assert.Contains("Unknown command: not-a-runner-command", invalid.StandardError, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunAsync(params string[] arguments)
    {
        var startInfo = TestFixtureProcess.CreateStartInfo(arguments);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), standardOutput, standardError)
                .WaitAsync(deadline.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            var stillRunning = !process.HasExited;
            var outputComplete = standardOutput.IsCompleted;
            var errorComplete = standardError.IsCompleted;
            if (stillRunning)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new TimeoutException($"Central test fixture did not exit: {string.Join(' ', arguments)}; "
                                       + $"running={stillRunning}, stdout={outputComplete}, stderr={errorComplete}");
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(true),
            await standardError.ConfigureAwait(true));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DownKyi.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
