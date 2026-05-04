using System.Runtime.InteropServices;

namespace ZeroBrowser.Browser;

/// <summary>
/// Locates installed Chromium-based browsers (Google Chrome, Brave, Microsoft
/// Edge, Vivaldi, Opera) on the host system. Used by the profile editor so a
/// user can opt in to launching with a real branded build — for example to
/// gain access to the Chrome Web Store, which is disabled in the bundled
/// Chromium-for-Testing build that ships with PuppeteerSharp.
/// </summary>
public static class BrowserDetector
{
    /// <summary>
    /// Enumerate all Chromium-based browsers we can find on disk.
    /// Returns each candidate exactly once even if it appears in multiple
    /// standard install locations.
    /// </summary>
    public static IReadOnlyList<DetectedBrowser> Detect()
    {
        var candidates = GetCandidates();
        var found = new List<DetectedBrowser>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c.Path)) continue;
            if (!File.Exists(c.Path))         continue;
            if (!seen.Add(c.Path))            continue;
            found.Add(c);
        }

        return found;
    }

    private static List<DetectedBrowser> GetCandidates()
    {
        var list = new List<DetectedBrowser>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Standard install locations on Windows.
            string[] roots =
            [
                Environment.GetEnvironmentVariable("ProgramFiles")        ?? @"C:\Program Files",
                Environment.GetEnvironmentVariable("ProgramFiles(x86)")   ?? @"C:\Program Files (x86)",
                Environment.GetEnvironmentVariable("LocalAppData")        ?? string.Empty
            ];

            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
            {
                list.Add(new DetectedBrowser(BrowserKind.Chrome,    "Google Chrome",    Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe")));
                list.Add(new DetectedBrowser(BrowserKind.Brave,     "Brave",            Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")));
                list.Add(new DetectedBrowser(BrowserKind.Edge,      "Microsoft Edge",   Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe")));
                list.Add(new DetectedBrowser(BrowserKind.Vivaldi,   "Vivaldi",          Path.Combine(root, "Vivaldi", "Application", "vivaldi.exe")));
                list.Add(new DetectedBrowser(BrowserKind.Opera,     "Opera",            Path.Combine(root, "Opera", "launcher.exe")));
                list.Add(new DetectedBrowser(BrowserKind.Chromium,  "Chromium",         Path.Combine(root, "Chromium", "Application", "chrome.exe")));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            list.Add(new DetectedBrowser(BrowserKind.Chrome,   "Google Chrome",  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"));
            list.Add(new DetectedBrowser(BrowserKind.Brave,    "Brave",          "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser"));
            list.Add(new DetectedBrowser(BrowserKind.Edge,     "Microsoft Edge", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"));
            list.Add(new DetectedBrowser(BrowserKind.Vivaldi,  "Vivaldi",        "/Applications/Vivaldi.app/Contents/MacOS/Vivaldi"));
            list.Add(new DetectedBrowser(BrowserKind.Opera,    "Opera",          "/Applications/Opera.app/Contents/MacOS/Opera"));
            list.Add(new DetectedBrowser(BrowserKind.Chromium, "Chromium",       "/Applications/Chromium.app/Contents/MacOS/Chromium"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Most Linux distros symlink browser binaries into /usr/bin or /usr/local/bin.
            string[] dirs = ["/usr/bin", "/usr/local/bin", "/snap/bin", "/var/lib/flatpak/exports/bin"];
            (BrowserKind Kind, string Display, string[] Names)[] specs =
            [
                (BrowserKind.Chrome,   "Google Chrome",  new[] { "google-chrome",          "google-chrome-stable" }),
                (BrowserKind.Brave,    "Brave",          new[] { "brave-browser",          "brave" }),
                (BrowserKind.Edge,     "Microsoft Edge", new[] { "microsoft-edge",         "microsoft-edge-stable" }),
                (BrowserKind.Vivaldi,  "Vivaldi",        new[] { "vivaldi",                "vivaldi-stable" }),
                (BrowserKind.Opera,    "Opera",          new[] { "opera" }),
                (BrowserKind.Chromium, "Chromium",       new[] { "chromium",               "chromium-browser" })
            ];

            foreach (var dir in dirs)
            {
                foreach (var spec in specs)
                {
                    foreach (var n in spec.Names)
                    {
                        list.Add(new DetectedBrowser(spec.Kind, spec.Display, Path.Combine(dir, n)));
                    }
                }
            }
        }

        return list;
    }
}

public enum BrowserKind
{
    Chromium,   // open-source Chromium build
    Chrome,     // Google Chrome (branded)
    Brave,
    Edge,
    Vivaldi,
    Opera
}

public sealed record DetectedBrowser(BrowserKind Kind, string DisplayName, string Path)
{
    public override string ToString() => $"{DisplayName} — {Path}";
}
