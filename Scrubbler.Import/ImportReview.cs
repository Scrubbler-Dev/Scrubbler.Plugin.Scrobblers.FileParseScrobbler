using System.Text;

namespace Scrubbler.Import;

public enum HistoryMatch { Found, Missing, Ambiguous, NoTimestamp }
public enum ReviewAction { KeepUnresolved, MarkImported, Skip, Retry }
public sealed record HistoryPlay(string Artist, string Track, string? Album, DateTimeOffset Timestamp);
public sealed record ReviewedImportEntry(ImportEntry Entry, HistoryMatch Match, IReadOnlyList<HistoryPlay> Candidates);
public sealed record ImportReview(string JobId, string Account, DateTimeOffset CheckedAtUtc, IReadOnlyList<ReviewedImportEntry> Entries, int ConfirmedCount = 0);
public sealed record ReviewDecision(string EntryId, ReviewAction Action);
public sealed record ImportEntryResolution(ImportEntry Expected, EntryStatus Status);

public interface ILastFmHistoryReader
{
    Task<IReadOnlyList<HistoryPlay>> GetAsync(string account, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

public sealed class ImportReviewService(ImportStore store, ILastFmHistoryReader? history = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public static bool IsUnresolved(EntryStatus status) => status is EntryStatus.Uncertain or EntryStatus.InFlight or EntryStatus.NeedsAttention;

    public async Task<ImportReview> CheckAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ImportJob job;
        ImportEntry[] entries;
        using (store.AcquireLock())
        {
            job = store.Get(jobId);
            entries = store.Entries(jobId).Where(e => IsUnresolved(e.Status)).ToArray();
            // Acquiring the lease proves no runner is currently sending these plays.
            // Pause before releasing it so the snapshot cannot be submitted during review.
            job.Paused = true;
            var interrupted = entries.Where(e => e.Status == EntryStatus.InFlight).ToArray();
            foreach (var entry in interrupted)
            {
                entry.Status = EntryStatus.Uncertain;
                entry.Message = "Interrupted submission. Review Last.fm before retrying.";
            }
            store.Save(job, interrupted, "Paused for submission review.");
        }
        var timestamps = entries.Where(e => e.SubmittedTimestamp.HasValue).Select(e => e.SubmittedTimestamp!.Value).ToArray();
        // A failed/incomplete lookup propagates: never classify a network error as a missing play.
        var plays = timestamps.Length == 0 ? [] : await (history ?? throw new InvalidOperationException("A Last.fm history reader is required.")).GetAsync(job.Account,
            timestamps.Min().AddSeconds(-1), timestamps.Max().AddSeconds(1), cancellationToken);
        var byTimestamp = plays.ToLookup(p => p.Timestamp.ToUnixTimeSeconds());
        var localCounts = timestamps.GroupBy(t => t.ToUnixTimeSeconds()).ToDictionary(g => g.Key, g => g.Count());
        var reviewed = entries.Select(entry =>
        {
            if (entry.SubmittedTimestamp is not { } timestamp)
                return new ReviewedImportEntry(entry, HistoryMatch.NoTimestamp, []);
            var candidates = byTimestamp[timestamp.ToUnixTimeSeconds()].ToArray();
            var match = localCounts[timestamp.ToUnixTimeSeconds()] > 1 ? HistoryMatch.Ambiguous
                : candidates.Length == 0 ? HistoryMatch.Missing
                : candidates.Length == 1 && localCounts[timestamp.ToUnixTimeSeconds()] == 1
                    && Same(candidates[0].Artist, entry.Track.Artist) && Same(candidates[0].Track, entry.Track.Track)
                    && (string.IsNullOrWhiteSpace(entry.Track.Album) || Same(candidates[0].Album, entry.Track.Album))
                        ? HistoryMatch.Found : HistoryMatch.Ambiguous;
            return new ReviewedImportEntry(entry, match, candidates);
        }).ToArray();
        var confirmed = reviewed.Where(e => e.Match == HistoryMatch.Found).ToArray();
        // Only a complete history lookup can confirm plays. Revalidate the snapshot
        // under the writer lease before recording any matches, keeping the job paused.
        cancellationToken.ThrowIfCancellationRequested();
        if (confirmed.Length > 0)
            store.ResolveReviewed(jobId, job.Account, confirmed.Select(e => new ImportEntryResolution(e.Entry, EntryStatus.Accepted)).ToArray());
        return new(jobId, job.Account, _clock.GetUtcNow(), reviewed.Where(e => e.Match != HistoryMatch.Found).ToArray(), confirmed.Length);
    }

    public void Apply(ImportReview review, IReadOnlyList<ReviewDecision> decisions)
    {
        if (decisions.Select(d => d.EntryId).Distinct().Count() != decisions.Count)
            throw new ArgumentException("Each entry may have only one decision.");
        var resolutions = new List<ImportEntryResolution>();
        foreach (var decision in decisions.Where(d => d.Action != ReviewAction.KeepUnresolved))
        {
            var row = review.Entries.SingleOrDefault(e => e.Entry.Id == decision.EntryId)
                ?? throw new InvalidOperationException("This entry was already resolved or is not in this review. Check Last.fm again.");
            if (decision.Action == ReviewAction.Retry)
            {
                if (row.Match is HistoryMatch.Found or HistoryMatch.Ambiguous)
                    throw new InvalidOperationException("This play has a Last.fm match. Review it before choosing a resolution.");
                if (row.Entry.SubmittedTimestamp is { } timestamp && (timestamp < _clock.GetUtcNow().AddDays(-14) || timestamp > _clock.GetUtcNow()))
                    throw new InvalidOperationException("This timestamp cannot be retried. Skip the entry and scrobble it manually with a valid date.");
            }
            var status = decision.Action switch
            {
                ReviewAction.MarkImported => EntryStatus.Accepted,
                ReviewAction.Skip => EntryStatus.Skipped,
                ReviewAction.Retry => EntryStatus.Pending,
                _ => throw new ArgumentException("Unknown review decision.")
            };
            resolutions.Add(new(row.Entry, status));
        }
        store.ResolveReviewed(review.JobId, review.Account, resolutions);
    }

    private static bool Same(string? left, string? right) => string.Equals(
        (left ?? "").Trim().Normalize(NormalizationForm.FormC),
        (right ?? "").Trim().Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);
}
