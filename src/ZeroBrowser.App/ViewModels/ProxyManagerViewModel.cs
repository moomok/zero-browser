using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public ProxyManagerViewModel(ProxyRepository repo)
    {
        _repo = repo;
        Reload();
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
