using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Core.Util;
using ZeroBrowser.Storage.Cookies;

namespace ZeroBrowser.App.ViewModels;

public sealed partial class CookieImportViewModel : ObservableObject
{
    private readonly Profile _profile;

    public string Title => $"Import cookies — {_profile.Name}";

    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private string _statusMessage;
    [ObservableProperty] private int _existingCount;

    public event Action? Closed;

    public CookieImportViewModel(Profile profile)
    {
        _profile = profile;
        var existing = CookieStore.Load(profile.StoragePath);
        _existingCount = existing.Count;
        _statusMessage = existing.Count == 0
            ? "No cookies imported yet."
            : $"{existing.Count} cookie(s) currently saved for this profile.";
    }

    [RelayCommand]
    private void Import()
    {
        if (string.IsNullOrWhiteSpace(Input))
        {
            StatusMessage = "Paste cookie data first (JSON or Netscape format).";
            return;
        }

        try
        {
            var result = CookieImporter.Parse(Input);
            CookieStore.Save(_profile.StoragePath, result.Cookies);
            ExistingCount = result.Cookies.Count;
            var summary = $"Imported {result.Cookies.Count} cookie(s) ({result.Format}).";
            if (result.Warnings.Count > 0)
                summary += $" {result.Warnings.Count} warning(s): " + string.Join(" | ", result.Warnings.Take(3));
            StatusMessage = summary;
            Input = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to parse: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearAll()
    {
        CookieStore.Clear(_profile.StoragePath);
        ExistingCount = 0;
        StatusMessage = "Cleared all imported cookies for this profile.";
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
