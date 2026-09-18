using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scrubbler.Import;
using Scrubbler.Plugin.Scrobbler.FileParseScrobbler.Parser.CSV;
using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON;

namespace Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

internal sealed partial class FileParseScrobbleViewModel
{
    [ObservableProperty] private string _importAccount = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QueueImportCommand))]
    private bool _hasImportAccount;
    [ObservableProperty] private string _importAmount = "500";
    [ObservableProperty] private string _importIntervalHours = "24";
    [ObservableProperty] private bool _importAllowParseErrors;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportStatusVisibility))]
    private string _importStatus = "";
    public Microsoft.UI.Xaml.Visibility ImportStatusVisibility => string.IsNullOrWhiteSpace(ImportStatus)
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadyForScrobbling))]
    [NotifyPropertyChangedFor(nameof(ManualTabVisibility))]
    [NotifyPropertyChangedFor(nameof(ImportsTabVisibility))]
    private int _selectedTabIndex;
    public override bool ReadyForScrobbling => SelectedTabIndex == 0;
    public Microsoft.UI.Xaml.Visibility ManualTabVisibility => SelectedTabIndex == 0
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ImportsTabVisibility => SelectedTabIndex == 1
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    [RelayCommand]
    private void ShowManualTab() => SelectedTabIndex = 0;

    [RelayCommand]
    private async Task ShowImportsTab()
    {
        SelectedTabIndex = 1;
        await RefreshImports();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportJobs))]
    [NotifyPropertyChangedFor(nameof(ImportCreationHint))]
    [NotifyCanExecuteChangedFor(nameof(QueueImportCommand))]
    private ObservableCollection<JobSummary> _importJobs = [];
    public bool HasImportJobs => ImportJobs.Count > 0;
    public string ImportCreationHint => ImportJobs.Any(j => !j.Completed)
        ? "Finish or delete the existing import before starting another." : "";
    private bool CanQueueImport => HasImportAccount && File.Exists(SelectedFilePath) && !ImportJobs.Any(j => !j.Completed);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressText))]
    [NotifyPropertyChangedFor(nameof(ImportTaskSummary))]
    [NotifyCanExecuteChangedFor(nameof(DeleteImportCommand))]
    private JobSummary? _selectedImportJob;
    public string ImportTaskSummary => SelectedImportJob?.DisplayName ?? "No import yet";
    public string ImportProgressText => SelectedImportJob?.ProgressDescription ?? "Start your first import above. Progress and upcoming runs will appear here.";

    [RelayCommand]
    private async Task OpenImportFile()
    {
        var file = await _filePicker.PickFileAsync([".json", ".csv", ".txt"]);
        if (file == null) return;
        var extension = Path.GetExtension(file.Path);
        var parser = AvailableParsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (parser != null) SelectedParser = parser;
        SelectedFilePath = file.Path;
    }

    [RelayCommand]
    private async Task RefreshImports()
    {
        // Refresh on every visit, including after sign-in, logout or account removal.
        HasImportAccount = false;
        try
        {
            var settings = AutomaticImportSetup.Resolve(AutomaticImportSetup.PluginRoot);
            var account = AutomaticImportSetup.CheckAccount(settings);
            ImportAccount = "Last.fm account: " + account.Account;
            HasImportAccount = true;
        }
        catch (Exception ex) { ImportAccount = ex.Message; }

        try
        {
            var store = await Task.Run(() => new ImportStore());
            ImportJobs = new(await Task.Run(store.List));
            // Always show the unfinished task; otherwise show the latest completed import.
            SelectedImportJob = ImportJobs.FirstOrDefault(j => !j.Completed) ?? ImportJobs.LastOrDefault();
        }
        catch (Exception ex) { ImportStatus = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanQueueImport))]
    private async Task QueueImport()
    {
        IsBusy = true;
        ImportJob? createdJob = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Background imports currently require Windows.");
            var setup = AutomaticImportSetup.Resolve(AutomaticImportSetup.PluginRoot);
            var account = AutomaticImportSetup.CheckAccount(setup).Account;
            var profile = SelectedParser switch
            {
                CsvFileParserViewModel csv => new ImportProfile { Format = "csv", Csv = csv.Config },
                JsonFileParserViewModel json => new ImportProfile { Format = "json", Json = json.Config },
                _ => throw new InvalidOperationException("Select CSV or JSON.")
            };
            const TimestampPolicy policy = TimestampPolicy.Import;
            // Capture configuration and source path before awaiting the confirmation dialog.
            var file = SelectedFilePath;
            var amount = int.Parse(ImportAmount, CultureInfo.InvariantCulture);
            var interval = double.Parse(ImportIntervalHours, CultureInfo.InvariantCulture);
            var allowErrors = ImportAllowParseErrors;
            var parsed = await Task.Run(() => profile.Parse(file, policy));
            var count = parsed.Scrobbles.Count();
            var errors = parsed.Errors.Count();
            if (count == 0 || (errors > 0 && !allowErrors)) throw new InvalidOperationException($"Found {count} tracks and {errors} errors. Correct errors or enable importing valid rows only.");
            var result = await _dialogService.ShowDialogAsync(new ContentDialog
            {
                Title = "Start scheduled import",
                Content = $"Import {count:N0} tracks to {account}, up to {amount} every {interval} hours? " +
                    (errors > 0 ? $"{errors} unreadable rows will be skipped and saved in an error report. " : "") +
                    "Tracks will appear on Last.fm with new dates. " +
                    "Your file stays intact. Imports run with Scrubbler closed while you are signed in to Windows.",
                PrimaryButtonText = "Start import", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            });
            if (result != ContentDialogResult.Primary) return;
            var store = new ImportStore();
            // Keep a new job paused until setup succeeds, including when an older task already exists.
            createdJob = await Task.Run(() => store.Create(file, profile, account, amount, interval, policy, allowParseErrors: allowErrors, initiallyPaused: true));
            await AutomaticImportSetup.EnableAsync(store, setup, account);
            store.Pause(createdJob.Id, false);
            ImportStatus = await RequestImmediateRun(store, $"{createdJob.SourceName} is scheduled.");
            await RefreshImports();
            SelectedImportJob = ImportJobs.FirstOrDefault(j => j.Job.Id == createdJob.Id);
        }
        catch (Exception ex)
        {
            _logService.Error("Could not start scheduled import.", ex);
            ImportStatus = ex.Message;
            if (createdJob != null)
            {
                ImportStatus += " Your import is saved and paused. Use Resume to try again.";
                await RefreshImports();
                SelectedImportJob = ImportJobs.FirstOrDefault(j => j.Job.Id == createdJob.Id);
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PauseImport() => await SetImportPaused(true);
    [RelayCommand]
    private async Task ResumeImport() => await SetImportPaused(false);

    private bool CanDeleteImport => SelectedImportJob != null;

    [RelayCommand(CanExecute = nameof(CanDeleteImport))]
    private async Task DeleteImport()
    {
        var selected = SelectedImportJob;
        if (selected == null) return;
        var result = await _dialogService.ShowDialogAsync(new ContentDialog
        {
            Title = "Delete import?",
            Content = $"Remove {selected.Job.SourceName} and its remaining queue? Scrobbles already sent to Last.fm, your source file and its backup are kept.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        });
        if (result != ContentDialogResult.Primary) return;
        try
        {
            await Task.Run(() => new ImportStore().Delete(selected.Job.Id));
            ImportStatus = "Import deleted.";
            await RefreshImports();
        }
        catch (Exception ex) { ImportStatus = ex.Message; }
    }

    private async Task SetImportPaused(bool paused)
    {
        try
        {
            if (SelectedImportJob == null) return;
            var id = SelectedImportJob.Job.Id;
            var account = SelectedImportJob.Job.Account;
            var store = new ImportStore();
            if (!paused)
                await AutomaticImportSetup.EnableAsync(store, AutomaticImportSetup.Resolve(AutomaticImportSetup.PluginRoot), account);
            await Task.Run(() => store.Pause(id, paused));
            ImportStatus = paused ? "Import paused." : await RequestImmediateRun(store, "Import resumed.");
            await RefreshImports();
        }
        catch (Exception ex) { ImportStatus = ex.Message; }
    }

    private static async Task<string> RequestImmediateRun(ImportStore store, string status)
    {
        try
        {
            await WindowsImportSchedule.RunNowAsync(store);
            return status + " A background run was requested. Eligible imports will start now; cooldowns and unresolved submissions still apply.";
        }
        catch (Exception ex)
        {
            // Scheduling already succeeded. A failed immediate trigger must not
            // claim that the enabled job is paused or undo its existing schedule.
            return status + " The immediate run could not start; the next scheduled check will retry. " + ex.Message;
        }
    }
}
