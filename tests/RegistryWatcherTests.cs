using System;
using System.Threading;
using System.Threading.Tasks;
using RegistryEditor.Presentation.Backend;
using Xunit;

namespace RegistryEditor.Backend.Tests;

public class RegistryWatcherTests
{
    [Fact]
    public async Task WaitForChangeAsync_ValueUpdateNotifiesTheWatchedFixture()
    {
        using var scope = new RegistryScope();
        using var watcher = new RegistryWatcher(scope.Path);
        var pending = watcher.WaitForChangeAsync(TestContext.Current.CancellationToken);

        RegistryBackend.Set(scope.Path, new RawValue("value", 3, new byte[] { 0x31 }));

        await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0x31 }, Assert.Single(RegistryBackend.Values(scope.Path)).Bytes);
    }

    [Fact]
    public async Task WaitForChangeAsync_DirectChildCreationNotifiesTheWatchedFixture()
    {
        using var scope = new RegistryScope();
        using var watcher = new RegistryWatcher(scope.Path);
        var pending = watcher.WaitForChangeAsync(TestContext.Current.CancellationToken);

        RegistryBackend.CreateKey(scope.Path, "Child");

        await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "Child" }, RegistryBackend.Children(scope.Path));
    }

    [Fact]
    public async Task WaitForChangeAsync_RearmsAfterEachNotification()
    {
        using var scope = new RegistryScope();
        using var watcher = new RegistryWatcher(scope.Path);

        var firstWait = watcher.WaitForChangeAsync(TestContext.Current.CancellationToken);
        RegistryBackend.Set(scope.Path, new RawValue("first", 3, new byte[] { 1 }));
        await firstWait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var secondWait = watcher.WaitForChangeAsync(TestContext.Current.CancellationToken);
        RegistryBackend.Set(scope.Path, new RawValue("second", 3, new byte[] { 2 }));
        await secondWait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "first", "second" }, System.Linq.Enumerable.Select(RegistryBackend.Values(scope.Path), value => value.Name));
    }

    [Fact]
    public async Task WaitForChangeAsync_CancellationEndsThePendingWait()
    {
        using var scope = new RegistryScope();
        using var watcher = new RegistryWatcher(scope.Path);
        using var cancellation = new CancellationTokenSource();
        var pending = watcher.WaitForChangeAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Constructor_RejectsRemotePathsBeforeOpeningThem()
    {
        Assert.Throws<ArgumentException>(() =>
            new RegistryWatcher(@"\\127.0.0.1\HKCU\Software\RegEditto.Tests"));
    }
}
