using LouisaWiFi.Windows.Platform;

namespace LouisaWiFi.Windows.Tests;

public class AutoConnectProfileTests
{
    [Fact]
    public async Task WaitsForConnectionAndRepairsDelayedManualModeBeforeReportingSuccess()
    {
        var poll = 0;
        var automatic = true;
        var repairs = new List<int>();

        var stable = await NativeWifiAdapter.WaitForStableAutoConnectAsync(
            () => poll >= 3,
            () => automatic,
            () =>
            {
                repairs.Add(poll);
                automatic = true;
            },
            _ =>
            {
                poll++;
                if (poll == 4) automatic = false;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(stable);
        Assert.Equal([7], repairs);
        Assert.Equal(12, poll);
    }

    [Fact]
    public async Task WaitsForFiveConnectedPollsBeforeChangingTheProfile()
    {
        var poll = 0;
        var automatic = false;
        var repairs = new List<int>();

        var stable = await NativeWifiAdapter.WaitForStableAutoConnectAsync(
            () => poll >= 3 && poll != 5,
            () => automatic,
            () =>
            {
                repairs.Add(poll);
                automatic = true;
            },
            _ =>
            {
                poll++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(stable);
        Assert.Equal([10], repairs);
        Assert.Equal(15, poll);
    }

    [Fact]
    public async Task ConnectionDropResetsTheStabilityWindow()
    {
        var poll = 0;

        var stable = await NativeWifiAdapter.WaitForStableAutoConnectAsync(
            () => poll != 4,
            () => true,
            () => throw new InvalidOperationException("Unexpected profile change."),
            _ =>
            {
                poll++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(stable);
        Assert.Equal(9, poll);
    }

    [Fact]
    public async Task UnstableProfileTimesOutAfterThirtyPolls()
    {
        var poll = 0;
        var repairs = 0;

        var stable = await NativeWifiAdapter.WaitForStableAutoConnectAsync(
            () => true,
            () => false,
            () => repairs++,
            _ =>
            {
                poll++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(stable);
        Assert.Equal(30, poll);
        Assert.Equal(1, repairs);
    }

    [Fact]
    public async Task ReconnectionAndProfileResetDoNotTriggerRepeatedRewrites()
    {
        var poll = 0;
        var automatic = false;
        var repairs = 0;

        var stable = await NativeWifiAdapter.WaitForStableAutoConnectAsync(
            () => poll != 6,
            () => automatic,
            () =>
            {
                repairs++;
                automatic = true;
            },
            _ =>
            {
                poll++;
                if (poll == 8) automatic = false;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(stable);
        Assert.Equal(30, poll);
        Assert.Equal(1, repairs);
    }

    [Fact]
    public async Task CancellationStopsTheStabilityCheck()
    {
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeWifiAdapter.WaitForStableAutoConnectAsync(
                () => true,
                () => true,
                () => { },
                token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cancellation.Token));
    }
}
