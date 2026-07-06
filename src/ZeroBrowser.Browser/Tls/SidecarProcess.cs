using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroBrowser.Browser.Tls;

/// <summary>
/// Manages the lifecycle of one sidecar Go process: launch, wait for /health,
/// surface logs, kill on dispose.
/// </summary>
public sealed class SidecarProcess : IAsyncDisposable
{
    private readonly Process _proc;
    public int Port { get; }
    public int HealthPort => Port + 1;
    public string SidecarPath { get; }
    public string ProfileId { get; }
    public string TemplateId { get; }

    private SidecarProcess(Process proc, int port, string sidecarPath, string profileId, string templateId)
    {
        _proc = proc;
        Port = port;
        SidecarPath = sidecarPath;
        ProfileId = profileId;
        TemplateId = templateId;
    }

    /// <summary>
    /// Launch the sidecar binary and block until /health returns 200 or
    /// the timeout elapses. Throws on any failure.
    /// </summary>
    public static async Task<SidecarProcess> StartAsync(
        string sidecarPath,
        int port,
        string profileId,
        string fingerprintMode,
        string seed,
        string caCertPath,
        string caKeyPath,
        string? upstreamProxy,
        TimeSpan readyTimeout,
        CancellationToken ct = default)
    {
        if (!File.Exists(sidecarPath))
            throw new FileNotFoundException("sidecar binary not found", sidecarPath);

        // On Linux/macOS, the sidecar binary must be user-executable. If it
        // was copied from a Windows filesystem (FAT32/NTFS) or extracted from
        // a zip that didn't preserve mode bits, Process.Start will fail with
        // "Permission denied" on Linux and "Bad CPU type in program" or similar
        // on macOS. Set the exec bit defensively here — it's a no-op on Windows.
        ExecutablePermissions.EnsureUserExecutable(sidecarPath);

        var psi = new ProcessStartInfo
        {
            FileName = sidecarPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(sidecarPath) ?? Environment.CurrentDirectory
        };
        psi.ArgumentList.Add("--port");        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--profile-id");  psi.ArgumentList.Add(profileId);
        psi.ArgumentList.Add("--fingerprint-mode"); psi.ArgumentList.Add(fingerprintMode);
        psi.ArgumentList.Add("--seed");        psi.ArgumentList.Add(seed);
        psi.ArgumentList.Add("--ca-cert");     psi.ArgumentList.Add(caCertPath);
        psi.ArgumentList.Add("--ca-key");      psi.ArgumentList.Add(caKeyPath);
        if (!string.IsNullOrWhiteSpace(upstreamProxy))
        {
            psi.ArgumentList.Add("--upstream-proxy");
            psi.ArgumentList.Add(upstreamProxy);
        }

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Debug.WriteLine($"[tls-sidecar] {e.Data}");
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Debug.WriteLine($"[tls-sidecar] {e.Data}");
        };

        if (!proc.Start())
            throw new InvalidOperationException("failed to start sidecar process");
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        var templateId = await WaitForHealthAsync(port + 1, readyTimeout, ct).ConfigureAwait(false);
        return new SidecarProcess(proc, port, sidecarPath, profileId, templateId);
    }

    private static async Task<string> WaitForHealthAsync(int healthPort, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var url = $"http://127.0.0.1:{healthPort}/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    // Body is JSON: {"template":"Chrome_106_Shuffle", ...}
                    var template = ExtractJsonString(body, "template") ?? "unknown";
                    return template;
                }
                lastError = new InvalidOperationException($"health returned {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        throw new TimeoutException($"sidecar did not become ready on {url} within {timeout.TotalSeconds:F1}s: {lastError?.Message}");
    }

    private static string? ExtractJsonString(string json, string key)
    {
        var needle = "\"" + key + "\":\"";
        var i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + needle.Length;
        var end = json.IndexOf('"', start);
        if (end < 0) return null;
        return json.Substring(start, end - start);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* best-effort */ }
        try { await _proc.WaitForExitAsync().ConfigureAwait(false); } catch { }
        _proc.Dispose();
    }
}
