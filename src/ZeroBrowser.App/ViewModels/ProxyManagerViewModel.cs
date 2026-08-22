using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroBrowser.Browser.Tor;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Core.Util;
using ZeroBrowser.Storage.Sqlite;

namespace ZeroBrowser.App.ViewModels;

public sealed partial class ProxyManagerViewModel : ObservableObject
{
    private readonly ProxyRepository _repo;

    public ObservableCollection<ProxyItemViewModel> Proxies { get; } = new();

    [ObservableProperty] private string _bulkInput = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // --- Tor panel state ---
    [ObservableProperty] private string _torStateText = "stopped";
    [ObservableProperty] private int _torBootstrapPercent;
    [ObservableProperty] private string _torDetail = string.Empty;
    [ObservableProperty] private bool _isTorBusy;
    [ObservableProperty] private string _torExitIp = string.Empty;

    public TorManager Tor => TorManager.Shared;

    public ProxyManagerViewModel(ProxyRepository repo)
    {
        _repo = repo;
        Reload();

        Tor.StateChanged += () => Dispatcher.UIThread.Post(RefreshTorState);
        RefreshTorState();
    }

    private void RefreshTorState()
    {
        TorStateText = Tor.State switch
        {
            TorManager.TorState.Stopped         => "stopped",
            TorManager.TorState.Starting        => "starting…",
            TorManager.TorState.RunningManaged  => "running (managed)",
            TorManager.TorState.RunningExternal => "running (external)",
            TorManager.TorState.Failed          => "failed",
            _ => "?"
        };
        TorBootstrapPercent = Tor.BootstrapPercent;
        TorDetail = Tor.LastError ?? Tor.BootstrapSummary;
        OnPropertyChanged(nameof(IsTorRunning));
        OnPropertyChanged(nameof(CanNewIdentity));
    }

    public bool IsTorRunning => Tor.IsRunning;
    public bool CanNewIdentity => Tor.SupportsNewIdentity;

    /// <summary>Add a ready-to-use local Tor proxy entry and select it implicitly.</summary>
    [RelayCommand]
    private void AddTorProxy()
    {
        var exists = _repo.ListAll().Any(p =>
            p.Type == ProxyType.Tor && p.Host == "127.0.0.1" && p.Port == TorManager.DefaultSocksPort);
        if (!exists)
        {
            _repo.Insert(new ProxyEntry
            {
                Id   = Guid.NewGuid(),
                Type = ProxyType.Tor,
                Host = "127.0.0.1",
                Port = TorManager.DefaultSocksPort,
                Status = "tor"
            });
        }
        Reload();
        StatusMessage = exists ? "Tor proxy entry already present." : "Tor proxy entry added (127.0.0.1:9050).";
    }

    [RelayCommand]
    private async Task StartTorAsync()
    {
        IsTorBusy = true;
        StatusMessage = "Starting tor…";
        try
        {
            var ep = await Tor.EnsureRunningAsync();
            StatusMessage = $"tor running on {ep.host}:{ep.port}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"tor failed: {ex.Message}";
        }
        finally
        {
            IsTorBusy = false;
            RefreshTorState();
        }
    }

    [RelayCommand]
    private async Task StopTorAsync()
    {
        IsTorBusy = true;
        try
        {
            await Tor.StopAsync();
            StatusMessage = "tor stopped.";
            TorExitIp = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"tor stop failed: {ex.Message}";
        }
        finally
        {
            IsTorBusy = false;
            RefreshTorState();
        }
    }

