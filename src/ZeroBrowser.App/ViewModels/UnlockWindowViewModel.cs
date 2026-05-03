using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroBrowser.Storage.Crypto;

namespace ZeroBrowser.App.ViewModels;

public sealed partial class UnlockWindowViewModel : ObservableObject
{
    private readonly MasterKey _masterKey;

    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _passwordConfirm = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public bool IsFirstRun { get; }
    public bool ShowConfirm => IsFirstRun;
    public string PrimaryButtonText => IsFirstRun ? "Set master password" : "Unlock";
    public string Subtitle => IsFirstRun
        ? "Choose a master password. It encrypts proxy credentials and other secrets on disk. There is no recovery — keep it safe."
        : "Enter your master password to unlock the app.";
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Set after a successful unlock/initialize. Null while still locked.</summary>
    public SecretBox? UnlockedBox { get; private set; }

    /// <summary>Raised when the user has successfully unlocked or initialized the master key.</summary>
    public event Action? Unlocked;

    public UnlockWindowViewModel(MasterKey masterKey)
    {
        _masterKey = masterKey;
        IsFirstRun = !masterKey.Exists;
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void Submit()
    {
        ErrorMessage = string.Empty;

        if (string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Password cannot be empty.";
            return;
        }

        if (IsFirstRun)
        {
            if (Password.Length < 8)
            {
                ErrorMessage = "Password must be at least 8 characters.";
                return;
            }
            if (Password != PasswordConfirm)
            {
                ErrorMessage = "Passwords do not match.";
                return;
            }
            UnlockedBox = _masterKey.Initialize(Password);
        }
        else
        {
            UnlockedBox = _masterKey.TryUnlock(Password);
            if (UnlockedBox is null)
            {
                ErrorMessage = "Incorrect password.";
                return;
            }
        }

        // Clear sensitive form state from memory before raising event.
        Password = string.Empty;
        PasswordConfirm = string.Empty;
        Unlocked?.Invoke();
    }
}
