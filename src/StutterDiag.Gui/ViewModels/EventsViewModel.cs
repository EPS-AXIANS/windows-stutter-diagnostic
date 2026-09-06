using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StutterDiag.Gui.Services;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.ViewModels;

/// <summary>
/// Recent events in a virtualized grid with category / severity / free-text filtering.
/// Purely a surface over <c>GetRecentEventsAsync</c>; no interpretation.
/// </summary>
public sealed partial class EventsViewModel : ViewModelBase
{
    private const int FetchCount = 500;
    private const string AllToken = "(all)";

    private readonly ObservableCollection<RecentEventDto> _events = new();

    public EventsViewModel(ServiceConnection service) : base(service)
    {
        EventsView = CollectionViewSource.GetDefaultView(_events);
        EventsView.Filter = FilterEvent;
        Categories = new ObservableCollection<string> { AllToken };
        Severities = new ObservableCollection<string> { AllToken };
    }

    public ICollectionView EventsView { get; }

    public ObservableCollection<string> Categories { get; }

    public ObservableCollection<string> Severities { get; }

    [ObservableProperty]
    private string _categoryFilter = AllToken;

    [ObservableProperty]
    private string _severityFilter = AllToken;

    [ObservableProperty]
    private string _textFilter = string.Empty;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private int _visibleCount;

    private bool CanRefresh() => IsServiceAvailable && !Busy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        Busy = true;
        RefreshCommand.NotifyCanExecuteChanged();
        try
        {
            var list = await Service.GetRecentEventsAsync(FetchCount).ConfigureAwait(true);

            _events.Clear();
            foreach (var e in list) _events.Add(e);

            RebuildTokens(Categories, list.Select(e => e.Category));
            RebuildTokens(Severities, list.Select(e => e.Severity));

            EventsView.Refresh();
            UpdateVisibleCount();
        }
        finally
        {
            Busy = false;
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnCategoryFilterChanged(string value) => ApplyFilter();

    partial void OnSeverityFilterChanged(string value) => ApplyFilter();

    partial void OnTextFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        EventsView.Refresh();
        UpdateVisibleCount();
    }

    private void UpdateVisibleCount() => VisibleCount = EventsView.Cast<object>().Count();

    private bool FilterEvent(object obj)
    {
        if (obj is not RecentEventDto e) return false;

        if (CategoryFilter != AllToken &&
            !string.Equals(e.Category, CategoryFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (SeverityFilter != AllToken &&
            !string.Equals(e.Severity, SeverityFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(TextFilter))
        {
            var t = TextFilter.Trim();
            bool hit =
                (e.Message?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Source?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hit) return false;
        }
        return true;
    }

    protected override void OnServiceAvailabilityChanged() => RefreshCommand.NotifyCanExecuteChanged();

    private static void RebuildTokens(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var distinct = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        target.Clear();
        target.Add(AllToken);
        foreach (var v in distinct) target.Add(v);
    }
}
