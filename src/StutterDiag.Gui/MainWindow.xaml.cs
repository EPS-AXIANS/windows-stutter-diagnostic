using System.ComponentModel;
using System.Windows;

namespace StutterDiag.Gui;

/// <summary>
/// Shell window. Closing it hides to the tray (the service keeps running regardless — see
/// docs/ARCHITECTURE.md §3). A real exit is driven from <see cref="App"/> via
/// <see cref="ForceClose"/>.
/// </summary>
public partial class MainWindow : Window
{
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user closes the window and it is hidden to the tray instead.</summary>
    public event EventHandler? CloseToTrayRequested;

    /// <summary>Called by <see cref="App"/> to actually close the window on Exit.</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
            CloseToTrayRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }
}
