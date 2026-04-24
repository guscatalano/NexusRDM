using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }
}
