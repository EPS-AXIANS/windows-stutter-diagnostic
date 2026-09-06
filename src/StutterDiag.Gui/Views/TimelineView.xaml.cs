using System.Windows;
using System.Windows.Controls;
using StutterDiag.Gui.ViewModels;

namespace StutterDiag.Gui.Views;

public partial class TimelineView : UserControl
{
    private TimelineViewModel? _boundVm;

    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (DataContext is TimelineViewModel vm)
        {
            _boundVm = vm;
            _boundVm.JumpRequested += OnJumpRequested;
        }
    }

    private void Detach()
    {
        if (_boundVm is not null)
            _boundVm.JumpRequested -= OnJumpRequested;
        _boundVm = null;
    }

    private void OnJumpRequested(object? sender, int direction) => TlCanvas.JumpToStutter(direction);

    private void OnFitClick(object sender, RoutedEventArgs e) => TlCanvas.ZoomToFit();
}
