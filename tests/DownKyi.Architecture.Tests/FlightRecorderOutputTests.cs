using System.Diagnostics;
using System.Text.Json;
using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class FlightRecorderOutputTests
{
    private const string DiscardedLineMarker = "[output line exceeded 8192 characters and was discarded]";

    [Fact]
    public async Task UnterminatedOversizedOutputIsDiscardedWithoutUnboundedEvidence()
    {
        const string secret = "fixture-long-line-secret";
        var evidenceDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-flight-recorder-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceDirectory);
        try
        {
            var result = await FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.long-line.slice",
                    "fixture.long-line.test",
                    CreateFixtureStartInfo(secret),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory),
                CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            var artifact = await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(secret, artifact, StringComparison.Ordinal);
            Assert.True(new FileInfo(result.EvidencePath).Length < 16_384);
            using var document = JsonDocument.Parse(artifact);
            Assert.Contains(
                DiscardedLineMarker,
                document.RootElement.GetProperty("StdoutTail").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingRootStartTimeDoesNotOverrideExitCodeOrOutputDrain()
    {
        const string secret = "fixture-root-identity-secret";
        var evidenceDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-flight-recorder-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceDirectory);
        try
        {
            var result = await FlightRecorderExecution.RunAsync(
                new ProcessExecutionRequest(
                    "fixture.identity-failure.slice",
                    "fixture.identity-failure.test",
                    CreateFixtureStartInfo(secret),
                    TimeSpan.FromSeconds(3),
                    evidenceDirectory,
                    RootStartTimeReader: _ => throw new System.ComponentModel.Win32Exception(
                        "fixture start time unavailable")),
                CancellationToken.None);

            Assert.Equal(1, result.ExitCode);
            Assert.Null(result.RootStartTimeUtc);
            var artifact = await File.ReadAllTextAsync(
                result.EvidencePath,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(secret, artifact, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(artifact);
            var rootProcess = document.RootElement.GetProperty("RootProcess");
            Assert.Equal(result.RootPid, rootProcess.GetProperty("Pid").GetInt32());
            Assert.False(rootProcess.TryGetProperty("StartTimeUtc", out _));
            Assert.Equal("process_exit", document.RootElement.GetProperty("Outcome").GetString());
            Assert.Contains(
                document.RootElement.GetProperty("Events").EnumerateArray(),
                item => string.Equals(
                    item.GetProperty("Event").GetString(),
                    "root_start_time_unavailable",
                    StringComparison.Ordinal));
            Assert.Contains(
                DiscardedLineMarker,
                document.RootElement.GetProperty("StdoutTail").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(evidenceDirectory, recursive: true);
        }
    }

    private static ProcessStartInfo CreateFixtureStartInfo(string secret, string? readyPath = null)
    {
        var startInfo = TestFixtureProcess.CreateStartInfo("fixture-long-line", secret);
        if (readyPath is not null)
        {
            startInfo.ArgumentList.Add(readyPath);
        }
        return startInfo;
    }
}
