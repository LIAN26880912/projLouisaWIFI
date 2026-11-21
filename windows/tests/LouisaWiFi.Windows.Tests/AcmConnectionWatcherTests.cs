using LouisaWiFi.Windows.Platform;

namespace LouisaWiFi.Windows.Tests;

public class AcmConnectionWatcherTests
{
    private const uint AttemptFail = 11;
    private static readonly Guid Wlan = Guid.NewGuid();
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task AttemptFailBeforeCompletionIsNotFinal()
    {
        using var watcher = new AcmConnectionWatcher(Wlan, "LouisaCoffee");
        watcher.OnNotification(Wlan, AcmConnectionWatcher.ConnectionStart, "LouisaCoffee", 0);
        watcher.OnNotification(Wlan, AttemptFail, "LouisaCoffee", 0);
        watcher.OnNotification(Wlan, AcmConnectionWatcher.ConnectionComplete, "LouisaCoffee", 0);

        Assert.True(await watcher.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task CompletionWithReasonCodeFails()
    {
        using var watcher = new AcmConnectionWatcher(Wlan, "LouisaCoffee");
        watcher.OnNotification(Wlan, AcmConnectionWatcher.ConnectionStart, "LouisaCoffee", 0);
        watcher.OnNotification(Wlan, AcmConnectionWatcher.ConnectionComplete, "LouisaCoffee", 0x40001);

        Assert.False(await watcher.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task IgnoresStaleCompletionAndOtherInterfacesOrProfiles()
    {
        using var watcher = new AcmConnectionWatcher(Wlan, "LouisaCoffee");
        watcher.OnNotification(Wlan, AcmConnectionWatcher.ConnectionComplete, "LouisaCoffee", 0);
        watcher.OnNotification(Guid.NewGuid(), AcmConnectionWatcher.ConnectionStart, "LouisaCoffee", 0);
        watcher.OnNotification(Wlan, AcmConnectionWatcher.ConnectionStart, "louisacoffee", 0);
        watcher.OnNotification(Wlan, AcmConnectionWatcher.ConnectionComplete, "louisacoffee", 0);

        Assert.False(await watcher.WaitAsync(Short, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationThrows()
    {
        using var watcher = new AcmConnectionWatcher(Wlan, "LouisaCoffee");
        using var cancel = new CancellationTokenSource(Short);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            watcher.WaitAsync(TimeSpan.FromSeconds(30), cancel.Token));
    }
}
