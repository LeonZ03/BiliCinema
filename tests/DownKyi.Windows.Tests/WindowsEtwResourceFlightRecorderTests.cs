using System.Diagnostics;
using System.Globalization;
using DownKyi.TestInfrastructure;
using CentralRunnerProgram = DownKyi.CentralTestRunner.Program;

namespace DownKyi.Windows.Tests;

public sealed class WindowsEtwResourceFlightRecorderTests
{
    [Fact]
    public void DiagnosticToolDrainsStandardOutputAndErrorConcurrently()
    {
        var result = WindowsEtwResourceFlightRecorder.RunTool(
            "dotnet",
            CreateFixtureArguments("fixture-dual-output"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(256 * 1024, result.Output.Length);
    }

    [Fact]
    public void DiagnosticToolTerminatesPipeHoldingDescendantAfterRootExit()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-etw-tool-child-{Guid.NewGuid():N}.pid");
        var releaseName = $"Local\\downkyi-etw-tool-release-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            releaseName);
        int? childPid = null;
        try
        {
            var childWasAliveBeforeRelease = false;
            var result = WindowsEtwResourceFlightRecorder.RunToolAfterReadiness(
                "dotnet",
                () =>
                {
                    childPid = ReadMarkerAsync(marker).GetAwaiter().GetResult();
                    childWasAliveBeforeRelease = IsAlive(childPid.Value);
                    release.Set();
                },
                CreateFixtureArguments(
                    "fixture-exit-with-pipe-holder",
                    RuntimeConfigPath,
                    marker,
                    releaseName));

            Assert.True(childPid.HasValue, "The pipe-holder PID marker was not published.");
            Assert.True(childWasAliveBeforeRelease, "The pipe-holder exited before the root was released.");
            Assert.Equal(0, result.ExitCode);
            Assert.False(IsAlive(childPid.Value));
        }
        finally
        {
            release.Set();
            if (childPid is { } pid && IsAlive(pid))
            {
                using var child = Process.GetProcessById(pid);
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }

            File.Delete(marker);
        }
    }

    private static async Task<int> ReadMarkerAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            try
            {
                if (File.Exists(path) && int.TryParse(
                        await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
                            .ConfigureAwait(false),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var processId))
                {
                    return processId;
                }
            }
            catch (IOException)
            {
                // The fixture has created the marker but has not closed its write handle.
            }

            await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"The fixture did not publish {Path.GetFileName(path)}.");
    }

    private static string RuntimeConfigPath => Path.Combine(
        AppContext.BaseDirectory,
        $"{Path.GetFileNameWithoutExtension(typeof(WindowsEtwResourceFlightRecorderTests).Assembly.Location)}.runtimeconfig.json");

    private static string[] CreateFixtureArguments(string mode, params string[] arguments)
    {
        var result = new List<string>
        {
            "exec",
            "--runtimeconfig",
            RuntimeConfigPath,
            typeof(CentralRunnerProgram).Assembly.Location,
            mode
        };
        result.AddRange(arguments);
        return [.. result];
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
