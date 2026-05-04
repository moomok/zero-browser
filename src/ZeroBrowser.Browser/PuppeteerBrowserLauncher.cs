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

    public PuppeteerBrowserLauncher(FingerprintInjector injector)
    {
        _injector = injector;
    }

    public async Task<IBrowserSession> LaunchAsync(LaunchRequest request, CancellationToken ct = default)
    {
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

        var args = new List<string>
        {
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-blink-features=AutomationControlled",
            "--disable-features=IsolateOrigins,site-per-process",
            $"--lang={request.Fingerprint.PrimaryLanguage}",
            $"--accept-lang={request.Fingerprint.AcceptLanguage}"
        };

        if (request.Proxy is not null)
        {
            var scheme = request.Proxy.Type switch
            {
                ProxyType.Http   => "http",
                ProxyType.Https  => "https",
                ProxyType.Socks5 => "socks5",
                _ => "http"
            };
            args.Add($"--proxy-server={scheme}://{request.Proxy.Host}:{request.Proxy.Port}");
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

        // Proxy authentication
        if (request.Proxy is { Username: { } user, Password: { } pwd })
        {
            await page.AuthenticateAsync(new Credentials { Username = user, Password = pwd }).ConfigureAwait(false);
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

        return new PuppeteerBrowserSession(request.Profile, browser);
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
}

internal sealed class PuppeteerBrowserSession : IBrowserSession
{
    private readonly IBrowser _browser;
    public Profile Profile { get; }

    public PuppeteerBrowserSession(Profile profile, IBrowser browser)
    {
        Profile = profile;
        _browser = browser;
    }

    public bool IsRunning => !_browser.IsClosed;

    public async Task CloseAsync()
    {
        if (!_browser.IsClosed) await _browser.CloseAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _browser.Dispose();
    }
}
