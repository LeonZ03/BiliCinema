using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using DownKyi.CentralTestRunner;
using DownKyi.ProcessSupervision;
using DownKyi.Services.Updates;

namespace DownKyi.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateHelperTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RunningParentPreventsExecutableReplacement()
    {
        var fixture = await UpdateFixture.CreateAsync();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync();
        await fixture.WaitForReadyAsync();

        Assert.Equal(UpdateFixture.Original, await File.ReadAllBytesAsync(fixture.State.TargetPath,
            TestContext.Current.CancellationToken));
        Assert.False(fixture.Helper!.HasExited);
        Assert.False(File.Exists(fixture.State.BackupPath));
    }

    [Fact]
    public async Task HelperRechecksHashBeforeReplacingInstalledExecutable()
    {
        var fixture = await UpdateFixture.CreateAsync();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        fixture.State.ParentProcessId = int.MaxValue;
        fixture.State.ExpectedSha256 = new string('0', 64);
        await fixture.SaveAsync();
        await fixture.StartAsync();
        await fixture.WaitForExitAsync();

        Assert.Equal(UpdateFixture.Original, await File.ReadAllBytesAsync(fixture.State.TargetPath,
            TestContext.Current.CancellationToken));
        Assert.Equal("failed", await ReadPhaseWithDiagnosticsAsync(fixture));
        Assert.Equal("checksum_mismatch", (await fixture.ReadJournalAsync()).FailureCode);
        Assert.False(File.Exists(fixture.State.BackupPath));
    }

    [Fact]
    public async Task LaunchFailureRestoresPreviousExecutable()
    {
        var fixture = await UpdateFixture.CreateAsync();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        fixture.State.ParentProcessId = int.MaxValue;
        await fixture.SaveAsync();
        await fixture.StartAsync();
        await fixture.WaitForExitAsync();

        Assert.Equal(UpdateFixture.Original, await File.ReadAllBytesAsync(fixture.State.TargetPath,
            TestContext.Current.CancellationToken));
        Assert.Equal("rolled-back", await ReadPhaseWithDiagnosticsAsync(fixture));
        Assert.Equal("launch_updated", (await fixture.ReadJournalAsync()).FailureStep);
        Assert.Equal("restart_previous_failed", (await fixture.ReadJournalAsync()).RecoveryFailureCode);
        Assert.True(File.Exists(fixture.State.PackagePath));
    }

    [Theory]
    [InlineData(0, "completed", "BiliCinema.exe")]
    [InlineData(17, "rolled-back", "BiliCinema.exe")]
    [InlineData(0, "completed", "BiliCinema-1.5.1-win-x64.exe")]
    [InlineData(17, "rolled-back", "BiliCinema-1.5.1-win-x64.exe")]
    public async Task FastApplicationExitRollsBackOnlyWhenExitCodeIndicatesFailure(int exitCode, string expectedPhase, string targetName)
    {
        var fixture = await UpdateFixture.CreateAsync(targetName);
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        await fixture.PrepareExecutablePairAsync(exitCode);
        var previous = await File.ReadAllBytesAsync(fixture.State.TargetPath, TestContext.Current.CancellationToken);
        var update = await File.ReadAllBytesAsync(fixture.State.PackagePath, TestContext.Current.CancellationToken);
        await fixture.StartAsync();
        await fixture.WaitForExitAsync();

        Assert.Equal(expectedPhase, await ReadPhaseWithDiagnosticsAsync(fixture));
        Assert.Equal(exitCode == 0 ? update : previous,
            await File.ReadAllBytesAsync(fixture.State.TargetPath, TestContext.Current.CancellationToken));
        Assert.Single(await File.ReadAllLinesAsync(fixture.UpdatedLaunchMarker, TestContext.Current.CancellationToken));
        if (exitCode == 0)
        {
            Assert.False(File.Exists(fixture.PreviousLaunchMarker));
            Assert.Equal(previous,
                await File.ReadAllBytesAsync(fixture.State.BackupPath, TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Equal("startup_failed", (await fixture.ReadJournalAsync()).FailureCode);
            await fixture.WaitForPreviousLaunchAsync();
            Assert.Single(await File.ReadAllLinesAsync(fixture.PreviousLaunchMarker, TestContext.Current.CancellationToken));
            Assert.Equal(update,
                await File.ReadAllBytesAsync(fixture.State.PackagePath, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task FixtureCleanupOwnsRestartedApplicationAfterHelperHasExited()
    {
        var fixture = await UpdateFixture.CreateAsync("BiliCinema-1.5.1-win-x64.exe");
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        await fixture.PrepareExecutablePairAsync(17, holdPrevious: true);
        await fixture.StartAsync();
        await fixture.WaitForExitAsync();
        Assert.Equal("rolled-back", await ReadPhaseWithDiagnosticsAsync(fixture));

        var pid = await fixture.WaitForPreviousLaunchAsync(waitForExit: false);
        using (var previous = Process.GetProcessById(pid))
        {
            Assert.False(previous.HasExited);
            await Assert.ThrowsAsync<DirectoryResourceRundownTimeoutException>(() =>
                WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    fixture.State.TargetPath, TimeSpan.Zero, requireWriteAccess: true));
        }

        await fixture.DisposeAsync();
        Assert.False(File.Exists(fixture.State.TargetPath));
    }

    [Fact]
    public async Task PreviousLaunchReadinessAcceptsCompleteRecordWhilePublisherStillOwnsWriteHandle()
    {
        var fixture = await UpdateFixture.CreateAsync();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var publisher = new FileStream(fixture.PreviousLaunchMarker, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read);
        var record = System.Text.Encoding.UTF8.GetBytes(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
        await publisher.WriteAsync(record, TestContext.Current.CancellationToken);
        await publisher.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Environment.ProcessId, await fixture.WaitForPreviousLaunchAsync(waitForExit: false));
    }

    private async Task<string> ReadPhaseWithDiagnosticsAsync(UpdateFixture fixture)
    {
        var phase = await fixture.ReadPhaseAsync().ConfigureAwait(false);
        output.WriteLine(await fixture.ReadDiagnosticsAsync().ConfigureAwait(false));
        return phase;
    }

    private sealed class UpdateFixture : IAsyncDisposable
    {
        internal static readonly byte[] Original = [77, 90, 1, 2, 3];
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bilicinema update's test-{Guid.NewGuid():N}");
        private Task<string>? _output;
        private Task<string>? _error;
        private OwnedProcessScope? _scope;
        internal WindowsUpdateJournal State { get; } = new();
        internal Process? Helper { get; private set; }
        private string Journal => Path.Combine(_directory, ".bilicinema-update-test", "update.json");
        internal string UpdatedLaunchMarker => Path.Combine(_directory, "updated-launch.txt");
        internal string PreviousLaunchMarker => Path.Combine(_directory, "previous-launch.txt");

        internal async Task PrepareExecutablePairAsync(int updateExitCode, bool holdPrevious = false)
        {
            await CompileExecutableAsync(State.TargetPath, PreviousLaunchMarker, 0, holdPrevious).ConfigureAwait(false);
            await CompileExecutableAsync(State.PackagePath, UpdatedLaunchMarker, updateExitCode).ConfigureAwait(false);
            State.ExpectedSha256 = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(State.PackagePath, TestContext.Current.CancellationToken).ConfigureAwait(false)));
            State.ParentProcessId = int.MaxValue;
            await SaveAsync().ConfigureAwait(false);
        }

        private static async Task CompileExecutableAsync(string target, string marker, int exitCode, bool hold = false)
        {
            var source = target + ".cs";
            var body = "using System.IO; internal static class Program { private static int Main() { using (var process = System.Diagnostics.Process.GetCurrentProcess()) { File.AppendAllText("
                + JsonSerializer.Serialize(marker) + ", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"\\n\"); } "
                + (hold ? "using (var lifetime = new System.Threading.ManualResetEvent(false)) { lifetime.WaitOne(); } " : string.Empty)
                + "return "
                + exitCode.ToString(CultureInfo.InvariantCulture) + "; } }";
            await File.WriteAllTextAsync(source, body, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.Is64BitOperatingSystem ? "Microsoft.NET/Framework64" : "Microsoft.NET/Framework",
                "v4.0.30319", "csc.exe");
            var info = new ProcessStartInfo(compiler)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "/nologo", "/target:winexe", "/out:" + target, source })
            {
                info.ArgumentList.Add(argument);
            }

            using var compilation = Process.Start(info)
                ?? throw new InvalidOperationException("The executable fixture compiler did not start.");
            var stdout = compilation.StandardOutput.ReadToEndAsync();
            var stderr = compilation.StandardError.ReadToEndAsync();
            try
            {
                await compilation.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                await stdout.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                Assert.Equal(0, compilation.ExitCode);
            }
            finally
            {
                if (!compilation.HasExited)
                {
                    compilation.Kill(entireProcessTree: true);
                    await compilation.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        internal async Task<int> WaitForPreviousLaunchAsync(bool waitForExit = true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var pid = 0;
            while (pid <= 0)
            {
                if (File.Exists(PreviousLaunchMarker))
                {
                    // File creation precedes the publisher's final flush and close.
                    // Share its write handle and require a complete PID record.
                    using var stream = new FileStream(PreviousLaunchMarker, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
                    using var reader = new StreamReader(stream);
                    var marker = await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                    if (marker.EndsWith('\n'))
                        int.TryParse(marker.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid);
                }

                if (pid > 0) break;
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }

            if (!waitForExit) return pid;

            try
            {
                using var previous = Process.GetProcessById(pid);
                await previous.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // The short-lived previous executable may exit before its handle is opened.
            }

            return pid;
        }

        internal static async Task<UpdateFixture> CreateAsync(string targetName = "BiliCinema.exe")
        {
            var fixture = new UpdateFixture();
            try
            {
                var staging = Path.GetDirectoryName(fixture.Journal)!;
                Directory.CreateDirectory(staging);
                var state = fixture.State;
                state.ParentProcessId = Environment.ProcessId;
                state.TargetPath = Path.Combine(fixture._directory, targetName);
                state.PackagePath = Path.Combine(staging, "BiliCinema.exe");
                state.BackupPath = Path.Combine(staging, "previous.exe");
                state.HelperPath = Path.Combine(staging, "install.ps1");
                state.HelperReadyPath = Path.Combine(staging, "helper-ready");
                byte[] package = [77, 90, 4, 5, 6]; // Deliberately invalid PE: process launch must fail.
                state.ExpectedSha256 = Convert.ToHexString(SHA256.HashData(package));
                state.Phase = "ready";
                await fixture.SaveAsync().ConfigureAwait(false);
                await File.WriteAllBytesAsync(state.TargetPath, Original, TestContext.Current.CancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(state.PackagePath, package, TestContext.Current.CancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(state.HelperPath, WindowsUpdateHelper.Script, TestContext.Current.CancellationToken).ConfigureAwait(false);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal Task SaveAsync() => File.WriteAllTextAsync(Journal,
            JsonSerializer.Serialize(State, WindowsUpdateJsonContext.Default.WindowsUpdateJournal),
            TestContext.Current.CancellationToken);

        internal async Task StartAsync()
        {
            var info = WindowsUpdateService.CreateHelperStartInfo(State.HelperPath, Journal);
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            // The helper may exit while its restarted application is still alive.
            // The existing OS scope owns that descendant through fixture cleanup.
            _scope = await OwnedProcessScope.StartAsync(info, TestContext.Current.CancellationToken).ConfigureAwait(false);
            _output = _scope.Host.StandardOutput.ReadToEndAsync();
            _error = _scope.Host.StandardError.ReadToEndAsync();
            Helper = _scope.Host;
        }

        internal async Task WaitForReadyAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (!File.Exists(State.HelperReadyPath))
            {
                Assert.False(Helper!.HasExited);
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }

        internal async Task WaitForExitAsync() => await Helper!.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(false);

        internal async Task<string> ReadPhaseAsync()
        {
            var journal = await ReadJournalAsync().ConfigureAwait(false);
            return journal.Phase;
        }

        internal async Task<WindowsUpdateJournal> ReadJournalAsync()
        {
            var content = await File.ReadAllTextAsync(Journal, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var journal = JsonSerializer.Deserialize(content, WindowsUpdateJsonContext.Default.WindowsUpdateJournal);
            return journal!;
        }

        internal Task<string> ReadDiagnosticsAsync() => _error ?? Task.FromResult(string.Empty);

        public async ValueTask DisposeAsync()
        {
            var deadline = new CleanupDeadline(TimeSpan.FromSeconds(10));
            if (_scope != null)
            {
                try
                {
                    await _scope.TerminateAsync(deadline).ConfigureAwait(false);
                    await _scope.Host.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(deadline.Remaining).ConfigureAwait(false);
                    if (_output != null) await _output.ConfigureAwait(false);
                    if (_error != null) await _error.ConfigureAwait(false);
                }
                finally
                {
                    Helper = null;
                    _scope.Dispose();
                    _scope = null;
                }
            }

            if (Directory.Exists(_directory))
            {
                foreach (var executable in new[] { State.TargetPath, State.PackagePath, State.BackupPath })
                {
                    await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                        executable, deadline.Remaining, requireWriteAccess: true).ConfigureAwait(false);
                }

                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(_directory, deadline.Remaining)
                    .ConfigureAwait(false);
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
