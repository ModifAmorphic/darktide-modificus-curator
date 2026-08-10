using Avalonia.Controls;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The inline local-import card code-behind. A typed <see cref="UserControl"/>
/// whose <c>DataContext</c> is an <see cref="ImportWorkflowViewModel"/> (reached
/// through the host <see cref="ModListViewModel.ImportWorkflow"/> child). No
/// interaction logic lives here: the card binds entirely to the workflow VM's
/// state, fields, and commands. The constructor is required for
/// <c>InitializeComponent</c> (Avalonia's compiled-XAML partial-class wiring).
/// </summary>
public partial class ImportWorkflowView : UserControl
{
    public ImportWorkflowView()
    {
        InitializeComponent();
    }
}