    [RelayCommand]
    private async Task NewIdentityAsync()
    {
        try
        {
            await Tor.NewIdentityAsync();
            StatusMessage = "New tor identity requested (circuits rebuilt).";
            TorExitIp = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"New Identity failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CheckExitIpAsync()
    {
        TorExitIp = "checking…";
        try
        {
            await Tor.EnsureRunningAsync();
            var info = await Tor.GetExitIpAsync();
            TorExitIp = info.IsTor ? $"{info.ExitIp} (via tor)" : $"{info.ExitIp} (NOT via tor!)";
            StatusMessage = info.IsTor
                ? $"tor exit node confirmed: {info.ExitIp}"
                : "⚠ traffic did NOT exit through tor — check your setup.";
        }
        catch (Exception ex)
        {
            TorExitIp = "unavailable";
            StatusMessage = $"Exit check failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void Reload()
    {
        Proxies.Clear();
        foreach (var p in _repo.ListAll())
            Proxies.Add(new ProxyItemViewModel(p));
        StatusMessage = $"{Proxies.Count} proxy(ies)";
    }

    [RelayCommand]
    private void Import()
    {
        if (string.IsNullOrWhiteSpace(BulkInput))
        {
            StatusMessage = "Paste proxies into the input box first.";
            return;
        }

        var result = ProxyImporter.Parse(BulkInput);
        if (result.Proxies.Count > 0)
            _repo.InsertMany(result.Proxies);

        BulkInput = string.Empty;
        Reload();
        StatusMessage = result.Failures.Count switch
        {
            0  => $"Imported {result.Proxies.Count} proxy(ies).",
            _  => $"Imported {result.Proxies.Count}; {result.Failures.Count} failed (see lines: {string.Join(", ", result.Failures.Select(f => f.LineNumber))})"
        };
    }

    [RelayCommand]
    private void Delete(ProxyItemViewModel? item)
    {
        if (item is null) return;
        _repo.Delete(item.Entry.Id);
        Reload();
    }

    [RelayCommand]
    private async Task TestProxyAsync(ProxyItemViewModel? item)
    {
        if (item is null) return;
        item.TestStatus = "testing…";
        StatusMessage = $"Testing {item.Display}…";

        // Tor entries are tested through the real tunnel — verifies bootstrap
        // AND that traffic actually exits via a tor node.
        if (item.Entry.Type == ProxyType.Tor)
        {
            try
            {
                await Tor.EnsureRunningAsync();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var info = await Tor.GetExitIpAsync();
                sw.Stop();
                if (!info.IsTor)
                {
                    item.TestStatus = $"NOT tor ({info.ExitIp})";
                    StatusMessage = $"⚠ {item.Display} exited from {info.ExitIp} without tor.";
                    return;
                }
                item.TestStatus = $"tor OK ({sw.ElapsedMilliseconds}ms)";
                StatusMessage = $"{item.Display} — exit node {info.ExitIp} in {sw.ElapsedMilliseconds}ms";
            }
            catch (Exception ex)
            {
                item.TestStatus = "failed";
                StatusMessage = $"{item.Display} — {ex.Message}";
            }
            return;
        }

        try
        {
            var proxy = item.Entry;
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = CreateWebProxy(proxy)
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.GetAsync("https://httpbin.org/ip");
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                item.TestStatus = $"OK ({sw.ElapsedMilliseconds}ms)";
                StatusMessage = $"{item.Display} — connected in {sw.ElapsedMilliseconds}ms";
            }
            else
            {
                item.TestStatus = $"HTTP {(int)response.StatusCode}";
                StatusMessage = $"{item.Display} — HTTP {(int)response.StatusCode}";
            }
        }
        catch (TaskCanceledException)
        {
            item.TestStatus = "timeout";
            StatusMessage = $"{item.Display} — connection timed out (15s)";
        }
        catch (HttpRequestException ex)
        {
            item.TestStatus = "failed";
            StatusMessage = $"{item.Display} — {ex.Message}";
        }
        catch (Exception ex)
        {
            item.TestStatus = "error";
            StatusMessage = $"{item.Display} — {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        StatusMessage = $"Testing {Proxies.Count} proxies…";
        foreach (var p in Proxies)
        {
            await TestProxyAsync(p);
        }
        var ok = Proxies.Count(p => p.TestStatus?.StartsWith("OK") == true);
        StatusMessage = $"Done: {ok}/{Proxies.Count} proxies connected successfully.";
    }

    private static WebProxy CreateWebProxy(ProxyEntry proxy)
    {
        var scheme = proxy.Type switch
        {
            ProxyType.Http   => "http",
            ProxyType.Https  => "https",
            ProxyType.Socks5 => "socks5",
            _ => "http"
        };
        var wp = new WebProxy($"{scheme}://{proxy.Host}:{proxy.Port}");
        if (proxy.Username is not null && proxy.Password is not null)
        {
            wp.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
        }
        return wp;
    }
}

public sealed partial class ProxyItemViewModel : ObservableObject
{
    public ProxyEntry Entry { get; }

    public ProxyItemViewModel(ProxyEntry entry) { Entry = entry; }

    public string Display => $"{Entry.Type.ToString().ToLowerInvariant()}://{Entry.Host}:{Entry.Port}";
    public string Type     => Entry.Type.ToString();
    public string Host     => Entry.Host;
    public int    Port     => Entry.Port;
    public string Auth     => Entry.Username is null ? "—" : $"{Entry.Username}:••••";
    public string Status   => Entry.Status ?? "untested";

    [ObservableProperty] private string? _testStatus;
}
