using System.Collections.ObjectModel;
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
}

public sealed class ProxyItemViewModel : ObservableObject
{
    public ProxyEntry Entry { get; }

    public ProxyItemViewModel(ProxyEntry entry) { Entry = entry; }

    public string Display => $"{Entry.Type.ToString().ToLowerInvariant()}://{Entry.Host}:{Entry.Port}";
    public string Type     => Entry.Type.ToString();
    public string Host     => Entry.Host;
    public int    Port     => Entry.Port;
    public string Auth     => Entry.Username is null ? "—" : $"{Entry.Username}:••••";
    public string Status   => Entry.Status ?? "untested";
}
