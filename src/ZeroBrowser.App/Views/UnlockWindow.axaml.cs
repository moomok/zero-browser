using Avalonia.Controls;
using Avalonia.Input;
using ZeroBrowser.App.ViewModels;

namespace ZeroBrowser.App.Views;

public partial class UnlockWindow : Window
{
    public UnlockWindow()
    {
        InitializeComponent();
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is UnlockWindowViewModel vm)
        {
            vm.SubmitCommand.Execute(null);
        }
    }
}
