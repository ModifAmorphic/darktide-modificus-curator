using Avalonia.Controls;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The main window: the app shell (SplitView navigation rail + hosted
/// destination content + global status strip). Its <c>DataContext</c> is set by
/// the composition root (<see cref="App.OnFrameworkInitializationCompleted"/>)
/// to the resolved <see cref="ViewModels.ShellViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
