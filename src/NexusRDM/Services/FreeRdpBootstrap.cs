using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace NexusRDM.Services;

/// <summary>
/// Resolves a usable <c>wfreerdp.exe</c> path. Probe order:
///   1. User-configured override in <see cref="ViewModels.SettingsStore"/>
///      (lets advanced users point at a system-installed FreeRDP
///      they trust).
///   2. Cached download under <c>%LocalAppData%\NexusRDM\FreeRDP\</c>
///      from a previous run.
///   3. System PATH (winget/choco-installed FreeRDP).
///   4. Last resort: download the official FreeRDP MSI from GitHub
///      Releases, extract wfreerdp.exe + dependent DLLs into the
///      cache directory via <c>msiexec /a</c>, return that path.
///
/// Step 4 is one-time (~10 MB on disk). Subsequent <c>RdpLaunchMode.FreeRdp</c>
/// connections reuse the cached binary instantly. If the user
/// uninstalls / disables FreeRDP they can wipe the cache directory
/// and get a fresh download.
///
/// All filesystem + network work is awaitable; callers can show a
/// progress dialog by subscribing to <paramref name="onProgress"/>
/// in <see cref="ResolveAsync"/>.
/// </summary>
internal static class FreeRdpBootstrap
{
    // FreeRDP 3.5.1 MSI — stable Windows release, ~10 MB download,
    // ships wfreerdp.exe + the DLLs we need (winpr3, freerdp3,
    // freerdp-client3 plus a few mbedTLS/openssl). Pin the version
    // so behaviour doesn't drift between user installs. Bump when
    // upstream ships a fix we want.
    private const string DefaultMsiUrl =
        "https://github.com/FreeRDP/FreeRDP/releases/download/3.5.1/FreeRDP-3.5.1.msi";

    private static string?            _resolved;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Returns the absolute path to <c>wfreerdp.exe</c>, downloading
    /// + extracting it if needed. Null = unavailable (e.g. download
    /// failed and no system install exists).
    /// </summary>
    public static async Task<string?> ResolveAsync(IProgress<string>? onProgress = null, CancellationToken ct = default)
    {
        if (_resolved is not null) return _resolved;
        await _gate.WaitAsync(ct);
        try
        {
            if (_resolved is not null) return _resolved;

            // 1. User override.
            var userPath = ViewModels.SettingsStore.ReadFreeRdpExePath();
            if (!string.IsNullOrWhiteSpace(userPath) && File.Exists(userPath))
            {
                _resolved = userPath;
                return _resolved;
            }

            // 2. Cached download.
            var cacheDir = CacheDirectory();
            var cached   = Path.Combine(cacheDir, "wfreerdp.exe");
            if (File.Exists(cached))
            {
                _resolved = cached;
                return _resolved;
            }

            // 3. System PATH probe.
            if (TryProbe("wfreerdp.exe"))
            {
                _resolved = "wfreerdp.exe";
                return _resolved;
            }

            // 4. Download + extract.
            try
            {
                onProgress?.Report($"Downloading FreeRDP MSI…");
                Directory.CreateDirectory(cacheDir);

                var msiPath = Path.Combine(cacheDir, "freerdp-installer.msi");
                await DownloadAsync(DefaultMsiUrl, msiPath, ct);

                onProgress?.Report("Extracting FreeRDP…");
                var extracted = await ExtractMsiAsync(msiPath, ct);
                if (extracted is null)
                {
                    onProgress?.Report("MSI extraction failed.");
                    return null;
                }

                // Move wfreerdp.exe + every DLL from the extracted
                // tree into our cache dir flat. The MSI's directory
                // layout puts everything under
                //   <extract>\PFiles\FreeRDP\<version>\
                // — we don't care about that depth, we just want the
                // binary + its imports next to it.
                CopyFlat(extracted, cacheDir);

                if (File.Exists(cached))
                {
                    onProgress?.Report($"FreeRDP ready: {cached}");
                    _resolved = cached;
                    return _resolved;
                }
                onProgress?.Report("wfreerdp.exe not found in extracted MSI.");
                return null;
            }
            catch (Exception ex)
            {
                onProgress?.Report($"FreeRDP download failed: {ex.Message}");
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusRDM", "FreeRDP");

    private static bool TryProbe(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "/version")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            });
            if (p is null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
    }

    /// <summary>
    /// Run <c>msiexec /a</c> ("administrative install") which extracts
    /// the MSI's payload to a directory without actually installing
    /// anything. Returns the directory the files were extracted to,
    /// or null on failure.
    /// </summary>
    private static async Task<string?> ExtractMsiAsync(string msiPath, CancellationToken ct)
    {
        var extractDir = Path.Combine(Path.GetDirectoryName(msiPath)!, "extract");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        var psi = new ProcessStartInfo("msiexec.exe",
            $"/a \"{msiPath}\" /qn TARGETDIR=\"{extractDir}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return null;
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 && Directory.Exists(extractDir) ? extractDir : null;
    }

    private static void CopyFlat(string source, string dest)
    {
        // Walk the extracted MSI tree, copy every wfreerdp.exe / DLL
        // into the cache directory at the top level. Skips the .msi
        // file itself (the admin install copies it back into the
        // extract dir alongside the payload).
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;

            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not ".exe" and not ".dll" and not ".pdb") continue;

            var target = Path.Combine(dest, name);
            try { File.Copy(path, target, overwrite: true); }
            catch { /* best effort — partial copies still leave a usable wfreerdp.exe */ }
        }
    }
}
