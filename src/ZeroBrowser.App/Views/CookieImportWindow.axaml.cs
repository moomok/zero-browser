using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ZeroBrowser.App.Views;

public partial class CookieImportWindow : Window
{
    public CookieImportWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
