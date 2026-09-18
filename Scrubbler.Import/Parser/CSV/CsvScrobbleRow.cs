namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.CSV;

public sealed record CsvScrobbleRow
{
    public string? Timestamp { get; init; }
    public string Artist { get; init; } = string.Empty;
    public string Track { get; init; } = string.Empty;
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? MillisecondsPlayed { get; init; }
}

