using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace pViewer.Services;

/// <summary>A newer release on GitHub, with the installer of the edition in use.</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string InstallerName, string InstallerUrl, long InstallerSize, string? Sha256);

/// <summary>
/// Looks for a newer release on GitHub and downloads its installer. Only the release published
/// as "latest" counts (never drafts or pre-releases), and the installer must come from GitHub.
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/FirekeeperPhate/pViewer/releases/latest";
    public const string ReleasesPage = "https://github.com/FirekeeperPhate/pViewer/releases";

    /// <summary>Automatic checks happen at most this often.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>Longest wait for the next piece of a download.</summary>
    internal static TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pViewer", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Full edition: the .NET runtime sits next to the program.</summary>
    public static bool IsFullEdition => File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));

    /// <summary>Installed with the setup (its uninstaller is there): it can update itself.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>
    /// Another pViewer process from this same program folder (the installer could not replace it).
    /// Another user's pViewer counts only for an all-users install: a per-user one has its own folder.
    /// </summary>
    public static bool OtherInstancesRunning()
    {
        string self = Environment.ProcessPath ?? "";
        int id = Environment.ProcessId;
        int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        bool shared = IsAllUsersInstall();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("pViewer"))
        {
            using (process)
            {
                if (process.Id == id) continue;
                try
                {
                    if (process.SessionId != session)
                    {
                        if (shared) return true; // cannot be inspected; may be using the same files
                        continue;
                    }
                    if (string.Equals(process.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return true; // cannot be inspected (running elevated): better to ask to close it
                }
                catch (InvalidOperationException) { } // exited meanwhile
            }
        }
        return false;
    }

    /// <summary>
    /// Installed for all users: the setup registered it under HKLM (in any folder the user chose),
    /// or it lives in Program Files.
    /// </summary>
    private static bool IsAllUsersInstall()
    {
        string here = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (here.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{03AEDA4F-AF78-4EC2-89CB-CE68A794A15F}_is1");
            return key?.GetValue("InstallLocation") is string location
                   && string.Equals(Path.TrimEndingDirectorySeparator(location), here, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string DownloadFolder => Path.Combine(Path.GetTempPath(), "pViewer-update");

    /// <summary>
    /// Deletes the installers downloaded for earlier updates (one still running is skipped, and so
    /// is a download of the last hour, which another pViewer window may be about to start).
    /// </summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(DownloadFolder)) return;
            foreach (string file in Directory.EnumerateFiles(DownloadFolder))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(1)) continue;
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Directory.Delete(DownloadFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <returns>The newer release, or null if this is the latest (or there is no usable installer).</returns>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(LatestReleaseApi, ct);
        // No release yet, or the repository is not public: simply nothing to update to.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseRelease(json.RootElement, CurrentVersion, IsFullEdition);
    }

    /// <summary>Reads GitHub's "latest release" answer (separate for the tests).</summary>
    internal static UpdateInfo? ParseRelease(JsonElement release, Version current, bool fullEdition)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= current) return null;

        string wanted = $"pViewer-Setup-{tag.TrimStart('v', 'V')}-{(fullEdition ? "Full" : "Light")}.exe";
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsGitHubUrl(url)) return null;
            string? sha = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d
                          && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
            string page = release.TryGetProperty("html_url", out var html) && html.GetString() is { } h && IsGitHubUrl(h) ? h : ReleasesPage;
            // Without a usable size the download could not be checked: no update rather than an error.
            if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                || !size.TryGetInt64(out long bytes) || bytes <= 0)
                return null;
            return new UpdateInfo(version, page, wanted, url, bytes, sha);
        }
        return null;
    }

    private static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads the installer into a temporary folder and checks its size (and SHA-256, when
    /// GitHub gives it). Returns the path of the verified file.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        string folder = DownloadFolder;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, update.InstallerName);
        string partial = path + ".part";
        try
        {
            using (var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is { } final && !IsGitHubUrl(final.AbsoluteUri))
                    throw new InvalidDataException("The download was redirected outside GitHub.");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                // HttpClient.Timeout ends with the headers: a connection that stalls later would wait forever.
                using var stalled = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha = SHA256.Create();
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while (true)
                {
                    stalled.CancelAfter(ReadTimeout);
                    try
                    {
                        read = await source.ReadAsync(buffer, stalled.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException("The download stopped responding.");
                    }
                    if (read == 0) break;
                    total += read;
                    if (total > update.InstallerSize) throw new InvalidDataException("The download is larger than announced.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    progress?.Report((double)total / update.InstallerSize);
                }
                sha.TransformFinalBlock([], 0, 0);
                if (total != update.InstallerSize) throw new InvalidDataException("The download is incomplete.");
                if (update.Sha256 is { } expected && !string.Equals(Convert.ToHexString(sha.Hash!), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded installer does not match the release (checksum).");
            }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
    }
}
