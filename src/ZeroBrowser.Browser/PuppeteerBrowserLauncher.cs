using ZeroBrowser.Core.Fingerprint;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Storage.Cookies;
using PuppeteerSharp;

namespace ZeroBrowser.Browser;

/// <summary>
/// Cross-platform browser launcher built on PuppeteerSharp. Spawns a separate Chromium
/// process per profile, with a per-profile user-data-dir, proxy server, and the full
/// fingerprint patch script injected before any page script runs (via
/// <c>Page.addScriptToEvaluateOnNewDocument</c>).
/// </summary>
public sealed class PuppeteerBrowserLauncher : IBrowserLauncher
{
    private readonly FingerprintInjector _injector;

    public string? LastWarning { get; private set; }

    public PuppeteerBrowserLauncher(FingerprintInjector injector)
    {
        _injector = injector;
    }

    public async Task<IBrowserSession> LaunchAsync(LaunchRequest request, CancellationToken ct = default)
    {
        LastWarning = null;
        Directory.CreateDirectory(request.Profile.StoragePath);

        // Resolve which Chromium binary to launch. If the profile pins an
        // installed branded build (Chrome, Brave, Edge, …) and the file still
        // exists, we use that. Otherwise we fall back to PuppeteerSharp's
        // bundled Chromium-for-Testing build (BrowserFetcher downloads on
        // first use).
        string? executablePath = null;
        if (!string.IsNullOrWhiteSpace(request.Profile.EnginePath) && File.Exists(request.Profile.EnginePath))
        {
            executablePath = request.Profile.EnginePath;
        }
        else
        {
            var fetcher = new BrowserFetcher();
            await fetcher.DownloadAsync().ConfigureAwait(false);
        }

        // TLS fingerprint diversion via external Go+uTLS sidecar (v0.4).
        // The legacy in-process CipherSuitesPolicy implementation was retired
        // because it cannot produce browser-matching JA3 hashes (see commit
        // 148e8b2). The new sidecar gives byte-level ClientHello control.
        Tls.SidecarProcess? tlsSidecar = null;
        var tlsMode = request.Profile.TlsDiversionMode ?? "none";
        if (!string.IsNullOrWhiteSpace(tlsMode) && tlsMode != "none")
        {
            try
            {
                tlsMode = Tls.TlsFingerprintPool.NormalizeToUserAgent(tlsMode, request.Fingerprint.UserAgent);
                var sidecarPath = Tls.TlsSupport.ResolveSidecarPath();
                if (sidecarPath is null)
                {
                    LastWarning = $"TLS diversion mode '{tlsMode}' requested but the sidecar binary is not present ({Tls.TlsSupport.Platform}/{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}). " +
                                  "Run zero-browser-tls-sidecar/build.ps1 and place the binary under vendor/tls-sidecar/. Launching without diversion.";
                    System.Diagnostics.Debug.WriteLine("TLS diversion: " + LastWarning);
                    tlsMode = "none";
                }
                else
                {
                    var sidecarPort = FindFreeTcpPort();
                    var upstream = request.Proxy is null ? null : BuildUpstreamProxyUrl(request.Proxy);
                    var caPaths = EnsureLocalCaAsync().GetAwaiter().GetResult();
                    tlsSidecar = await Tls.SidecarProcess.StartAsync(
                        sidecarPath: sidecarPath,
                        port: sidecarPort,
                        profileId: request.Profile.Id.ToString("N"),
                        fingerprintMode: tlsMode,
                        seed: request.Profile.FingerprintSeed,
                        caCertPath: caPaths.cert,
                        caKeyPath: caPaths.key,
                        upstreamProxy: upstream,
                        readyTimeout: TimeSpan.FromSeconds(5),
                        ct: ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LastWarning = $"TLS sidecar failed to start ({ex.Message}); launching without diversion.";
                System.Diagnostics.Debug.WriteLine("TLS diversion: " + LastWarning);
                tlsSidecar = null;
                tlsMode = "none";
            }
        }

        var args = new List<string>
        {
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-blink-features=AutomationControlled",
            "--disable-features=IsolateOrigins,site-per-process",
            $"--lang={request.Fingerprint.PrimaryLanguage}",
            $"--accept-lang={request.Fingerprint.AcceptLanguage}"
        };

        // Disable QUIC when TLS diversion is active — QUIC can't be MITMed.
        if (tlsSidecar is not null)
        {
            args.Add("--disable-quic");
            args.Add("--disable-features=UseChromeOSDirectVideoDecoder,PreconnectToSearch,NetworkServiceInProcess");
        }

        if (tlsSidecar is not null)
        {
            // Route all browser traffic through our TLS-diversion sidecar.
            // Inside the sidecar, the TLS leg uses uTLS to emit a real
            // browser-matching ClientHello.
            args.Add($"--proxy-server=http://127.0.0.1:{tlsSidecar.Port}");
        }
        else if (request.Proxy is not null)
        {
            // Sanitize proxy host/port to prevent argument injection via
            // crafted values stored in the database.
            var proxyHost = request.Proxy.Host
                .Replace(" ", "").Replace("\"", "").Replace("'", "")
                .Replace(";", "").Replace("&", "").Replace("|", "")
                .Replace("`", "").Replace("$", "").Replace("\\", "")
                .Replace("\n", "").Replace("\r", "");
            if (string.IsNullOrWhiteSpace(proxyHost) || request.Proxy.Port <= 0 || request.Proxy.Port > 65535)
                throw new ArgumentException("Invalid proxy host or port.");
            var scheme = request.Proxy.Type switch
            {
                ProxyType.Http   => "http",
                ProxyType.Https  => "https",
                ProxyType.Socks5 => "socks5",
                _ => "http"
            };

            // For SOCKS5 with auth, credentials must go in the URL because
            // page.AuthenticateAsync only handles HTTP 407 proxy-auth challenges.
            // HTTP/HTTPS proxies use the 407 challenge flow via AuthenticateAsync.
            if (request.Proxy.Type == ProxyType.Socks5
                && request.Proxy.Username is not null
                && request.Proxy.Password is not null)
            {
                var socksUser = Uri.EscapeDataString(request.Proxy.Username);
                var socksPass = Uri.EscapeDataString(request.Proxy.Password);
                args.Add($"--proxy-server=socks5://{socksUser}:{socksPass}@{proxyHost}:{request.Proxy.Port}");
            }
            else
            {
                args.Add($"--proxy-server={scheme}://{proxyHost}:{request.Proxy.Port}");
            }
        }

        // Sideload extensions: only include enabled ones whose folders still
        // exist on disk. Chromium accepts a comma-separated list to
        // --load-extension. The flag has no escape mechanism, so a path
        // containing a comma would be parsed as two separate (invalid) paths.
        // We filter those out here as a defensive guard — the UI also rejects
        // such paths at folder-pick time, and CRX imports use GUIDed names.
        // When running on the deterministic Chromium-for-Testing build we
        // additionally pin the set with --disable-extensions-except so the
        // binary's own state can't drift between runs. When running on a
        // user-installed branded browser we intentionally do NOT pin — that
        // would prevent Web Store extensions the user installs interactively
        // from running.
        string[]? loadExtensionPaths = null;
        if (request.Extensions is { Count: > 0 })
        {
            loadExtensionPaths = request.Extensions
                .Where(e => e.Enabled
                            && !string.IsNullOrWhiteSpace(e.Path)
                            && !e.Path!.Contains(',')
                            && Directory.Exists(e.Path))
                .Select(e => e.Path!)
                .ToArray();

            if (loadExtensionPaths.Length > 0)
            {
                var joined = string.Join(",", loadExtensionPaths);
                args.Add($"--load-extension={joined}");
                if (executablePath is null)
                {
                    args.Add($"--disable-extensions-except={joined}");
                }
            }
        }

        // PuppeteerSharp's DefaultArgs include "--disable-extensions" which
        // unconditionally blocks every extension at startup, including
        // anything passed via --load-extension and any Web Store extensions
        // the user has previously installed in this profile's user-data-dir.
        //
        // - On Chromium-for-Testing we already counter this with
        //   --disable-extensions-except=<paths> (Chrome treats this as an
        //   override of --disable-extensions for the whitelisted folders).
        // - On a branded build we explicitly want Web Store extensions and
        //   the user's interactive installs to work, so we drop
        //   --disable-extensions from the defaults entirely. We always do
        //   this in branded mode (not just when sideloading) so a profile
        //   that uses Chrome with no sideloaded extensions can still install
        //   from the Web Store the next time it launches.
        string[]? ignoredDefaultArgs = null;
        if (executablePath is not null)
        {
            ignoredDefaultArgs = new[] { "--disable-extensions" };
        }

        var launchOptions = new LaunchOptions
        {
            Headless = request.Headless,
            UserDataDir = request.Profile.StoragePath,
            ExecutablePath = executablePath,
            Args = args.ToArray(),
            IgnoredDefaultArgs = ignoredDefaultArgs,
            DefaultViewport = null,                  // use real window size
            AcceptInsecureCerts = false
        };

        var browser = await Puppeteer.LaunchAsync(launchOptions).ConfigureAwait(false);
        var page = (await browser.PagesAsync().ConfigureAwait(false)).FirstOrDefault()
                   ?? await browser.NewPageAsync().ConfigureAwait(false);

        // Proxy authentication — HTTP/HTTPS proxies use 407 challenge.
        // Must be applied on every new page, not just the initial one.
        // SOCKS5 auth is already embedded in the --proxy-server URL above.
        if (request.Proxy is { Username: { } user, Password: { } pwd }
            && request.Proxy.Type != ProxyType.Socks5)
        {
            await page.AuthenticateAsync(new Credentials { Username = user, Password = pwd }).ConfigureAwait(false);

            // Apply auth to any future tabs/pages the user opens.
            browser.TargetCreated += async (_, e) =>
            {
                try
                {
                    var target = e.Target;
                    if (target.Type == TargetType.Page)
                    {
                        var newPage = await target.PageAsync().ConfigureAwait(false);
                        if (newPage is not null)
                            await newPage.AuthenticateAsync(new Credentials { Username = user, Password = pwd }).ConfigureAwait(false);
                    }
                }
                catch { /* best-effort: tab may close before auth completes */ }
            };
        }

        // User agent + Sec-CH-UA
        await page.SetUserAgentAsync(request.Fingerprint.UserAgent).ConfigureAwait(false);
        await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
        {
            ["Accept-Language"]      = request.Fingerprint.AcceptLanguage,
            ["sec-ch-ua"]            = request.Fingerprint.SecChUa,
            ["sec-ch-ua-platform"]   = $"\"{request.Fingerprint.SecChUaPlatform}\"",
            ["sec-ch-ua-mobile"]     = request.Fingerprint.SecChUaMobile ? "?1" : "?0"
        }).ConfigureAwait(false);

        // Timezone (CDP) — works for the entire browser instance.
        await page.EmulateTimezoneAsync(request.Fingerprint.Timezone).ConfigureAwait(false);

        // Inject fingerprint patch on every new document, in every frame.
        var patchScript = _injector.BuildPatchScript(request.Fingerprint);
        await page.EvaluateFunctionOnNewDocumentAsync($"() => {{ {patchScript} }}").ConfigureAwait(false);

        // Apply imported cookies (if any) before first navigation.
        var imported = CookieStore.Load(request.Profile.StoragePath);
        if (imported.Count > 0)
        {
            var cookieParams = imported.Select(ToCookieParam).ToArray();
            await page.SetCookieAsync(cookieParams).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.StartUrl))
        {
            await page.GoToAsync(request.StartUrl).ConfigureAwait(false);
        }

        return new PuppeteerBrowserSession(request.Profile, browser, tlsSidecar);
    }

