using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DownKyi.Architecture.Tests;

internal static class TestFixtureProcess
{
    public static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "DownKyi.CentralTestRunner.exe" : "DownKyi.CentralTestRunner");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The built central test fixture executable is missing.", executable);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Keep the fixture independent of VSTest's process-specific startup settings.
        startInfo.Environment.Clear();
        foreach (var variable in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH", "HOME" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[variable] = value;
            }
        }

        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent
                         ?? throw new DirectoryNotFoundException("Could not locate the active .NET runtime root.");
        startInfo.Environment["DOTNET_ROOT"] = dotnetRoot.FullName;
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
