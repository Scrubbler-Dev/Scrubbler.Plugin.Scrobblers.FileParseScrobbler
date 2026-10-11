using CommunityToolkit.Mvvm.ComponentModel;
using Scrubbler.Import;
using System.Diagnostics.CodeAnalysis;

namespace Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

internal sealed record ImportReviewChoice(ReviewAction Action, string Label);

internal sealed partial class ImportReviewEntryViewModel : ObservableObject
{
    public ReviewedImportEntry Review { get; }
    public string Title => $"Row {Review.Entry.Track.SourceIndex + 1}: {Review.Entry.Track.Artist} — {Review.Entry.Track.Track}";
    public string Album => "Album: " + (Review.Entry.Track.Album ?? "Not provided");
    public string Timestamp => Review.Entry.SubmittedTimestamp is { } time ? $"Submitted: {time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" : "Submission time was not recorded.";
    public string PreviousMessage => LastFmScrobbleFeedback.IgnoredCode(Review.Entry) is { } code
        ? LastFmScrobbleFeedback.DescribeIgnoredCode(code) ?? Review.Entry.Message ?? "" : Review.Entry.Message ?? "";
    public string MatchDescription => (Review.Match is HistoryMatch.Missing or HistoryMatch.NoTimestamp)
        && LastFmScrobbleFeedback.RetryUnavailableReason(Review.Entry) is { Length: > 0 } reason
        ? reason : Review.Match switch
    {
        HistoryMatch.Found => string.IsNullOrWhiteSpace(Review.Entry.Track.Album)
            ? "Found on Last.fm: artist, track and timestamp match."
            : "Found on Last.fm: artist, track, album and timestamp match.",
        HistoryMatch.Missing => "Not found at the submitted time. Check again if the submission was recent.",
        HistoryMatch.Ambiguous => "Possible match. Compare the Last.fm details before deciding.",
        _ => "Could not check: no submission timestamp was recorded."
    };
    public string Candidates => Review.Match == HistoryMatch.NoTimestamp ? "Last.fm history could not be checked without a submission timestamp."
        : Review.Candidates.Count == 0 ? "No Last.fm play found at this timestamp."
        : string.Join("\n", Review.Candidates.Select(p => $"Last.fm: {p.Artist} — {p.Track}\nAlbum: {p.Album ?? "Not provided"}\n{p.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"));
    public IReadOnlyList<ImportReviewChoice> Choices { get; }
    public string RetryUnavailableReason { get; }
    private ImportReviewChoice _selectedChoice;

    [AllowNull]
    public ImportReviewChoice SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            // Rebinding a ComboBox can clear selection or write an item
            // from the previous row. Neither transition changes this row's decision.
            if (value != null && Choices.Any(choice => ReferenceEquals(choice, value)))
                SetProperty(ref _selectedChoice, value);
        }
    }

    public ImportReviewEntryViewModel(ReviewedImportEntry review, DateTimeOffset now)
    {
        Review = review;
        var choices = new List<ImportReviewChoice>
        {
            new(ReviewAction.KeepUnresolved, "Leave unresolved"),
            new(ReviewAction.MarkImported, "Mark as already imported"),
            new(ReviewAction.Skip, "Skip this entry")
        };
        var metadataReason = LastFmScrobbleFeedback.RetryUnavailableReason(review.Entry);
        if (metadataReason.Length == 0 && (review.Match is HistoryMatch.Missing or HistoryMatch.NoTimestamp)
            && (review.Entry.SubmittedTimestamp is not { } time || (time >= now.AddDays(-14) && time <= now)))
            choices.Add(new(ReviewAction.Retry, "Retry this entry"));
        Choices = choices;
        RetryUnavailableReason = metadataReason.Length > 0 ? metadataReason
            : review.Match is HistoryMatch.Missing or HistoryMatch.NoTimestamp && !choices.Any(c => c.Action == ReviewAction.Retry)
            ? "This timestamp is outside Last.fm's accepted window. Skip this entry and scrobble it manually with a valid date if needed."
            : "";
        _selectedChoice = choices.Single(c => c.Action == (review.Match == HistoryMatch.Found ? ReviewAction.MarkImported : ReviewAction.KeepUnresolved));
    }
}
