using DownKyi.TestInfrastructure;

namespace DownKyi.Windows.Tests;

public sealed class TestDataIsolationFixtureWindowsTests
{
    [Fact]
    public async Task DisposeAsyncFailsWhileOwnedFileHandleIsOpen()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-test-data-isolation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "held.txt");
        await File.WriteAllTextAsync(filePath, "held", TestContext.Current.CancellationToken);
        var deleteAttempts = 0;
        var fixture = new TestDataIsolationFixture(root, path =>
        {
            deleteAttempts++;
            Directory.Delete(path, recursive: true);
        });

        try
        {
            using (File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => fixture.DisposeAsync().AsTask());
                Assert.Equal(1, deleteAttempts);
                Assert.True(Directory.Exists(root));
            }

            await fixture.DisposeAsync();
            Assert.Equal(2, deleteAttempts);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
