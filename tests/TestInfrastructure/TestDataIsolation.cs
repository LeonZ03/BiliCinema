using System.Globalization;
using System.Reflection;

namespace DownKyi.TestInfrastructure;

public sealed class TestDataIsolationFixture : IAsyncDisposable
{
    private const string LifecycleMarkerEnvironmentVariable = "DOWNKYI_LIFECYCLE_MARKER";
    private readonly string _root;
    private readonly string? _lifecycleMarker;
    private readonly Action<string> _deleteRoot;

    public TestDataIsolationFixture()
    {
        var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";
        _root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-tests",
            assemblyName,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("DOWNKYI_DATA_DIR", _root);
        _lifecycleMarker = Environment.GetEnvironmentVariable(LifecycleMarkerEnvironmentVariable);
        _deleteRoot = DeleteRoot;
        WriteLifecycleMarker("started");
    }

    internal TestDataIsolationFixture(string root, Action<string> deleteRoot)
    {
        ArgumentNullException.ThrowIfNull(deleteRoot);
        _root = root;
        _deleteRoot = deleteRoot;
    }

    public ValueTask DisposeAsync()
    {
        WriteLifecycleMarker("disposing");
        _deleteRoot(_root);
        WriteLifecycleMarker("disposed");
        return ValueTask.CompletedTask;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void WriteLifecycleMarker(string state)
    {
        if (string.IsNullOrWhiteSpace(_lifecycleMarker))
        {
            return;
        }

        var markerDirectory = Path.GetDirectoryName(_lifecycleMarker);
        if (!string.IsNullOrWhiteSpace(markerDirectory))
        {
            Directory.CreateDirectory(markerDirectory);
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{state}|{Environment.ProcessId}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        File.AppendAllText(_lifecycleMarker, line + Environment.NewLine);
    }
}
