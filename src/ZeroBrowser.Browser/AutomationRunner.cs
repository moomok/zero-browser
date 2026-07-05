using System.Diagnostics;
using System.Text;

namespace ZeroBrowser.Browser;

/// <summary>
/// Spawns a user-supplied Playwright/Puppeteer script attached to a running profile session.
/// The script receives the profile's browser WebSocket endpoint via environment variable
/// so it can attach without launching its own browser instance.
/// </summary>
public sealed class AutomationRunner
{
    /// <summary>
    /// Run a Node.js script against a live browser session.
    /// The script receives CDP_WS_URL as env var pointing to the browser's DevTools endpoint.
    /// </summary>
    /// <param name="cdpWebSocketUrl">CDP WebSocket debug URL of the running browser (get via session).</param>
    /// <param name="scriptPath">Absolute path to a .js / .mjs script that uses playwright/puppeteer.</param>
    /// <param name="timeoutMs">Max time to wait for script completion.</param>
    public static async Task<AutomationResult> RunAsync(string cdpWebSocketUrl, string scriptPath, int timeoutMs = 60_000)
    {
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("Script not found", scriptPath);

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["CDP_WS_URL"] = cdpWebSocketUrl;
        psi.Environment["ZB_PROFILE_MODE"] = "1";

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var completed = process.WaitForExit(timeoutMs);
        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            return new AutomationResult(false, stdout.ToString(), stderr.ToString(), timeoutMs, "Script timed out.");
        }

        var success = process.ExitCode == 0;
        var message = success ? "Script completed successfully." : $"Script exited with code {process.ExitCode}.";
        return new AutomationResult(success, stdout.ToString(), stderr.ToString(), (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - process.StartTime.Ticks / TimeSpan.TicksPerMillisecond), message);
    }
}

public sealed record AutomationResult(
    bool Success,
    string StandardOutput,
    string StandardError,
    long DurationMs,
    string Message);