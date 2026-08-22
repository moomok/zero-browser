using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ZeroBrowser.Browser.Tor;

/// <summary>
/// Manages a local Tor daemon for per-profile traffic routing.
///
/// Two modes:
///  - <b>External</b>: a tor already listening on the default SOCKS port (9050) —
///    we reuse it read-only. Circuit isolation still works because isolation is
///    driven by per-profile SOCKS5 credentials, which any tor honours.
///  - <b>Managed</b>: we spawn our own tor with a dedicated data directory,
///    cookie-authenticated control port, and free ports. Supports New Identity
///    (SIGNAL NEWNYM) and graceful shutdown.
/// </summary>
public sealed partial class TorManager : IDisposable
{
    /// <summary>Shared instance rooted at %LOCALAPPDATA%/ZeroBrowser/tor.</summary>
    public static TorManager Shared { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroBrowser", "tor"));

    public const int DefaultSocksPort = 9050;
    private const int DefaultControlPort = 9051;

    private readonly string _dataDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _socksPort;
    private int _controlPort;
    private string _cookieHex = string.Empty;

    public enum TorState { Stopped, Starting, RunningManaged, RunningExternal, Failed }

    // --- observable state (polled/subscribed by the UI layer) ---
    public TorState State { get; private set; } = TorState.Stopped;
    public int BootstrapPercent { get; private set; }
    public string BootstrapSummary { get; private set; } = string.Empty;
    public string? LastError { get; private set; }
    public string BinaryPath { get; private set; } = string.Empty;
    public bool IsRunning => State is TorState.RunningManaged or TorState.RunningExternal;
    public bool SupportsNewIdentity => State == TorState.RunningManaged;

    public event Action? StateChanged;

    public (string host, int port) SocksEndpoint => ("127.0.0.1", _socksPort == 0 ? DefaultSocksPort : _socksPort);

    public TorManager(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
    }

    private void SetState(TorState state, string? error = null)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Make sure a usable Tor SOCKS endpoint exists: reuse an external daemon
    /// when one answers on 127.0.0.1:{DefaultSocksPort}, otherwise spawn ours.
    /// Waits for bootstrap completion before returning. Safe to call repeatedly.
    /// </summary>
    public async Task<(string host, int port)> EnsureRunningAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning) return SocksEndpoint;

            // Probe for an external tor first.
            if (await PortOpenAsync("127.0.0.1", DefaultSocksPort, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false))
            {
                _socksPort = DefaultSocksPort;
                _controlPort = 0;
                BootstrapPercent = 100;
                BootstrapSummary = "external daemon";
                SetState(TorState.RunningExternal);
                return SocksEndpoint;
            }

