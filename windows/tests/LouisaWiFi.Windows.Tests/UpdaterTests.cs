using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.Tests;

public class UpdaterTests : IDisposable
{
    private const string Page = "https://github.com/" + Updater.Repo + "/releases/";
    private readonly string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static string Json(string tag, string asset = "LouisaWiFi-windows-x64.zip",
        string digest = "sha256:" + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12",
        string download = Page + "download/")
        => $$"""
        {"tag_name":"{{tag}}","html_url":"{{Page}}tag/{{tag}}",
         "assets":[{"name":"LouisaWiFi.zip","browser_download_url":"{{download}}{{tag}}/LouisaWiFi.zip","digest":"{{digest}}"},
                   {"name":"{{asset}}","browser_download_url":"{{download}}{{tag}}/{{asset}}","digest":"{{digest}}"}]}
        """;

    [Theory]
    [InlineData("v0.1.6", "0.1.6.0")]
    [InlineData("0.1.6.1", "0.1.6.1")]
    [InlineData("v1.2", "1.2.0.0")]
    [InlineData("0.1.6+abc", "0.1.6.0")]
    public void ParsesTagsAsFourPartVersions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), Updater.ParseVersion(tag));

    [Fact]
    public void DisplayHidesZeroRevision()
    {
        Assert.Equal("0.1.6", Updater.Display(new Version(0, 1, 6, 0)));
        Assert.Equal("0.1.6.1", Updater.Display(new Version(0, 1, 6, 1)));
    }

    [Theory]
    [InlineData("v0.1.6", false)]
    [InlineData("v0.1.5", false)]
    [InlineData("v0.1.6.1", true)]
    [InlineData("v0.2.0", true)]
    public void OffersOnlyNewerReleases(string tag, bool offered)
    {
        var updater = new Updater(current: new Version(0, 1, 6));
        Assert.Equal(offered, updater.Parse(Json(tag), Architecture.X64) is not null);
    }

    [Fact]
    public void PicksTheZipForThisCpu()
    {
        var updater = new Updater(current: new Version(0, 1, 0));
        var release = updater.Parse(Json("v0.2.0", "LouisaWiFi-windows-arm64.zip"), Architecture.Arm64)!;

        Assert.EndsWith("/LouisaWiFi-windows-arm64.zip", release.Zip.AbsoluteUri);
        Assert.Equal("ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12", release.Sha256);
        Assert.Null(updater.Parse(Json("v0.2.0", "LouisaWiFi-windows-arm64.zip"), Architecture.X64));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:abc")]
    [InlineData("md5:ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12")]
    [InlineData("sha256:zz12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12")]
    public void RejectsMissingOrBadDigest(string digest) =>
        Assert.Null(new Updater(current: new Version(0, 1)).Parse(Json("v0.2.0", digest: digest), Architecture.X64));

    [Theory]
    [InlineData("https://evil.example/" + Updater.Repo + "/releases/download/")]
    [InlineData("http://github.com/" + Updater.Repo + "/releases/download/")]
    [InlineData("https://github.com/someone/else/releases/download/")]
    public void RejectsDownloadsOutsideThisRepo(string download) =>
        Assert.Null(new Updater(current: new Version(0, 1)).Parse(Json("v0.2.0", download: download), Architecture.X64));

    [Fact]
    public async Task DownloadKeepsOnlyMatchingDigest()
    {
        var body = "zip bytes"u8.ToArray();
        var good = Convert.ToHexStringLower(SHA256.HashData(body));
        var http = new HttpClient(new StaticHandler(body));
        var updater = new Updater(http, current: new Version(0, 1));
        var zip = new Uri(Page + "download/v0.2.0/LouisaWiFi-windows-x64.zip");

        var path = await updater.DownloadAsync(new AppRelease(new Version(0, 2), new Uri(Page), zip, good), dir, default);
        Assert.Equal(body, File.ReadAllBytes(path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updater.DownloadAsync(new AppRelease(new Version(0, 2), new Uri(Page), zip, new string('0', 64)), dir, default));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ExtractTakesOnlyKnownFilesAndChecksVersion()
    {
        // Any PE with version info works; the app dll is 0.1.0.0 like the csproj's <Version>.
        var exe = typeof(Updater).Assembly.Location;
        var zip = Path.Combine(dir, "update.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(exe, "LouisaWiFi.exe");
            archive.CreateEntry("../evil.txt");
            archive.CreateEntry("sub/LouisaWiFi.exe");
        }
        var version = typeof(Updater).Assembly.GetName().Version!;

        var staging = Updater.Extract(zip, Path.Combine(dir, "staging"), version);
        Assert.Equal(["LouisaWiFi.exe"], Directory.GetFileSystemEntries(staging).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));

        Assert.Throws<InvalidDataException>(() =>
            Updater.Extract(zip, Path.Combine(dir, "staging"), new Version(version.Major + 1, 0)));
    }

    [Fact]
    public void SwapKeepsOldFilesForRollbackAndCleanup()
    {
        var app = Directory.CreateDirectory(Path.Combine(dir, "app")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(dir, "staging")).FullName;
        var update = Directory.CreateDirectory(Path.Combine(dir, "update")).FullName;
        File.WriteAllText(Path.Combine(app, "LouisaWiFi.exe"), "old");
        File.WriteAllText(Path.Combine(staging, "LouisaWiFi.exe"), "new");
        File.WriteAllText(Path.Combine(staging, "THIRD-PARTY-NOTICES.txt"), "notices");

        Updater.Swap(app, staging);
        Assert.Equal("new", File.ReadAllText(Path.Combine(app, "LouisaWiFi.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(app, "LouisaWiFi.exe.old")));

        Updater.Rollback(app);
        Assert.Equal("old", File.ReadAllText(Path.Combine(app, "LouisaWiFi.exe")));
        Assert.False(File.Exists(Path.Combine(app, "LouisaWiFi.exe.old")));
        Assert.True(File.Exists(Path.Combine(app, "THIRD-PARTY-NOTICES.txt")));

        File.WriteAllText(Path.Combine(app, "LouisaWiFi.exe.old"), "stale");
        Updater.Cleanup(app, update);
        Assert.False(File.Exists(Path.Combine(app, "LouisaWiFi.exe.old")));
        Assert.False(Directory.Exists(update));
    }

    [Fact]
    public void CanWriteDetectsWritableFolder()
    {
        Assert.True(Updater.CanWrite(dir));
        Assert.False(Updater.CanWrite(Path.Combine(dir, "missing")));
    }

    private sealed class StaticHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
