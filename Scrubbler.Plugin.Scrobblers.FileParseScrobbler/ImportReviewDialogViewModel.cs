using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Scrubbler.Import;

namespace Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

internal sealed partial class ImportReviewDialogViewModel : ObservableObject
{
    public ImportReview Review { get; }
    public string Account => $"Last.fm account: {Review.Account} · Checked {Review.CheckedAtUtc.ToLocalTime():g}";
    public string Result => $"{Review.ConfirmedCount} verified on Last.fm and marked imported. {Entries.Count} still need review.";
    public IReadOnlyList<ImportReviewEntryViewModel> Entries { get; }
    public bool HasEntries => Entries.Count > 0;
    public Microsoft.UI.Xaml.Visibility EntriesVisibility => HasEntries ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool CanSave => Entries.Any(e => e.SelectedChoice.Action != ReviewAction.KeepUnresolved);
    [ObservableProperty] private ImportReviewEntryViewModel? _selectedEntry;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorVisibility))]
    private string _errorMessage = "";
    public Microsoft.UI.Xaml.Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorMessage) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public ImportReviewDialogViewModel(ImportReview review, DateTimeOffset now)
    {
        Review = review;
        Entries = review.Entries.Select(e => new ImportReviewEntryViewModel(e, now)).ToArray();
        foreach (var row in Entries) row.PropertyChanged += ChoiceChanged;
        SelectedEntry = Entries.FirstOrDefault();
        UpdateSummary();
    }

    public ReviewDecision[] CaptureDecisions() => Entries.Select(e => new ReviewDecision(e.Review.Entry.Id, e.SelectedChoice.Action)).ToArray();

    private void ChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ImportReviewEntryViewModel.SelectedChoice)) return;
        UpdateSummary();
        OnPropertyChanged(nameof(CanSave));
    }

    private void UpdateSummary()
    {
        var imported = Entries.Count(e => e.SelectedChoice.Action == ReviewAction.MarkImported);
        var skipped = Entries.Count(e => e.SelectedChoice.Action == ReviewAction.Skip);
        var retry = Entries.Count(e => e.SelectedChoice.Action == ReviewAction.Retry);
        var unresolved = Entries.Count(e => e.SelectedChoice.Action == ReviewAction.KeepUnresolved);
        Summary = HasEntries ? $"{imported} to mark imported · {skipped} to skip · {retry} to retry · {unresolved} unresolved"
            : "Nothing left to review.";
    }
}
