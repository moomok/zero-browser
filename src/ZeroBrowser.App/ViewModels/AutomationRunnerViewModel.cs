using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroBrowser.Browser;

namespace ZeroBrowser.App.ViewModels;

public sealed partial class AutomationRunnerViewModel : ObservableObject
{
    [ObservableProperty] private string _scriptPath = string.Empty;
    [ObservableProperty] private string _output = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isRunning;

    private Window? _owner;
    private Process? _currentProcess;

    /// <summary>
    /// CDP WebSocket endpoint of the last launched browser session, set by the caller.
    /// Falls back to $env:ZB_PROFILE_CDP_URL when unset.
    /// </summary>
    public string? CdpWebSocketUrl { get; set; }

    public void SetOwner(Window owner) => _owner = owner;

    [RelayCommand]
    private async Task BrowseScriptAsync()
    {
        if (_owner is null) return;
        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Node.js script",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Node.js scripts (*.js, *.mjs, *.cjs)") { Patterns = ["*.js", "*.mjs", "*.cjs"] },
                FilePickerFileTypes.All
            ]
        });
        if (files is null || files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) ScriptPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(ScriptPath) || !File.Exists(ScriptPath))
        {
            StatusMessage = "Pick a valid .js / .mjs script first.";
            return;
        }

        var cdpWsUrl = GetActiveCdpWebSocketUrl();
        if (string.IsNullOrWhiteSpace(cdpWsUrl))
        {
            StatusMessage = "No browser launched. Set $env:ZB_PROFILE_CDP_URL or paste CDP URL into script.";
        }

        IsRunning = true;
        StatusMessage = $"Running {Path.GetFileName(ScriptPath)}…";
        Output = string.Empty;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{ScriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["CDP_WS_URL"] = cdpWsUrl ?? string.Empty;
            psi.Environment["ZB_PROFILE_MODE"] = "1";

            _currentProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _currentProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    Dispatcher.UIThread.InvokeAsync(() => Output += e.Data + Environment.NewLine);
            };
            _currentProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    Dispatcher.UIThread.InvokeAsync(() => Output += "[err] " + e.Data + Environment.NewLine);
            };
            _currentProcess.Exited += (_, _) =>
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsRunning = false;
                    StatusMessage = _currentProcess is null ? "Done." : $"Exited with code {_currentProcess.ExitCode}.";
                });
            };

            try
            {
                _currentProcess.Start();
            }
            catch (Exception ex)
            {
                Output += "[host] " + ex.Message + Environment.NewLine;
                IsRunning = false;
                StatusMessage = "Could not start Node.js — is it installed?";
                return;
            }

            _currentProcess.BeginOutputReadLine();
            _currentProcess.BeginErrorReadLine();
            StatusMessage = $"Running… (pid {_currentProcess.Id})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Launch error: {ex.Message}";
            IsRunning = false;
        }
        await Task.CompletedTask;
    }

    private bool CanRun() => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        try
        {
            _currentProcess?.Kill(entireProcessTree: true);
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cancel error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Output = string.Empty;
        StatusMessage = "Cleared.";
    }

    /// <summary>
    /// Best-effort lookup for the most recent launched browser's CDP URL.
    /// Prefers the URL captured at launch time; falls back to $env:ZB_PROFILE_CDP_URL.
    /// </summary>
    private string? GetActiveCdpWebSocketUrl()
    {
        if (!string.IsNullOrWhiteSpace(CdpWebSocketUrl)) return CdpWebSocketUrl;
        var env = Environment.GetEnvironmentVariable("ZB_PROFILE_CDP_URL");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
