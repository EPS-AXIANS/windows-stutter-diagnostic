using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StutterDiag.Core.Config;
using StutterDiag.Gui.Services;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.ViewModels;

/// <summary>
/// Edits the effective <see cref="AppConfig"/> (fetched/pushed as JSON over IPC). Numeric
/// fields bind straight to the nested option objects; Save round-trips through
/// <c>SetConfigJsonAsync</c> and shows any validation warnings inline. Some changes only
/// take effect after the service restarts — the view states this.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions WriteOptions = new(IpcJson.Options)
    {
        WriteIndented = true
    };

    public SettingsViewModel(ServiceConnection service) : base(service)
    {
    }

    /// <summary>The live, editable configuration. Bound two-way by the view.</summary>
    [ObservableProperty]
    private AppConfig _config = AppConfigDefaults.Create();

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private bool _loadedOnce;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>Raised after a successful save, carrying the (possibly changed) hotkey spec.</summary>
    public event EventHandler<string>? Saved;

    private bool CanWork() => IsServiceAvailable && !Busy;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task LoadAsync()
    {
        Busy = true;
        RaiseCanExecute();
        try
        {
            var json = await Service.GetConfigJsonAsync().ConfigureAwait(true);
            Warnings.Clear();
            if (json is null)
            {
                StatusText = "Could not read configuration from the service.";
                return;
            }

            var parsed = Deserialize(json);
            if (parsed is null)
            {
                StatusText = "Configuration JSON from the service could not be parsed.";
                return;
            }

            Config = parsed;
            LoadedOnce = true;
            StatusText = "Loaded current configuration.";
        }
        finally
        {
            Busy = false;
            RaiseCanExecute();
        }
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SaveAsync()
    {
        Busy = true;
        RaiseCanExecute();
        try
        {
            var json = JsonSerializer.Serialize(Config, WriteOptions);
            var warnings = await Service.SetConfigJsonAsync(json).ConfigureAwait(true);

            Warnings.Clear();
            foreach (var w in warnings) Warnings.Add(w);

            StatusText = warnings.Count == 0
                ? "Saved. Some changes take effect only after the service is restarted."
                : $"Saved with {warnings.Count} validation warning(s). Some changes need a service restart.";

            Saved?.Invoke(this, Config.GamingMode.Hotkey);
        }
        finally
        {
            Busy = false;
            RaiseCanExecute();
        }
    }

    protected override void OnServiceAvailabilityChanged() => RaiseCanExecute();

    private void RaiseCanExecute()
    {
        LoadCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static AppConfig? Deserialize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Tolerate a wrapped payload: { "stutterDiag": { … } }.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("stutterDiag", out var inner))
            {
                return inner.Deserialize<AppConfig>(IpcJson.Options);
            }

            return JsonSerializer.Deserialize<AppConfig>(json, IpcJson.Options);
        }
        catch (JsonException ex)
        {
            GuiLog.Error("Config JSON parse failed", ex);
            return null;
        }
    }
}