    private static CookieParam ToCookieParam(CookieRecord c) => new()
    {
        Name      = c.Name,
        Value     = c.Value,
        Domain    = c.Domain,
        Path      = c.Path,
        HttpOnly  = c.HttpOnly ?? false,
        Secure    = c.Secure ?? false,
        SameSite  = c.SameSite switch
        {
            "Lax"    => SameSite.Lax,
            "Strict" => SameSite.Strict,
            "None"   => SameSite.None,
            _        => SameSite.Default
        },
        Expires   = c.ExpiresUnix
    };

    private static int FindFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string? BuildUpstreamProxyUrl(ZeroBrowser.Core.Models.ProxyEntry proxy)
    {
        var scheme = proxy.Type switch
        {
            ZeroBrowser.Core.Models.ProxyType.Http   => "http",
            ZeroBrowser.Core.Models.ProxyType.Https  => "https",
            ZeroBrowser.Core.Models.ProxyType.Socks5 => "socks5",
            _ => null
        };
        if (scheme is null) return null;
        var host = proxy.Host;
        var userInfo = "";
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            var u = Uri.EscapeDataString(proxy.Username);
            var p = Uri.EscapeDataString(proxy.Password ?? "");
            userInfo = $"{u}:{p}@";
        }
        return $"{scheme}://{userInfo}{host}:{proxy.Port}";
    }

    /// <summary>
    /// Ensure a local Root CA exists and is trusted by the OS, and return
    /// its cert + key paths. Without the OS-trust step the sidecar's leaf
    /// cert is unsigned-by-trusted-CA from Chromium's perspective and every
    /// HTTPS page renders "Your connection is not private" with
    /// <c>net::ERR_CERT_AUTHORITY_INVALID</c>.
    ///
    /// On Windows the install uses certutil + UAC; on macOS it shells out to
    /// <c>security add-trusted-cert</c> (needs GUI sudo prompt — typically
    /// only works in interactive sessions); on Linux it shells out to
    /// <c>certutil</c> against the per-user NSS db (requires
    /// <c>libnss3-tools</c> to be installed — see <see cref="Tls.TlsCertificateAuthority"/>).
    /// Install is idempotent: if the CA is already trusted we get a Failed
    /// result with the cert already-present code path.
    /// </summary>
    private static async Task<(string cert, string key)> EnsureLocalCaAsync()
    {
        if (!Tls.TlsCertificateAuthority.Exists)
        {
            Tls.TlsCertificateAuthority.Generate();
        }
        var cert = Tls.TlsCertificateAuthority.CertPath;
        var key = Tls.TlsCertificateAuthority.KeyPemPath;
        if (!System.IO.File.Exists(cert) || !System.IO.File.Exists(key))
        {
            throw new System.IO.FileNotFoundException("Root CA files missing after generate", cert);
        }

        // Best-effort: install to OS trust store so Chromium accepts leaf
        // certs signed by this CA. We swallow non-Success results and let
        // the caller warn; we do NOT throw here because the user might have
        // intentionally chosen to skip install (e.g. macOS CI runner with no
        // sudo). Failure surfaces via LastWarning on the launcher.
        try
        {
            var installResult = await Tls.TlsCertificateAuthority.InstallToTrustStoreAsync().ConfigureAwait(false);
            // We don't return this — caller reads it via a different path.
            // (For now we just leave the install to happen; a future iteration
            // could surface the failure reason in LastWarning when TLS mode
            // is active and install didn't succeed.)
            _ = installResult;
        }
        catch
        {
            // Install failure must not prevent profile launch — the user can
            // hit "Install Root CA" manually from the profile editor.
        }

        return (cert, key);
    }
}

