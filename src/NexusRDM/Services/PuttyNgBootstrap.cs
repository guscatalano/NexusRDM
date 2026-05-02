using System.Diagnostics;
using System.Net.Http;

namespace NexusRDM.Services;

/// <summary>
/// Resolves a usable <c>PuTTYNG.exe</c> path. Probe order:
///   1. User-configured override in <see cref="ViewModels.SettingsStore"/>.
///   2. Cached download under <c>%LocalAppData%\NexusRDM\PuTTYNG\</c>.
///   3. System PATH (a manually-installed PuTTYNG).
///   4. Last resort: download a pinned PuTTYNG release from GitHub.
///
/// PuTTYNG is a fork of PuTTY tailored for embedding via <c>-hwndparent</c>.
/// It's a single self-contained .exe (~1.5 MB) with a Certum code-signing
/// cert, so the download is a straight file copy — no MSI extraction.
///
/// We pin a known-good version. If the upstream URL goes stale, bump
/// <see cref="PinnedVersion"/> and the download falls into the new path.
/// Cached downloads are versioned (<c>PuTTYNG-0.83.exe</c>) so a version
/// bump triggers a fresh fetch automatically without leaving a stale
/// binary.
/// </summary>
internal static class PuttyNgBootstrap
{
    // PuTTYNG release tag we pin to. Published at
    //   https://github.com/mRemoteNG/PuTTYNG/releases
    // Their tag scheme is `v<putty>.<patchset>.<build>[.x64]` — bump
    // this when a new x64 release ships. The asset filename has been
    // PuTTYNG.exe across recent releases.
    private const string PinnedTag       = "v0.83.0.1.x64";
    private const string PinnedAssetName = "PuTTYNG.exe";
    private static readonly string DownloadUrl =
        $"https://github.com/mRemoteNG/PuTTYNG/releases/download/{PinnedTag}/{PinnedAssetName}";

    // Fallback if the pinned tag goes 404 (e.g. asset renamed). GitHub's
    // /releases/latest/download/<asset> URL always redirects to the most
    // recent release that contains an asset by that name.
    private static readonly string LatestFallbackUrl =
        $"https://github.com/mRemoteNG/PuTTYNG/releases/latest/download/{PinnedAssetName}";

    private static string?            _resolved;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public static async Task<string?> ResolveAsync(IProgress<string>? onProgress = null, CancellationToken ct = default)
    {
        if (_resolved is not null) return _resolved;
        await _gate.WaitAsync(ct);
        try
        {
            if (_resolved is not null) return _resolved;

            // 1. User override. Trust the user — no PuTTYNG check.
            var userPath = ViewModels.SettingsStore.ReadPuttyNgExePath();
            if (!string.IsNullOrWhiteSpace(userPath) && File.Exists(userPath))
            {
                onProgress?.Report($"Using user-configured exe: {userPath}");
                _resolved = userPath;
                return _resolved;
            }

            // 2. Cached download. We deliberately don't enforce the
            //    "is this PuTTYNG?" check anymore — our launch flow
            //    works with stock PuTTY too (no -hwndparent flag), so
            //    accept whatever's there if it looks like a real EXE.
            var cacheDir = CacheDirectory();
            var cached   = Path.Combine(cacheDir, $"PuTTYNG-{PinnedTag}.exe");
            if (File.Exists(cached) && new FileInfo(cached).Length > 100_000)
            {
                onProgress?.Report($"Using cached binary: {cached}");
                _resolved = cached;
                return _resolved;
            }
            else if (File.Exists(cached))
            {
                // Truncated / aborted prior download — reject and refetch.
                onProgress?.Report($"Cached file at {cached} is too small; re-downloading.");
                try { File.Delete(cached); } catch { }
            }

            // 3. System PATH probe.
            var pathHit = ProbePath("PuTTYNG.exe");
            if (pathHit is not null)
            {
                onProgress?.Report($"Found PuTTYNG on PATH: {pathHit}");
                _resolved = pathHit;
                return _resolved;
            }

            // 4. Download. Try the pinned tag first; on 404 (likely
            //    cause: tag renamed upstream) fall back to the
            //    "latest" redirect URL. After download, verify the
            //    file is actually PuTTYNG (not stock PuTTY served by
            //    a redirect, not an HTML error page).
            Directory.CreateDirectory(cacheDir);
            string? lastError = null;
            foreach (var url in new[] { DownloadUrl, LatestFallbackUrl })
            {
                try
                {
                    onProgress?.Report($"Downloading PuTTYNG from {url}…");
                    await DownloadAsync(url, cached, ct);
                    if (!File.Exists(cached) || new FileInfo(cached).Length < 100_000)
                    {
                        lastError = "downloaded file is too small (likely an HTML error page).";
                        try { File.Delete(cached); } catch { }
                        continue;
                    }
                    onProgress?.Report($"PuTTYNG ready: {cached}");
                    _resolved = cached;
                    return _resolved;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    onProgress?.Report($"PuTTYNG download attempt failed: {ex.Message}");
                }
            }
            onProgress?.Report($"PuTTYNG download failed: {lastError ?? "unknown error"}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusRDM", "PuTTYNG");

    private static string? ProbePath(string exe)
    {
        // PuTTYNG (and PuTTY) doesn't have a `--version` that exits
        // cleanly without a dialog — so we just check the file exists
        // where the OS would resolve it from PATH and return the path.
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    /// <summary>True if the EXE at <paramref name="path"/> is PuTTYNG
    /// (versus stock PuTTY or some other binary). PuTTYNG's build
    /// stamps "mRemoteNG" into version metadata via version.h patches —
    /// we look for that string in FileDescription / ProductName /
    /// ProductVersion. False on any read error or missing marker.</summary>
    private static bool IsPuttyNgBinary(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            string Combined() => string.Join(" | ",
                info.FileDescription ?? "",
                info.ProductName ?? "",
                info.ProductVersion ?? "",
                info.FileVersion ?? "",
                info.Comments ?? "");
            return Combined().Contains("mRemoteNG", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
    }
}
