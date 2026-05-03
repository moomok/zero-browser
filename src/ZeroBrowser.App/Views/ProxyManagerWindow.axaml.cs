using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ZeroBrowser.App.Views;

public partial class ProxyManagerWindow : Window
{
    public ProxyManagerWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
