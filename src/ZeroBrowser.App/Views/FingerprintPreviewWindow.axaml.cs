using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ZeroBrowser.App.Views;

public partial class FingerprintPreviewWindow : Window
{
    public FingerprintPreviewWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
