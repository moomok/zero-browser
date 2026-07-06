using Avalonia.Controls;
using ZeroBrowser.App.ViewModels;

namespace ZeroBrowser.App.Views;

public partial class AutomationRunnerWindow : Window
{
    public AutomationRunnerWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is AutomationRunnerViewModel vm)
                vm.SetOwner(this);
        };
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
