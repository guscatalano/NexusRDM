using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusRDM.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Nexus RDM";
}
