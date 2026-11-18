using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace LouisaWiFi.Windows.Services;

public sealed record AppRelease(Version Version, Uri Page, Uri Zip, string Sha256);

/// Port of app/Sources/Updater.swift: GitHub's latest release, LouisaWiFi-windows-{arch}.zip swapped in place.
public sealed class Updater
{
    public const string Repo = "LIAN26880912/projLouisaWIFI";
    internal const long MaxBytes = 200L * 1024 * 1024;
    private static readonly string[] Files = ["LouisaWiFi.exe", "THIRD-PARTY-NOTICES.txt"];

    private readonly HttpClient http;
    private readonly Uri api;
    private readonly string pagePrefix;
    private readonly string downloadPrefix;

    /// `site` (default https://github.com/) and `api` are only overridden by tests.
    public Updater(HttpClient? http = null, Uri? api = null, Uri? site = null, Version? current = null)
    {
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (this.http.DefaultRequestHeaders.UserAgent.Count == 0)
            this.http.DefaultRequestHeaders.UserAgent.ParseAdd("LouisaWiFi-Windows");
        this.api = api ?? new Uri($"https://api.github.com/repos/{Repo}/releases/latest");
        var root = (site ?? new Uri("https://github.com/")).AbsoluteUri.TrimEnd('/');
        pagePrefix = $"{root}/{Repo}/releases/";
        downloadPrefix = $"{root}/{Repo}/releases/download/";
        Current = Normalize(current ?? typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));
    }

    public Version Current { get; }

#pragma warning disable IL3000 // Empty Location is exactly how a single-file publish is detected.
    /// Only a published single-file LouisaWiFi.exe updates itself; dev builds under bin\ are left alone.
    public static bool IsPublished => string.IsNullOrEmpty(typeof(Updater).Assembly.Location);
#pragma warning restore IL3000

    public static string Display(Version version) => version.Revision > 0 ? version.ToString(4) : version.ToString(3);

    public static Version? ParseVersion(string? text) =>
        Version.TryParse(text?.Trim().TrimStart('v', 'V').Split('+')[0], out var version) ? Normalize(version) : null;

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// A newer release with a zip for this CPU, or null.
    public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(api, cancellationToken);
        // GitHub answers 404 while the repo has no published release yet: nothing newer to offer.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken), RuntimeInformation.ProcessArchitecture);
    }

    internal AppRelease? Parse(string json, Architecture architecture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (ParseVersion(root.GetProperty("tag_name").GetString()) is not { } version || version <= Current) return null;
        if (Trusted(root.GetProperty("html_url").GetString(), pagePrefix) is not { } page) return null;

        var name = $"LouisaWiFi-windows-{architecture.ToString().ToLowerInvariant()}.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var zip = Trusted(asset.GetProperty("browser_download_url").GetString(), downloadPrefix);
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            // Without GitHub's SHA-256 we can't verify the download, so don't offer it.
            if (zip is null || digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !digest[7..].All(Uri.IsHexDigit)) return null;
            return new AppRelease(version, page, zip, digest[7..].ToLowerInvariant());
        }
        return null;
    }

    private static Uri? Trusted(string? url, string prefix) =>
        url is not null && url.StartsWith(prefix, StringComparison.Ordinal) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;

    /// Streams the zip to folder\update.zip, refusing anything oversized or not matching the digest.
    public async Task<string> DownloadAsync(AppRelease release, string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "update.zip");
        using var response = await http.GetAsync(release.Zip, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("更新檔過大。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(path))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if ((total += read) > MaxBytes) throw new InvalidDataException("更新檔過大。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256)
                throw new InvalidDataException("更新檔驗證失敗，已取消更新。");
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// Extracts only the known top-level files, then checks the exe really is the expected version.
    public static string Extract(string zip, string staging, Version expected)
    {
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        using (var archive = ZipFile.OpenRead(zip))
            foreach (var entry in archive.Entries)
            {
                if (!Files.Contains(entry.FullName, StringComparer.Ordinal)) continue;
                if (entry.Length > MaxBytes) throw new InvalidDataException("更新檔過大。");
                entry.ExtractToFile(Path.Combine(staging, entry.FullName));
            }
        var exe = Path.Combine(staging, Files[0]);
        if (!File.Exists(exe)) throw new InvalidDataException("更新檔缺少 LouisaWiFi.exe。");
        if (ParseVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion) != Normalize(expected))
            throw new InvalidDataException("更新檔版本不符，已取消更新。");
        return staging;
    }

    public static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// A running exe can be renamed but not overwritten: each file goes to *.old, the new one takes its place.
    public static void Swap(string appFolder, string staging)
    {
        try
        {
            foreach (var name in Files)
            {
                var source = Path.Combine(staging, name);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(appFolder, name);
                File.Delete(target + ".old");
                if (File.Exists(target)) File.Move(target, target + ".old");
                File.Move(source, target);
            }
        }
        catch
        {
            Rollback(appFolder);
            throw;
        }
    }

    public static void Rollback(string appFolder)
    {
        foreach (var name in Files)
        {
            var target = Path.Combine(appFolder, name);
            if (!File.Exists(target + ".old")) continue;
            File.Delete(target);
            File.Move(target + ".old", target);
        }
    }

    /// Best effort: the previous exe may still be exiting; the next start tries again.
    public static void Cleanup(string appFolder, string updateFolder)
    {
        foreach (var name in Files)
            try { File.Delete(Path.Combine(appFolder, name + ".old")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try { if (Directory.Exists(updateFolder)) Directory.Delete(updateFolder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
