using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacBundleLayoutTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void NonCodePublishFilesMoveToResourcesWithoutBreakingDotNetHost()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-layout-{Guid.NewGuid():N}");
        var publishDirectory = Path.Combine(fixtureRoot, "publish");
        var legacyApp = Path.Combine(fixtureRoot, "Legacy.app");
        var correctedApp = Path.Combine(fixtureRoot, "Corrected.app");

        Directory.CreateDirectory(fixtureRoot);

        try
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException(
                    $"Unsupported macOS test architecture: {RuntimeInformation.ProcessArchitecture}")
            };

            var probeProject = Path.Combine(
                RepositoryRoot,
                "script",
                "macos",
                "fixtures",
                "BundleProbe",
                "BundleProbe.csproj");
            AssertSuccess(Run(
                "dotnet",
                fixtureRoot,
                "publish",
                probeProject,
                "-c",
                "Release",
                "-r",
                $"osx-{architecture}",
                "--self-contained",
                "-p:DebugType=None",
                "-p:DebugSymbols=false",
                "-o",
                publishDirectory));

            CreateAppBundle(legacyApp, publishDirectory);
            var legacyRuntimeConfig = Path.Combine(
                legacyApp,
                "Contents",
                "MacOS",
                "BundleProbe.runtimeconfig.json");
            var legacyDeps = Path.Combine(
                legacyApp,
                "Contents",
                "MacOS",
                "BundleProbe.deps.json");
            Assert.True(File.Exists(legacyRuntimeConfig));
            Assert.Null(new FileInfo(legacyRuntimeConfig).LinkTarget);
            Assert.True(File.Exists(legacyDeps));
            Assert.Null(new FileInfo(legacyDeps).LinkTarget);

            var legacySigning = RunSigningScript(legacyApp);
            Assert.NotEqual(0, legacySigning.ExitCode);
            var legacyOutput = legacySigning.StandardOutput + legacySigning.StandardError;
            Assert.Contains("runtime checksum path must remain a symlink", legacyOutput, StringComparison.Ordinal);

            CreateAppBundle(correctedApp, publishDirectory);
            AssertSuccess(Run(
                "/bin/bash",
                RepositoryRoot,
                Path.Combine(RepositoryRoot, "script", "macos", "prepare-app-layout.sh"),
                correctedApp));

            var runtimeConfigLink = Path.Combine(
                correctedApp,
                "Contents",
                "MacOS",
                "BundleProbe.runtimeconfig.json");
            var depsLink = Path.Combine(
                correctedApp,
                "Contents",
                "MacOS",
                "BundleProbe.deps.json");
            Assert.NotNull(new FileInfo(runtimeConfigLink).LinkTarget);
            Assert.NotNull(new FileInfo(depsLink).LinkTarget);
            Assert.True(File.Exists(Path.Combine(
                correctedApp,
                "Contents",
                "Resources",
                "dotnet",
                "BundleProbe.runtimeconfig.json")));

            AssertSuccess(RunSigningScript(correctedApp));
            AssertAdHocSignatureDoesNotEnableHardenedRuntime(correctedApp);
            AssertAdHocSignatureDoesNotEnableHardenedRuntime(Path.Combine(
                correctedApp,
                "Contents",
                "MacOS",
                "BundleProbe"));
            AssertAdHocSignatureDoesNotEnableHardenedRuntime(Path.Combine(
                correctedApp,
                "Contents",
                "MacOS",
                "libhostfxr.dylib"));
            AssertSuccess(Run(
                "/bin/bash",
                RepositoryRoot,
                Path.Combine(RepositoryRoot, "script", "macos", "verify-app-signature.sh"),
                correctedApp));

            var launch = Run(
                Path.Combine(correctedApp, "Contents", "MacOS", "BundleProbe"),
                fixtureRoot);
            AssertSuccess(launch);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void AppLoaderVerificationReportsRuntimeLoaderExit130()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-loader-failure-{Guid.NewGuid():N}");
        var appPath = Path.Combine(fixtureRoot, "Test.app");
        var executableDirectory = Path.Combine(appPath, "Contents", "MacOS");
        var executablePath = Path.Combine(executableDirectory, "TestApp");
        var runtimeDirectory = Path.Combine(appPath, "Contents", "Resources", "dotnet");
        Directory.CreateDirectory(executableDirectory);
        Directory.CreateDirectory(runtimeDirectory);

        try
        {
            WriteInfoPlist(appPath, "TestApp", "cn.bzdrs.downkyi.loader-failure");
            File.WriteAllText(Path.Combine(runtimeDirectory, "libcoreclr.dylib"), "fixture");
            File.CreateSymbolicLink(
                Path.Combine(executableDirectory, "libcoreclr.dylib"),
                "../Resources/dotnet/libcoreclr.dylib");
            File.WriteAllText(
                executablePath,
                "#!/bin/bash\necho 'Failed to load libhostfxr.dylib' >&2\necho 'mapping process and mapped file (non-platform) have different Team IDs' >&2\nexit 130\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AssertSuccess(Run("/bin/chmod", fixtureRoot, "+x", executablePath));

            var result = Run(
                "/bin/bash",
                RepositoryRoot,
                Path.Combine(RepositoryRoot, "script", "macos", "verify-app-loader.sh"),
                appPath);

            Assert.NotEqual(0, result.ExitCode);
            var output = result.StandardOutput + result.StandardError;
            Assert.Contains("status 130", output, StringComparison.Ordinal);
            Assert.Contains("libhostfxr.dylib", output, StringComparison.Ordinal);
            Assert.Contains("different Team IDs", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void AppLoaderVerificationObservesMappedCoreClrAndCleansUpProbe()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-loader-{Guid.NewGuid():N}");
        var appPath = Path.Combine(fixtureRoot, "Test.app");
        var executableDirectory = Path.Combine(appPath, "Contents", "MacOS");
        var executablePath = Path.Combine(executableDirectory, "TestApp");
        var runtimeDirectory = Path.Combine(appPath, "Contents", "Resources", "dotnet");
        var runtimePath = Path.Combine(runtimeDirectory, "libcoreclr.dylib");
        var librarySource = Path.Combine(fixtureRoot, "runtime.c");
        var executableSource = Path.Combine(fixtureRoot, "app.c");
        var pidMarker = Path.Combine(fixtureRoot, "app.pid");
        Directory.CreateDirectory(executableDirectory);
        Directory.CreateDirectory(runtimeDirectory);

        try
        {
            WriteInfoPlist(appPath, "TestApp", "cn.bzdrs.downkyi.loader-probe");
            File.WriteAllText(
                librarySource,
                "int downkyi_fixture_runtime(void) { return 0; }\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AssertSuccess(Run(
                "/usr/bin/clang",
                fixtureRoot,
                "-dynamiclib",
                "-Wl,-install_name,@rpath/libcoreclr.dylib",
                "-o",
                runtimePath,
                librarySource));
            File.CreateSymbolicLink(
                Path.Combine(executableDirectory, "libcoreclr.dylib"),
                "../Resources/dotnet/libcoreclr.dylib");

            File.WriteAllText(
                executableSource,
                "#include <signal.h>\n#include <stdio.h>\n#include <stdlib.h>\n#include <unistd.h>\nextern int downkyi_fixture_runtime(void);\nint main(void) {\n  if (downkyi_fixture_runtime() != 0) return 2;\n  const char *marker = getenv(\"DOWNKYI_PID_MARKER\");\n  if (marker == NULL) return 3;\n  FILE *file = fopen(marker, \"w\");\n  if (file == NULL) return 4;\n  fprintf(file, \"%d\", getpid());\n  if (fclose(file) != 0) return 5;\n  signal(SIGTERM, SIG_IGN);\n  for (;;) pause();\n}\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AssertSuccess(Run(
                "/usr/bin/clang",
                fixtureRoot,
                executableSource,
                "-L",
                runtimeDirectory,
                "-lcoreclr",
                "-Wl,-rpath,@executable_path/../Resources/dotnet",
                "-o",
                executablePath));

            var result = Run(
                "/bin/bash",
                RepositoryRoot,
                new Dictionary<string, string?>
                {
                    ["DOWNKYI_PID_MARKER"] = pidMarker
                },
                Path.Combine(RepositoryRoot, "script", "macos", "verify-app-loader.sh"),
                appPath);

            AssertSuccess(result);
            Assert.Contains("mapped bundled libcoreclr.dylib", result.StandardOutput, StringComparison.Ordinal);
            var pid = File.ReadAllText(pidMarker).Trim();
            Assert.Matches("^[0-9]+$", pid);
            AssertSuccess(Run("/bin/bash", fixtureRoot, "-c", $"! kill -0 {pid} 2>/dev/null"));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void BundleLaunchVerificationUsesNativeFinishedLaunchingAndTerminationStates()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-bundle-launch-{Guid.NewGuid():N}");
        var appPath = Path.Combine(fixtureRoot, "NativeFixture.app");
        var executableDirectory = Path.Combine(appPath, "Contents", "MacOS");
        var executablePath = Path.Combine(executableDirectory, "NativeFixture");
        var sourcePath = Path.Combine(fixtureRoot, "NativeFixture.m");
        Directory.CreateDirectory(executableDirectory);

        try
        {
            WriteInfoPlist(appPath, "NativeFixture", $"cn.bzdrs.downkyi.native-fixture-{Guid.NewGuid():N}");
            File.WriteAllText(
                sourcePath,
                "#import <AppKit/AppKit.h>\n@interface FixtureDelegate : NSObject <NSApplicationDelegate>\n@end\n@implementation FixtureDelegate\n- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication *)sender { return NSTerminateNow; }\n@end\nint main(int argc, const char *argv[]) {\n  @autoreleasepool {\n    NSApplication *application = [NSApplication sharedApplication];\n    FixtureDelegate *delegate = [FixtureDelegate new];\n    application.delegate = delegate;\n    [application setActivationPolicy:NSApplicationActivationPolicyProhibited];\n    [application run];\n    (void)delegate;\n  }\n  return 0;\n}\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AssertSuccess(Run(
                "/usr/bin/clang",
                fixtureRoot,
                "-fobjc-arc",
                "-framework",
                "AppKit",
                "-o",
                executablePath,
                sourcePath));

            var result = Run(
                "/usr/bin/xcrun",
                RepositoryRoot,
                "swift",
                Path.Combine(RepositoryRoot, "script", "macos", "verify-app-bundle-launch.swift"),
                appPath);

            AssertSuccess(result);
            Assert.Contains("isFinishedLaunching", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("isTerminated", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void BundleLaunchVerificationRejectsTerminationBeforeFinishedLaunching()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-bundle-termination-{Guid.NewGuid():N}");
        var appPath = Path.Combine(fixtureRoot, "TerminatingFixture.app");
        var executableDirectory = Path.Combine(appPath, "Contents", "MacOS");
        var executablePath = Path.Combine(executableDirectory, "TerminatingFixture");
        var sourcePath = Path.Combine(fixtureRoot, "TerminatingFixture.c");
        Directory.CreateDirectory(executableDirectory);

        try
        {
            WriteInfoPlist(appPath, "TerminatingFixture", $"cn.bzdrs.downkyi.terminating-fixture-{Guid.NewGuid():N}");
            File.WriteAllText(sourcePath, "int main(void) { return 17; }\n", new UTF8Encoding(false));
            AssertSuccess(Run("/usr/bin/clang", fixtureRoot, "-o", executablePath, sourcePath));

            var result = Run(
                "/usr/bin/xcrun",
                RepositoryRoot,
                "swift",
                Path.Combine(RepositoryRoot, "script", "macos", "verify-app-bundle-launch.swift"),
                appPath);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("::error::", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static ProcessResult RunSigningScript(string appPath)
    {
        return Run(
            "/bin/bash",
            RepositoryRoot,
            new Dictionary<string, string?>
            {
                ["MACOS_ADHOC_SIGNING"] = "true"
            },
            Path.Combine(RepositoryRoot, "script", "macos", "sign.sh"),
            appPath);
    }

    private static void AssertAdHocSignatureDoesNotEnableHardenedRuntime(string path)
    {
        var result = Run("/usr/bin/codesign", RepositoryRoot, "-dv", "--verbose=4", path);
        AssertSuccess(result);

        var codeDirectory = (result.StandardOutput + result.StandardError)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Single(line => line.StartsWith("CodeDirectory ", StringComparison.Ordinal));
        Assert.Contains("adhoc", codeDirectory, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime", codeDirectory, StringComparison.Ordinal);
    }

    private static void CreateAppBundle(string appPath, string publishDirectory)
    {
        var contentsDirectory = Path.Combine(appPath, "Contents");
        var macOsDirectory = Path.Combine(contentsDirectory, "MacOS");
        Directory.CreateDirectory(macOsDirectory);
        Directory.CreateDirectory(Path.Combine(contentsDirectory, "Resources"));

        AssertSuccess(Run("/bin/cp", RepositoryRoot, "-a", $"{publishDirectory}/.", macOsDirectory));
        var ariaDirectory = Path.Combine(macOsDirectory, "aria2");
        var ariaExecutable = Path.Combine(ariaDirectory, "aria2c");
        Directory.CreateDirectory(ariaDirectory);
        File.Copy(Path.Combine(macOsDirectory, "BundleProbe"), ariaExecutable);
        AssertSuccess(Run("/bin/chmod", RepositoryRoot, "+x", ariaExecutable));
        File.WriteAllText(
            $"{ariaExecutable}.sha256",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ariaExecutable))));
        File.WriteAllText(
            Path.Combine(contentsDirectory, "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleExecutable</key>
              <string>BundleProbe</string>
              <key>CFBundleIdentifier</key>
              <string>cn.bzdrs.downkyi.bundle-probe</string>
              <key>CFBundlePackageType</key>
              <string>APPL</string>
            </dict>
            </plist>
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteInfoPlist(string appPath, string executableName, string bundleIdentifier)
    {
        var contentsDirectory = Path.Combine(appPath, "Contents");
        Directory.CreateDirectory(contentsDirectory);
        File.WriteAllText(
            Path.Combine(contentsDirectory, "Info.plist"),
            $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleExecutable</key>
              <string>{{executableName}}</string>
              <key>CFBundleIdentifier</key>
              <string>{{bundleIdentifier}}</string>
              <key>CFBundlePackageType</key>
              <string>APPL</string>
            </dict>
            </plist>
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static ProcessResult Run(
        string fileName,
        string workingDirectory,
        params string[] arguments)
    {
        return Run(fileName, workingDirectory, environment: null, arguments);
    }

    private static ProcessResult Run(
        string fileName,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment != null)
        {
            foreach (var item in environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new ProcessResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private static void AssertSuccess(ProcessResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Process failed with exit code {result.ExitCode}. stdout={result.StandardOutput} stderr={result.StandardError}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
