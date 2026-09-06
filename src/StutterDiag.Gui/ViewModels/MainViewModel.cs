using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StutterDiag.Gui.Services;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.ViewModels;

/// <summary>One entry in the left navigation rail.</summary>
public sealed record NavItem(string Key, string Label, string Glyph);

/// <summary>
/// Shell view-model: owns the page view-models, the navigation state, and the app-wide
/// service-availability banner. It wires the <see cref="StatusPoller"/> to the dashboard.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly StatusPoller _poller;
    private readonly HashSet<string> _initialised = new(StringComparer.Ordinal);

    public MainViewModel(ServiceConnection service, StatusPoller poller)
    {
        Service = service;
        _poller = poller;

        Dashboard = new DashboardViewModel(service, Navigate);
        System = new SystemViewModel(service);
        Timeline = new TimelineViewModel(service);
        Events = new EventsViewModel(service);
        Settings = new SettingsViewModel(service);
        Compare = new CompareViewModel(service);
        Report = new ReportViewModel(service);

        NavItems = new[]
        {
            new NavItem("Dashboard", "Dashboard", ""),
            new NavItem("System", "System", ""),
            new NavItem("Timeline", "Timeline", ""),
            new NavItem("Events", "Events", ""),
            new NavItem("Compare", "Compare", ""),
            new NavItem("Report", "Report", ""),
            new NavItem("Settings", "Settings", ""),
        };

        _poller.StatusChanged += OnStatusChanged;
        Navigate("Dashboard");
    }

    public ServiceConnection Service { get; }

    public DashboardViewModel Dashboard { get; }
    public SystemViewModel System { get; }
    public TimelineViewModel Timeline { get; }
    public EventsViewModel Events { get; }
    public SettingsViewModel Settings { get; }
    public CompareViewModel Compare { get; }
    public ReportViewModel Report { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    private ObservableObject? _currentPage;

    [ObservableProperty]
    private string _currentKey = "Dashboard";

    /// <summary>Also used by the tray "Generate report" item.</summary>
    [RelayCommand]
    public void Navigate(string key)
    {
        CurrentKey = key;
        CurrentPage = key switch
        {
            "System" => System,
            "Timeline" => Timeline,
            "Events" => Events,
            "Compare" => Compare,
            "Report" => Report,
            "Settings" => Settings,
            _ => Dashboard
        };

        InitialiseOnce(key);
    }

    private void InitialiseOnce(string key)
    {
        if (!_initialised.Add(key)) return;

        switch (key)
        {
            case "System" when System.RefreshCommand.CanExecute(null):
                System.RefreshCommand.Execute(null);
                break;
            case "Timeline" when Timeline.LoadSessionsCommand.CanExecute(null):
                Timeline.LoadSessionsCommand.Execute(null);
                break;
            case "Events" when Events.RefreshCommand.CanExecute(null):
                Events.RefreshCommand.Execute(null);
                break;
            case "Compare" when Compare.LoadSessionsCommand.CanExecute(null):
                Compare.LoadSessionsCommand.Execute(null);
                break;
            case "Report" when Report.LoadSessionsCommand.CanExecute(null):
                Report.LoadSessionsCommand.Execute(null);
                break;
            case "Settings" when Settings.LoadCommand.CanExecute(null):
                Settings.LoadCommand.Execute(null);
                break;
        }
    }

    /// <summary>Retry the lazy first-load for any page skipped because the service was down.</summary>
    public void RetryPendingLoads()
    {
        foreach (var key in new[] { "System", "Timeline", "Events", "Compare", "Report", "Settings" })
        {
            if (_initialised.Contains(key)) continue;
            if (key == CurrentKey || key == "Settings")
            {
                _initialised.Remove(key);
                InitialiseOnce(key);
            }
        }
    }

    private void OnStatusChanged(object? sender, StatusDto? status) => Dashboard.ApplyStatus(status);

    public void Dispose()
    {
        _poller.StatusChanged -= OnStatusChanged;
        Dashboard.Dispose();
        System.Dispose();
        Timeline.Dispose();
        Events.Dispose();
        Settings.Dispose();
        Compare.Dispose();
        Report.Dispose();
    }
}