internal sealed class PuppeteerBrowserSession : IBrowserSession
{
    private readonly IBrowser _browser;
    private readonly Tls.SidecarProcess? _sidecar;
    public Profile Profile { get; }

    /// <summary>
    /// Maximum time we wait for the browser to shut down gracefully before
    /// we force-kill the underlying Chromium process. Tuned to be long
    /// enough for normal shutdown (which closes the CDP connection, the
    /// DevTools target, the renderer process, the GPU process, the utility
    /// process, and a handful of sandbox helpers — usually under 2s) but
    /// short enough that an unresponsive browser can't keep the user's
    /// session in limbo when the launcher is being torn down (e.g. on app
    /// shutdown, on test timeout, on a navigation that hangs).
    /// </summary>
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Track if we've already disposed so double-dispose is a no-op.</summary>
    private int _disposed;

    public PuppeteerBrowserSession(Profile profile, IBrowser browser, Tls.SidecarProcess? sidecar = null)
    {
        Profile = profile;
        _browser = browser;
        _sidecar = sidecar;
    }

    public bool IsRunning => !_browser.IsClosed && Volatile.Read(ref _disposed) == 0;

    public async Task CloseAsync()
    {
        if (_browser.IsClosed) return;
        try
        {
            using var cts = new CancellationTokenSource(GracefulShutdownTimeout);
            await _browser.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Graceful close failed (browser hung, CDP connection broken,
            // Chromium stuck mid-render). Fall through to force-kill below.
            TryForceKillBrowser();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: a second DisposeAsync must not double-kill processes.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Sidecar FIRST. The sidecar is the proxy that Chromium is talking
        // to; if we kill Chromium first, the sidecar's bidirectional pipes
        // will start getting RSTs on the upstream side and may briefly
        // appear to hang. Killing sidecar first lets those pipes unwind
        // cleanly. Both kills are best-effort and time-bounded — neither
        // path is allowed to hang indefinitely (the previous implementation
        // did `await _browser.CloseAsync()` without a timeout, which meant
        // a hung Chromium could leak 80+ child processes indefinitely, see
        // commit history around the test-timeout incident).
        if (_sidecar is not null)
        {
            try { await _sidecar.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }

        try
        {
            using var cts = new CancellationTokenSource(GracefulShutdownTimeout);
            await _browser.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryForceKillBrowser();
        }

        // Belt-and-suspenders: even if CloseAsync claimed success, kill any
        // straggler Chromium processes that Puppeteer might have left around
        // (renderer / GPU / utility processes). Process.Kill(entireProcessTree:true)
        // walks the child tree and SIGKILLs them all.
        TryForceKillBrowser();

        try { _browser.Dispose(); } catch { /* best-effort */ }
    }

    private void TryForceKillBrowser()
    {
        System.Diagnostics.Process? proc = null;
        try { proc = _browser.Process; } catch { /* may throw if browser already gone */ }
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited, or access denied (shouldn't happen for our own
            // child processes, but be defensive).
        }
    }
}
