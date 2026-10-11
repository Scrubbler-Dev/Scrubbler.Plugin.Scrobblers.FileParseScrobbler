using System.Globalization;

namespace Scrubbler.Import;

public static class LastFmScrobbleFeedback
{
    public static bool UsesCompilationArtist(ImportTrack track) =>
        string.Equals(track.Artist.Trim(), "Various Artists", StringComparison.OrdinalIgnoreCase);

    public const string CompilationArtistSkipReason = "Skipped before submission because the track artist is 'Various Artists'. Scrobble manually with the actual track artist if needed.";

    public static ImportEntry[] MarkCompilationArtistsSkipped(IEnumerable<ImportEntry> entries)
    {
        // Never reinterpret an accepted or uncertain request as a local skip.
        var skipped = entries.Where(e => e.Status == EntryStatus.Pending && UsesCompilationArtist(e.Track)).ToArray();
        foreach (var entry in skipped)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = CompilationArtistSkipReason;
        }
        return skipped;
    }

    public static string CompilationArtistFilterNotice(IEnumerable<ImportTrack> tracks)
    {
        var count = tracks.Count(UsesCompilationArtist);
        return count == 0 ? "" : $"{count:N0} {(count == 1 ? "track uses" : "tracks use")} 'Various Artists' as the track artist and will be skipped before submission. Skipped entries are kept with a reason; you can scrobble them manually with the actual track artist. ";
    }

    // https://www.last.fm/api/show/track.scrobble — individual ignored-message codes.
    public static string? DescribeIgnoredCode(int? code) => code switch
    {
        0 => null,
        1 => "Last.fm ignored code 1: Artist was ignored. Check the track artist; retrying unchanged metadata may be rejected again.",
        2 => "Last.fm ignored code 2: Track was ignored. Check the track title; retrying unchanged metadata may be rejected again.",
        3 => "Last.fm ignored code 3: Timestamp was too old.",
        4 => "Last.fm ignored code 4: Timestamp was too new.",
        5 => "Last.fm ignored code 5: Daily scrobble limit exceeded.",
        _ => $"Last.fm ignored code {code}."
    };

    public static int? IgnoredCode(ImportEntry entry)
    {
        if (entry.LastFmIgnoredCode is { } code) return code;
        // Older queues only recorded the code in this exact message format.
        const string prefix = "Last.fm ignored code ";
        var message = entry.Message;
        if (message == null || !message.StartsWith(prefix, StringComparison.Ordinal) || !message.EndsWith('.')) return null;
        return int.TryParse(message.AsSpan(prefix.Length, message.Length - prefix.Length - 1), NumberStyles.None,
            CultureInfo.InvariantCulture, out var legacyCode) ? legacyCode : null;
    }

    public static string RetryUnavailableReason(ImportEntry entry)
    {
        if (entry.Status != EntryStatus.NeedsAttention) return "";
        return IgnoredCode(entry) switch
        {
            1 when UsesCompilationArtist(entry.Track) => "Last.fm ignored this artist. 'Various Artists' is a compilation credit. Skip this entry and scrobble it manually with the actual track artist.",
            1 => "Last.fm ignored this artist. Skip this entry and scrobble it manually after correcting the track artist. The import's Retry action keeps the same metadata.",
            2 => "Last.fm ignored this track. Skip this entry and scrobble it manually after correcting the track title. The import's Retry action keeps the same metadata.",
            _ => ""
        };
    }
}
