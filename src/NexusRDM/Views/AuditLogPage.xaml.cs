using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

public sealed partial class AuditLogPage : Page
{
    public AuditLogViewModel ViewModel { get; }

    public AuditLogPage()
    {
        ViewModel = App.Services.GetRequiredService<AuditLogViewModel>();
        InitializeComponent();
        _ = ViewModel.LoadAsync();
    }
}
