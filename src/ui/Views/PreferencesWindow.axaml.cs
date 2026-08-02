using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The "Preferences" modal dialog (theme picker / font-scale slider / language
/// dropdown / show-Relay-console checkbox). Its <c>DataContext</c> is a
/// <see cref="ViewModels.PreferencesViewModel"/> (set by
/// <see cref="Dialogs.DialogService"/>). Each control applies + persists
/// immediately through the VM (no commit step), so "Done" just closes the window.
/// </summary>
/// <remarks>
/// <para><b>Live fit:</b> the font-scale slider grows the content as the user
/// drags it, which can push "Done" off-screen. The window sets
/// <c>SizeToContent="Height"</c> in XAML so Avalonia sizes it to the content at
/// show time (which lets <c>WindowStartupLocation="CenterOwner"</c> position it
/// correctly against the real height, not a default placeholder height).
/// However, <see cref="SizeToContent"/> in Avalonia 12.x is only honored at show
/// time, so once open this window re-measures its content on a short interval
/// and sets <see cref="Window.ClientSize"/> directly to keep it fitted as the
/// slider moves: measure the root (which includes the custom title bar) at the
/// current client width with infinite height, then size the client to the
/// measured height. One pass runs on open (so the first paint is correct), then
/// a ~200ms timer keeps it fitted as the slider moves. The timer is stopped on
/// close.</para>
/// </remarks>
public partial class PreferencesWindow : Window
{
    private const double FitIntervalMs = 200;

    private DispatcherTimer? _fitTimer;

    public PreferencesWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Size correctly for the current font scale on first paint.
        FitToContent();

        _fitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FitIntervalMs) };
        _fitTimer.Tick += (_, _) => FitToContent();
        _fitTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_fitTimer is not null)
        {
            _fitTimer.Stop();
            _fitTimer = null;
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Measures <see cref="ContentRoot"/> (the framed content, including the
    /// title bar) at the current client width with infinite height and sets
    /// <see cref="Window.ClientSize"/> to the measured height. No-op until the
    /// window has a real width. Width is held fixed; only height tracks content.
    /// </summary>
    private void FitToContent()
    {
        if (ContentRoot is null)
        {
            return;
        }

        var width = ClientSize.Width > 0
            ? ClientSize.Width
            : (Bounds.Width > 0 ? Bounds.Width : Width);
        if (width <= 0)
        {
            return;
        }

        ContentRoot.InvalidateMeasure();
        ContentRoot.Measure(new Size(width, double.PositiveInfinity));

        var desired = ContentRoot.DesiredSize.Height;
        if (desired <= 0 || !double.IsFinite(desired))
        {
            return;
        }

        var target = new Size(width, desired);
        if (ClientSize != target)
        {
            ClientSize = target;
        }
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Close();
}