            return await StartManagedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string host, int port)> StartManagedAsync(CancellationToken ct)
    {
        var binary = FindTorBinary()
            ?? throw new TorException(
                "tor binary not found. Install Tor (apt install tor / brew install tor / Tor Browser) " +
                "or set ZB_TOR_PATH to the full path of the executable.");

        var socksPort = await FreePortOrAsync(DefaultSocksPort).ConfigureAwait(false);
        var controlPort = await FreePortOrAsync(DefaultControlPort).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Unique per-profile SOCKS5 credentials give each profile its own circuit
        // chain; the extra Isolate* flags prevent sharing circuits between
        // destinations within one profile (matches Tor Browser stream isolation).
        psi.ArgumentList.Add("--Log");
        psi.ArgumentList.Add("notice stdout");
        psi.ArgumentList.Add("--SocksPort");
        psi.ArgumentList.Add($"127.0.0.1:{socksPort} IsolateDestAddr IsolateDestPort");
        psi.ArgumentList.Add("--ControlPort");
        psi.ArgumentList.Add($"127.0.0.1:{controlPort}");
        psi.ArgumentList.Add("--CookieAuthentication");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--DataDirectory");
        psi.ArgumentList.Add(_dataDir);
        psi.ArgumentList.Add("--CacheDirectory");
        psi.ArgumentList.Add(_dataDir);

        SetState(TorState.Starting);
        BootstrapPercent = 0;
        BootstrapSummary = "starting…";

        BinaryPath = binary;
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var bootstrapRe = BootstrappedLine();

        proc.OutputDataReceived += (_, e) => OnTorOutput(e.Data, bootstrapRe);
        proc.ErrorDataReceived += (_, e) => OnTorOutput(e.Data, bootstrapRe);
        proc.Exited += (_, _) =>
        {
            if (State != TorState.Stopped)
                SetState(TorState.Failed, $"tor exited (code {proc.ExitCode}).");
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            SetState(TorState.Failed, $"could not start tor: {ex.Message}");
            throw new TorException($"Could not start tor ({binary}): {ex.Message}", ex);
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        _process = proc;
        _socksPort = socksPort;
        _controlPort = controlPort;

        try
        {
            await WaitForBootstrapAsync(ct).ConfigureAwait(false);
            _cookieHex = await ReadControlCookieAsync().ConfigureAwait(false);
            SetState(TorState.RunningManaged);
            return SocksEndpoint;
        }
        catch
        {
            TryKillProcess(proc);
            _process = null;
            throw;
        }
    }

    private void OnTorOutput(string? line, Regex bootstrapRe)
    {
        if (line is null) return;
        var m = bootstrapRe.Match(line);
        if (!m.Success) return;
        var pct = int.Parse(m.Groups[1].Value);
        BootstrapPercent = pct;
        BootstrapSummary = m.Groups[2].Value.Trim();
        StateChanged?.Invoke();
    }

    private async Task WaitForBootstrapAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (BootstrapPercent < 100 && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
                throw new TorException($"tor exited during bootstrap: {LastError ?? BootstrapSummary}");
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        if (BootstrapPercent < 100)
            throw new TorException($"tor bootstrap stalled at {BootstrapPercent}% ({BootstrapSummary}).");
    }

    private async Task<string> ReadControlCookieAsync()
    {
        var cookiePath = Path.Combine(_dataDir, "control_auth_cookie");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(cookiePath))
        {
            if (DateTime.UtcNow > deadline)
                throw new TorException("control_auth_cookie never appeared — cannot talk to control port.");
            await Task.Delay(200).ConfigureAwait(false);
        }
        return Convert.ToHexString(await File.ReadAllBytesAsync(cookiePath).ConfigureAwait(false));
    }

    /// <summary>Stop the managed daemon (graceful SIGNAL SHUTDOWN, then kill).</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == TorState.RunningExternal)
            {
                SetState(TorState.Stopped);
                return; // nothing to stop — we don't own the external daemon
            }
            if (State == TorState.RunningManaged)
            {
                try
                {
                    using var ctrl = await TorControlClient.ConnectAsync("127.0.0.1", _controlPort, _cookieHex)
                        .ConfigureAwait(false);
                    await ctrl.CommandAsync("SIGNAL SHUTDOWN").ConfigureAwait(false);
                }
                catch
                {
                    // fall through to hard kill
                }
                var proc = _process;
                if (proc is not null)
                {
                    var exited = await WaitForExitAsync(proc, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (!exited) TryKillProcess(proc);
                }
            }
            SetState(TorState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Request fresh circuits (control-port SIGNAL NEWNYM). Only available for
    /// the managed daemon. Tor rate-limits NEWNYM internally (~10 s minimum).
    /// </summary>
    public async Task NewIdentityAsync(CancellationToken ct = default)
    {
        if (!SupportsNewIdentity)
            throw new TorException("New Identity requires the app-managed tor instance " +
                                   "(an external daemon's control port is not accessible).");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var ctrl = await TorControlClient.ConnectAsync("127.0.0.1", _controlPort, _cookieHex, ct)
                .ConfigureAwait(false);
            var reply = await ctrl.CommandAsync("SIGNAL NEWNYM", ct).ConfigureAwait(false);
            if (!reply.IsOk)
                throw new TorException($"NEWNYM rejected: {reply.Line}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Exit-node check (through the SOCKS5 tunnel)
    // ------------------------------------------------------------------

    public sealed record TorExitInfo(bool IsTor, string ExitIp);

    /// <summary>Ask check.torproject.org for the current exit IP through the tunnel.</summary>
    public async Task<TorExitInfo> GetExitIpAsync(CancellationToken ct = default)
    {
        var (host, port) = SocksEndpoint;
        using var conn = await Socks5Client.ConnectAsync(host, port, "check.torproject.org", 80, ct: ct)
            .ConfigureAwait(false);

        var request =
            "GET /api/ip HTTP/1.1\r\n" +
            "Host: check.torproject.org\r\n" +
            "User-Agent: zerobrowser-tor-check\r\n" +
            "Connection: close\r\n\r\n";
        var reqBytes = Encoding.ASCII.GetBytes(request);
        await conn.Stream.WriteAsync(reqBytes, ct).ConfigureAwait(false);

        var buf = new MemoryStream();
        var chunk = new byte[4096];
        int n;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((n = await conn.Stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buf.Write(chunk, 0, n);
            if (DateTime.UtcNow > deadline) break;
        }
        var body = Encoding.UTF8.GetString(buf.ToArray());
        var jsonStart = body.IndexOf('{');
        if (jsonStart < 0) throw new TorException("Unexpected response from check.torproject.org.");

        var isTor = body.Contains("\"IsTor\":true", StringComparison.OrdinalIgnoreCase);
        var ipMatch = ExitIpRegex().Match(body[jsonStart..]);
        return new TorExitInfo(isTor, ipMatch.Success ? ipMatch.Groups[1].Value : "?");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    public static string? FindTorBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ZB_TOR_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var exeName = OperatingSystem.IsWindows() ? "tor.exe" : "tor";

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }

        IEnumerable<string> wellKnown = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tor Browser", "Browser", "TorBrowser", "Tor", "tor.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tor Browser", "Browser", "TorBrowser", "Tor", "tor.exe"),
                @"C:\Program Files\tor\tor.exe"
            ]
            : OperatingSystem.IsMacOS()
            ?
            [
                "/Applications/Tor Browser.app/Contents/MacOS/Tor/tor",
                "/opt/homebrew/bin/tor",
                "/usr/local/bin/tor"
            ]
            :
            [
                "/usr/bin/tor",
                "/usr/local/bin/tor",
                "/usr/sbin/tor",
                "/snap/bin/tor"
            ];

        return wellKnown.FirstOrDefault(File.Exists);
    }

    private static async Task<bool> PortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Use the well-known port when free, otherwise grab an ephemeral one.</summary>
    private static async Task<int> FreePortOrAsync(int preferred)
    {
        if (await PortOpenAsync("127.0.0.1", preferred, TimeSpan.FromMilliseconds(250), CancellationToken.None)
                .ConfigureAwait(false))
        {
            return FindFreeTcpPort();
        }
        return preferred;
    }

    private static int FindFreeTcpPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => tcs.TrySetResult();
        if (proc.HasExited) return true;
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == tcs.Task && proc.HasExited;
    }

    private static void TryKillProcess(Process? proc)
    {
        if (proc is null) return;
        try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        TryKillProcess(_process);
        _process?.Dispose();
        _gate.Dispose();
    }

    [GeneratedRegex(@"Bootstrapped (\d+)%\s*\(([^)]*)\)")]
    internal static partial Regex BootstrappedLine();

    [GeneratedRegex(@"""IP"":\s*""([^""]+)""")]
    private static partial Regex ExitIpRegex();
}

public sealed class TorException : Exception
{
    public TorException(string message) : base(message) { }
    public TorException(string message, Exception inner) : base(message, inner) { }
}
