using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StutterDiag.Gui.Models;
using StutterDiag.Gui.Services;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.ViewModels;

/// <summary>
/// Full report generation: format, output folder, redaction toggles and session selection,
/// then <c>GenerateReportAsync</c>. Findings live in the report, not here.
/// </summary>
public sealed partial class ReportViewModel : ViewModelBase
{
    public ReportViewModel(ServiceConnection service) : base(service)
    {
    }

    public IReadOnlyList<string> Formats { get; } = new[] { "Html", "Json", "Csv", "Zip" };

    public ObservableCollection<SelectableSession> Sessions { get; } = new();

    [ObservableProperty]
    private string _selectedFormat = "Html";

    [ObservableProperty]
    private string _outputFolder = Shell.DefaultReportDirectory();

    [ObservableProperty]
    private bool _includeProcessNames = true;

    [ObservableProperty]
    private bool _includeUserNames;

    [ObservableProperty]
    private bool _includeCommandLines;

    [ObservableProperty]
    private bool _includeRawEventXml = true;

    [ObservableProperty]
    private bool _includeFullEventLog = true;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _resultPath;

    private bool CanLoad() => IsServiceAvailable && !Busy;

    private bool CanGenerate() =>
        IsServiceAvailable && !Busy &&
        !string.IsNullOrWhiteSpace(OutputFolder) &&
        Sessions.Any(s => s.IsSelected);

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadSessionsAsync()
    {
        Busy = true;
        RaiseCanExecute();
        try
        {
            var sessions = await Service.ListSessionsAsync().ConfigureAwait(true);

            foreach (var s in Sessions) s.PropertyChanged -= OnSelectionChanged;
            Sessions.Clear();
            foreach (var s in sessions)
            {
                var wrapper = new SelectableSession(s);
                wrapper.PropertyChanged += OnSelectionChanged;
                Sessions.Add(wrapper);
            }
        }
        finally
        {
            Busy = false;
            RaiseCanExecute();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        var chosen = Sessions.Where(s => s.IsSelected).Select(s => s.Session.Id).ToArray();
        if (chosen.Length == 0) return;

        Busy = true;
        RaiseCanExecute();
        try
        {
            Directory.CreateDirectory(OutputFolder);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var outPath = SelectedFormat.Equals("Zip", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(OutputFolder, $"stutterdiag-report-{stamp}.zip")
                : Path.Combine(OutputFolder, $"stutterdiag-report-{stamp}.{SelectedFormat.ToLowerInvariant()}");

            var request = new ReportRequestDto
            {
                Format = SelectedFormat,
                OutputPath = outPath,
                SessionIds = chosen,
                CompareMode = false,
                IncludeProcessNames = IncludeProcessNames,
                IncludeUserNames = IncludeUserNames,
                IncludeCommandLines = IncludeCommandLines,
                IncludeRawEventXml = IncludeRawEventXml,
                IncludeFullEventLog = IncludeFullEventLog
            };

            var produced = await Service.GenerateReportAsync(request).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(produced))
            {
                StatusText = "The service did not return a report path.";
                ResultPath = null;
            }
            else
            {
                ResultPath = produced;
                StatusText = $"Report generated: {produced}";
            }
        }
        finally
        {
            Busy = false;
            RaiseCanExecute();
        }
    }

    private bool CanOpenResult() => !string.IsNullOrWhiteSpace(ResultPath);

    [RelayCommand(CanExecute = nameof(CanOpenResult))]
    private void OpenResult() => Shell.OpenPath(ResultPath);

    [RelayCommand(CanExecute = nameof(CanOpenResult))]
    private void OpenFolder() => Shell.OpenContainingFolder(ResultPath);

    partial void OnOutputFolderChanged(string value) => RaiseCanExecute();

    partial void OnResultPathChanged(string? value)
    {
        OpenResultCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    protected override void OnServiceAvailabilityChanged() => RaiseCanExecute();

    private void OnSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableSession.IsSelected))
            RaiseCanExecute();
    }

    private void RaiseCanExecute()
    {
        LoadSessionsCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        OpenResultCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }
}
