using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using StutterDiag.Gui.Services;
using StutterDiag.Gui.ViewModels;

namespace StutterDiag.Gui;

/// <summary>
/// Application entry point. Single-instance (a named <see cref="Mutex"/>; a second launch
/// signals the first to surface). Constructs the small service graph
/// (<see cref="ServiceConnection"/> + <see cref="StatusPoller"/> + hotkey/toast), the tray
/// icon and the main window. The window closes to the tray; only "Exit" really exits.
/// This process NEVER runs elevated (see app.manifest).
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\StutterDiag.Gui.SingleInstance";
    private const string ShowEventName = @"Local\StutterDiag.Gui.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;

    private ServiceConnection? _connection;
    private StatusPoller? _poller;
    private HotkeyService? _hotkey;
    private ToastService? _toast;
    private MainViewModel? _main;
    private MainWindow? _window;
    private TaskbarIcon? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isNew);
        if (!isNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        // Tray app: the window closing must not end the process — only "Exit" does.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);

        GuiLog.Info($"GUI starting. Log file: {GuiLog.FilePath}");

        // Never let an exception reach the dispatcher and kill the app.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            GuiLog.Error("AppDomain unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            GuiLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        ApplyOsTheme();

        var pipeName = ResolvePipeName(e.Args);
        GuiLog.Info($"Using IPC pipe name '{pipeName}'.");

        _connection = new ServiceConnection(pipeName);
        _poller = new StatusPoller(_connection);
        _toast = new ToastService();
        _hotkey = new HotkeyService(_connection, _toast);
        _main = new MainViewModel(_connection, _poller);

        _main.Settings.Saved += (_, hotkeySpec) => _hotkey!.UpdateHotkey(hotkeySpec);

        _window = new MainWindow { DataContext = _main };
        _window.CloseToTrayRequested += (_, _) =>
        {
            GuiLog.Info("Main window hidden to tray.");
            _poller?.SetForeground(false);
        };

        CreateTrayIcon();

        _poller.Start();
        _hotkey.Initialize("Ctrl+Alt+F12");           // default; refined below once config is read
        _ = RefineHotkeyFromConfigAsync();

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        GuiLog.Error("Unhandled dispatcher exception (suppressed)", e.Exception);
        e.Handled = true;
    }

    private void SignalExistingInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowEventName);
            handle.Set();
        }
        catch (Exception ex)
        {
            GuiLog.Warn($"Could not signal the running instance: {ex.Message}");
        }
    }

    private void ShowMainWindow()
    {
        if (_exiting || _window is null) return;

        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;

        _poller?.SetForeground(true);
        _main?.RetryPendingLoads();
    }

    private void CreateTrayIcon()
    {
        try
        {
            _tray = new TaskbarIcon
            {
                ToolTipText = "StutterDiag",
                // Icon drawn with pure WPF (no System.Drawing dependency); Hardcodet
                // rasterises the ImageSource to an HICON internally.
                IconSource = BuildTrayIconSource()
            };
            _tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
            _tray.ContextMenu = BuildTrayMenu();
        }
        catch (Exception ex)
        {
            GuiLog.Error("Tray icon creation failed; continuing window-only", ex);
        }
    }

    private static ImageSource BuildTrayIconSource()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)), null,
                new Rect(0, 0, 32, 32), 6, 6);
            dc.DrawEllipse(Brushes.White, null, new Point(16, 16), 7, 7);
        }

        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Show" };
        show.Click += (_, _) => ShowMainWindow();

        var toggle = new MenuItem
        {
            Header = "Start / Stop monitoring",
            Command = _main!.Dashboard.ToggleMonitoringCommand
        };

        var report = new MenuItem { Header = "Generate report" };
        report.Click += (_, _) =>
        {
            ShowMainWindow();
            _main!.Navigate("Report");
        };

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApplication();

        menu.Items.Add(show);
        menu.Items.Add(toggle);
        menu.Items.Add(report);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private async Task RefineHotkeyFromConfigAsync()
    {
        try
        {
            var json = await _connection!.GetConfigJsonAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(json)) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("stutterDiag", out var wrapped))
                root = wrapped;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("gamingMode", out var gm) &&
                gm.TryGetProperty("hotkey", out var hk) &&
                hk.ValueKind == JsonValueKind.String &&
                hk.GetString() is { Length: > 0 } spec)
            {
                _hotkey!.UpdateHotkey(spec);
                GuiLog.Info($"Gaming-Mode hotkey refined from config: {spec}");
            }
        }
        catch (Exception ex)
        {
            GuiLog.Warn($"Could not refine hotkey from config: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        GuiLog.Info("GUI exiting on user request.");

        try { _poller?.Stop(); } catch { /* ignore */ }
        try { _hotkey?.Dispose(); } catch { /* ignore */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _main?.Dispose(); } catch { /* ignore */ }

        if (_connection is not null)
        {
            try { _connection.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); }
            catch { /* ignore */ }
        }

        try { _window?.ForceClose(); } catch { /* ignore */ }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _showRegistration?.Unregister(null); } catch { /* ignore */ }
        try { _showEvent?.Dispose(); } catch { /* ignore */ }
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        try { _mutex?.Dispose(); } catch { /* ignore */ }
        GuiLog.Info("GUI process exited.");
        base.OnExit(e);
    }

    // ---- Theme -----------------------------------------------------------------------

    private void ApplyOsTheme()
    {
        try
        {
            var uri = new Uri(IsOsDarkTheme() ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
            Resources.MergedDictionaries[1] = new ResourceDictionary { Source = uri };
        }
        catch (Exception ex)
        {
            GuiLog.Warn($"Theme detection failed, keeping light theme: {ex.Message}");
        }
    }

    private static bool IsOsDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- Pipe-name resolution ------------------------------------------------------

    /// <summary>
    /// Resolves the IPC pipe name. Priority: <c>--pipe &lt;name&gt;</c> arg, then env var
    /// <c>STUTTERDIAG_PIPE</c>, then <c>%LOCALAPPDATA%\StutterDiag\gui.settings.json</c>
    /// (<c>ipcPipeName</c>), then the built-in default <c>"StutterDiag.Service"</c>
    /// (== <c>AppConfig.Service.IpcPipeName</c> default). The GUI cannot read the service's
    /// own config before connecting, so this GUI-side override chain exists for non-default
    /// deployments.
    /// </summary>
    internal static string ResolvePipeName(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--pipe", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1];
        }

        var env = Environment.GetEnvironmentVariable("STUTTERDIAG_PIPE");
        if (!string.IsNullOrWhiteSpace(env)) return env!;

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StutterDiag", "gui.settings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("ipcPipeName", out var v) &&
                    v.ValueKind == JsonValueKind.String &&
                    v.GetString() is { Length: > 0 } name)
                    return name;
            }
        }
        catch (Exception ex)
        {
            GuiLog.Warn($"Could not read gui.settings.json: {ex.Message}");
        }

        return "StutterDiag.Service";
    }
}
