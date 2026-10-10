using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scrubbler.Import;

namespace Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

internal sealed partial class FileParseScrobbleViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewSubmissionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(QueueImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshImportsCommand))]
    [NotifyPropertyChangedFor(nameof(ReviewIdle))]
    private bool _isReviewBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewSubmissionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(QueueImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshImportsCommand))]
    [NotifyPropertyChangedFor(nameof(ReviewIdle))]
    private bool _isReviewOpen;

    public bool ReviewIdle => !IsReviewBusy && !IsReviewOpen;
    public bool HasReviewableEntries => SelectedImportJob?.Counts.Any(c => c.Value > 0 && ImportReviewService.IsUnresolved(c.Key)) == true;
    private bool CanReviewSubmissions => SupportsAutomatedImports && HasReviewableEntries && ReviewIdle;
    private bool CanResumeImport => SelectedImportJob is { Completed: false } job && job.Job.Paused && ReviewIdle
        && job.Counts.GetValueOrDefault(EntryStatus.Uncertain) + job.Counts.GetValueOrDefault(EntryStatus.InFlight) == 0;

    [RelayCommand(CanExecute = nameof(CanReviewSubmissions))]
    private async Task ReviewSubmissions()
    {
        if (SelectedImportJob is not { } selected) return;
        var id = selected.Job.Id;
        IsReviewOpen = true;
        try
        {
            var model = await CheckImportSubmissions(id);
            while (true)
            {
                // Close the review dialog before opening a confirmation: ContentDialog
                // supports only one open dialog at a time. Reuse the model after Cancel.
                var result = await _dialogService.ShowDialogAsync(new ImportReviewDialog { DataContext = model });
                if (result == ContentDialogResult.None)
                {
                    if (model.HasEntries) ImportStatus = "Review closed. Remaining entries still need review. Your import is paused.";
                    break;
                }
                if (result == ContentDialogResult.Secondary)
                {
                    try { model = await CheckImportSubmissions(id); }
                    catch (Exception ex)
                    {
                        _logService.Error("Could not check import submissions again.", ex);
                        model.ErrorMessage = "Could not complete the Last.fm check. Your choices have been kept. " + ex.Message;
                    }
                    continue;
                }
                if (await ConfirmAndSaveReview(model)) break;
            }
        }
        catch (Exception ex)
        {
            _logService.Error("Could not review import submissions.", ex);
            await RefreshImports();
            ImportStatus = "Could not complete the submission review. Refresh the import and check again. " + ex.Message;
        }
        finally { IsReviewBusy = false; IsBusy = false; IsReviewOpen = false; }
    }

    private async Task<ImportReviewDialogViewModel> CheckImportSubmissions(string id)
    {
        IsReviewBusy = true;
        IsBusy = true;
        try
        {
            var store = new ImportStore();
            var job = store.Get(id);
            var setup = AutomaticImportSetup.Resolve(AutomaticImportSetup.PluginRoot);
            var auth = AutomaticImportSetup.CheckAccount(setup);
            if (!string.Equals(auth.Account, job.Account, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Sign in as {job.Account} before reviewing this import.");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var service = new ImportReviewService(store, new LastFmHistoryReader(http, () => auth));
            var review = await Task.Run(() => service.CheckAsync(id));
            await RefreshImports();
            SelectedImportJob = ImportJobs.FirstOrDefault(j => j.Job.Id == id);
            if (SelectedImportJob == null) throw new InvalidOperationException("This import was removed during review.");
            ImportStatus = review.Entries.Count > 0 ? $"{review.ConfirmedCount} verified plays marked imported. {review.Entries.Count} entries still need review. Your import is paused."
                : SelectedImportJob.Completed ? "All submissions were verified on Last.fm. This import is complete."
                : "All submissions were verified on Last.fm. Use Resume when you are ready; the existing cooldown still applies.";
            return new(review, DateTimeOffset.UtcNow);
        }
        finally { IsReviewBusy = false; IsBusy = false; }
    }

    private async Task<bool> ConfirmAndSaveReview(ImportReviewDialogViewModel model)
    {
        var review = model.Review;
        var decisions = model.CaptureDecisions();
        var imported = decisions.Count(d => d.Action == ReviewAction.MarkImported);
        var skipped = decisions.Count(d => d.Action == ReviewAction.Skip);
        var retry = decisions.Count(d => d.Action == ReviewAction.Retry);
        IsReviewBusy = true;
        try
        {
            var confirmation = await _dialogService.ShowDialogAsync(new ContentDialog
            {
                Title = "Save submission review?",
                Content = $"For {review.Account}: mark {imported} as imported, skip {skipped}, and queue {retry} for retry. " +
                    (retry > 0 ? "Retrying a play that Last.fm already stored may create a duplicate. Only retry entries you have verified are missing. " : "") +
                    "Nothing will be submitted now. Your import stays paused and its cooldown is kept.",
                PrimaryButtonText = "Save decisions", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            });
            if (confirmation != ContentDialogResult.Primary) return false;
            await Task.Run(() => new ImportReviewService(new ImportStore()).Apply(review, decisions));
            await RefreshImports();
            SelectedImportJob = ImportJobs.FirstOrDefault(j => j.Job.Id == review.JobId);
            var uncertain = SelectedImportJob?.Counts.GetValueOrDefault(EntryStatus.Uncertain) + SelectedImportJob?.Counts.GetValueOrDefault(EntryStatus.InFlight) ?? 0;
            ImportStatus = SelectedImportJob?.Completed == true ? "Decisions saved. This import is complete."
                : uncertain > 0 ? "Decisions saved. Review the remaining uncertain entries before resuming."
                : "Decisions saved. Use Resume when you are ready; the existing cooldown still applies.";
            return true;
        }
        catch (Exception ex)
        {
            _logService.Error("Could not save submission review.", ex);
            model.ErrorMessage = "Could not save your decisions. Check again if the import has changed. " + ex.Message;
            ImportStatus = ex.Message;
            return false;
        }
        finally { IsReviewBusy = false; }
    }
}
