using DreamOfElectricStorage.Core;
using Microsoft.UI.Xaml.Controls;

namespace DreamOfElectricStorage.App;

/// <summary>
/// The main content page displayed inside the application window.
/// Add your UI logic, event handlers, and data binding here.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();

        // Placeholder use of the Core layer until the real graph view lands.
        var sample = new FileNode(Id: 1, ParentId: 0, Name: "C:", SizeBytes: 0, IsDirectory: true);
        StatusText.Text = $"Core linked. Sample node: {sample.Name} (dir={sample.IsDirectory}).";
    }
}
