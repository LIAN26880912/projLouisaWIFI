using System.IO.Pipes;
using LouisaWiFi.Windows.Platform;
using LouisaWiFi.Windows.Services;
using Microsoft.Win32;

namespace LouisaWiFi.Windows.Tests;

public class SystemIntegrationTests
{
    [Theory]
    [InlineData(new string[0], null, null)]
    [InlineData(new[] { "--nearest" }, CommandKind.Nearest, null)]
    [InlineData(new[] { "--SHOW" }, CommandKind.Show, null)]
    [InlineData(new[] { "--stats" }, CommandKind.Stats, null)]
    [InlineData(new[] { "--update" }, CommandKind.Update, null)]
    [InlineData(new[] { "--connect", " 金城延和 " }, CommandKind.Connect, "金城延和")]
    public void CommandLineParsesKnownCommands(string[] args, CommandKind? kind, string? store)
    {
        var (command, error) = CommandLine.Parse(args);
        Assert.Null(error);
        Assert.Equal(kind, command?.Kind);
        Assert.Equal(store, command?.Store);
    }

    [Theory]
    [InlineData("--connect")]
    [InlineData("--connect", "  ")]
    [InlineData("--nearest", "extra")]
    [InlineData("--delete")]
    [InlineData("louisawifi://connect")]
    public void CommandLineRejectsUnknownOrMalformedInput(params string[] args)
    {
        var (command, error) = CommandLine.Parse(args);
        Assert.Null(command);
        Assert.NotNull(error);
    }

    [Fact]
    public void CommandLineRejectsOverlongStoreName()
    {
        Assert.NotNull(CommandLine.Parse(["--connect", new string('店', 101)]).Error);
    }

    [Fact]
    public async Task SecondInstanceIsRefusedAndItsArgumentsReachTheFirst()
    {
        var name = "LouisaWiFiTest-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));

        using var stop = new CancellationTokenSource();
        var received = new TaskCompletionSource<string[]>();
        var listening = first!.ListenAsync(args => received.TrySetResult(args), stop.Token);

        Assert.True(SingleInstance.Send(["--connect", "金城延和門市"], name));
        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["--connect", "金城延和門市"], args);

        stop.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void SendFailsQuicklyWhenNobodyListens()
    {
        Assert.False(SingleInstance.Send(["--show"], "LouisaWiFiTest-" + Guid.NewGuid().ToString("N"), timeoutMs: 200));
    }

    [Fact]
    public async Task PipeMessagesThatAreTooLargeOrNotStringArraysAreIgnored()
    {
        Assert.Null(await SingleInstance.ReadAsync(new MemoryStream(new byte[SingleInstance.MaxMessageBytes + 1]), default));
        Assert.Null(await SingleInstance.ReadAsync(new MemoryStream("[null]"u8.ToArray()), default));
        Assert.Null(await SingleInstance.ReadAsync(new MemoryStream([]), default));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => SingleInstance.ReadAsync(new MemoryStream("{\"a\":1}"u8.ToArray()), default));
    }

    [Fact]
    public void LoginItemWritesAQuotedPathAndHonorsTaskManagerDisable()
    {
        var root = @"Software\LouisaWiFiTests\" + Guid.NewGuid().ToString("N");
        var exe = @"C:\Program Files\Louisa WiFi\LouisaWiFi.exe";
        var item = new LoginItem(root + @"\Run", root + @"\Approved", exe);
        try
        {
            Assert.True(item.Supported);
            Assert.False(item.Enabled);
            item.Set(true);
            using (var run = Registry.CurrentUser.OpenSubKey(root + @"\Run"))
                Assert.Equal($"\"{exe}\"", run!.GetValue("LouisaWiFi"));
            Assert.True(item.Enabled);

            using (var approved = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
                approved.SetValue("LouisaWiFi", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.False(item.Enabled);
            item.Set(true);
            Assert.True(item.Enabled);

            item.Set(false);
            Assert.False(item.Enabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void LoginItemRefusesToRegisterDotnetHost()
    {
        var root = @"Software\LouisaWiFiTests\" + Guid.NewGuid().ToString("N");
        var item = new LoginItem(root + @"\Run", root + @"\Approved", @"C:\Program Files\dotnet\dotnet.exe");
        try
        {
            Assert.False(item.Supported);
            Assert.Throws<InvalidOperationException>(() => item.Set(true));
            Assert.False(item.Enabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void AppLogRotatesAndFlattensLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var previous = AppLog.PathOverride;
        try
        {
            AppLog.PathOverride = Path.Combine(dir, "LouisaWiFi.log");
            Directory.CreateDirectory(dir);
            File.WriteAllText(AppLog.PathOverride, new string('x', 1024 * 1024 + 1));
            AppLog.Write("第一行\n第二行");
            Assert.True(File.Exists(AppLog.PathOverride + ".old"));
            var lines = File.ReadAllLines(AppLog.PathOverride);
            Assert.Single(lines);
            Assert.EndsWith("第一行 第二行", lines[0]);
        }
        finally
        {
            AppLog.PathOverride = previous;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
