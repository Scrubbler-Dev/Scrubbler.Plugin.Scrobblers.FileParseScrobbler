using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrubbler.Import;

public sealed class ImportTrack(string track, string artist, DateTimeOffset timestamp)
{
    public string Track { get; set; } = !string.IsNullOrWhiteSpace(track) ? track : throw new ArgumentException("Track is required.");
    public string Artist { get; set; } = !string.IsNullOrWhiteSpace(artist) ? artist : throw new ArgumentException("Artist is required.");
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public DateTimeOffset Timestamp { get; set; } = timestamp;
    public DateTimeOffset? OriginalTimestamp { get; set; }
    public int SourceIndex { get; set; }
}

public enum EntryStatus { Pending, InFlight, Accepted, NeedsAttention, Uncertain, Skipped }
public enum TimestampPolicy { Import, Original }
public enum SubmissionOutcome { Accepted, Retry, DailyLimit, Rejected, AuthenticationRequired, Uncertain }

public sealed class ImportEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required ImportTrack Track { get; set; }
    public EntryStatus Status { get; set; }
    public DateTimeOffset? SubmittedTimestamp { get; set; }
    public DateTimeOffset? AttemptedAtUtc { get; set; }
    public string? Message { get; set; }
}

public sealed class ImportJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Account { get; set; }
    public required string SourceHash { get; set; }
    public required string SourceName { get; set; }
    public required string ProfileJson { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextEligibleRunUtc { get; set; }
    public bool Paused { get; set; }
    public int MaxPerRun { get; set; } = 600;
    public double IntervalHours { get; set; } = 24;
    public TimestampPolicy TimestampPolicy { get; set; }
    public int SpacingSeconds { get; set; } = 1;
    public int DateOffsetDays { get; set; }
    public TimeSpan? ScrobbleTimeOfDay { get; set; }
    public string? LastMessage { get; set; }

    public void Validate()
    {
        if (DateOffsetDays is < 0 or > 10) throw new ArgumentException("Date offset must be between 0 and 10 days.");
        if (ScrobbleTimeOfDay is { } time && (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1) || time.Ticks % TimeSpan.TicksPerSecond != 0))
            throw new ArgumentException("Scrobble time must be a time of day with whole-second precision.");
        if (TimestampPolicy != TimestampPolicy.Import) throw new ArgumentException("Scheduled imports only support import mode.");
        if (string.IsNullOrWhiteSpace(Account)) throw new ArgumentException("A Last.fm username is required.");
        if (MaxPerRun is < 1 or > 600) throw new ArgumentException("Amount must be between 1 and 600.");
        if (!double.IsFinite(IntervalHours) || IntervalHours is < 24 or > 8760)
            throw new ArgumentException("Interval must be between 24 and 8760 hours.");
        if (SpacingSeconds < 1 || (long)SpacingSeconds * MaxPerRun > 86400)
            throw new ArgumentException("Timestamp spacing must fit a run within 24 hours.");
    }
}

public sealed record SubmissionResult(SubmissionOutcome Outcome, string? Message = null);
public sealed record JobSummary(ImportJob Job, IReadOnlyDictionary<EntryStatus, int> Counts, DateTimeOffset EffectiveNextRunUtc = default)
{
    public string DisplayName => $"{Job.SourceName} — {Counts.GetValueOrDefault(EntryStatus.Accepted):N0} of {Counts.Values.Sum():N0} imported" +
        (Completed ? " · Complete" : Job.Paused ? " · Paused" : " · Scheduled");
    public string ProgressDescription => Completed ? "Import complete." :
        Job.TimestampPolicy != TimestampPolicy.Import ? "This task uses original dates, which are only supported by manual scrobbling." :
        Counts.GetValueOrDefault(EntryStatus.Uncertain) + Counts.GetValueOrDefault(EntryStatus.InFlight) > 0 ? "Some submissions need review before this import can continue." :
        Job.Paused ? "Paused. Resume when you are ready." :
        EffectiveNextRunUtc > DateTimeOffset.UtcNow ? $"Next import after {EffectiveNextRunUtc.ToLocalTime():g}." : "Ready for the next background check.";
    public bool Completed => Counts.All(c => c.Key is EntryStatus.Accepted or EntryStatus.Skipped || c.Value == 0);
    public override string ToString() => $"{Job.Id} | {Job.Account} | {Job.SourceName} | {(Completed ? "Completed" : Job.Paused ? "Paused" : "Enabled")} | " +
        string.Join(", ", Counts.Select(c => $"{c.Key}: {c.Value}")) +
        (Completed ? "" : EffectiveNextRunUtc == default ? " | Ready" : $" | Next: {EffectiveNextRunUtc:u}") + $" | {Job.LastMessage}";
}

public interface IScrobbleSubmitter
{
    Task<IReadOnlyList<SubmissionResult>> SubmitAsync(string account, IReadOnlyList<ImportEntry> entries, CancellationToken cancellationToken);
}

public static class ImportJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
