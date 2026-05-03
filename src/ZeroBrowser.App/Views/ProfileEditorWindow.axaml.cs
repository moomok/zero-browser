using Avalonia.Controls;
using ZeroBrowser.App.ViewModels;

namespace ZeroBrowser.App.Views;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is ProfileEditorViewModel vm)
            {
                vm.Saved     += _ => Close();
                vm.Cancelled += () => Close();
            }
        };
    }
}
