using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;
using ScrobbleData = Scrubbler.Import.ImportTrack;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.CSV;

public sealed class CsvFileParser : IFileParser<CsvFileParserConfiguration>
{
    public FileParseResult Parse(string file, CsvFileParserConfiguration config, ScrobbleMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        config.Validate();

        var scrobbles = new List<ScrobbleData>();
        var errors = new List<string>();

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = config.HasHeaderRecord,
            Delimiter = config.Delimiter,
            Encoding = config.Encoding,
            BadDataFound = null,
            MissingFieldFound = null,
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim
        };

        using var reader = new StreamReader(file, csvConfig.Encoding);
        using var csv = new CsvReader(reader, csvConfig);

        csv.Context.RegisterClassMap(new CsvScrobbleRowMap(config));

        var rowIndex = 0;
        foreach (var row in csv.GetRecords<CsvScrobbleRow>())
        {
            rowIndex++;

            try
            {
                // Timestamp handling
                DateTime playedAt = DateTime.Now;
                if (mode == ScrobbleMode.UseScrobbleTimestamp)
                {
                    if (string.IsNullOrWhiteSpace(row.Timestamp) || !FileParseResult.TryParseDateString(row.Timestamp, out playedAt))
                        throw new FormatException("Timestamp could not be parsed");
                }

                // Short-play filter
                var playedMilliseconds = double.TryParse(row.MillisecondsPlayed, NumberStyles.Number, CultureInfo.InvariantCulture, out var milliseconds)
                    ? milliseconds : TimeSpan.TryParse(row.MillisecondsPlayed, out var played) ? played.TotalMilliseconds : double.NaN;
                if (config.FilterShortPlayedSongs && playedMilliseconds <= config.MillisecondsPlayedThreshold)
                    continue;

                scrobbles.Add(
                    new ScrobbleData(row.Track, row.Artist, playedAt.AddSeconds(1))
                    {
                        Album = row.Album,
                        AlbumArtist = row.AlbumArtist,
                        SourceIndex = rowIndex - 1,
                        OriginalTimestamp = DateTimeOffset.TryParse(row.Timestamp, out var original) ? original : null
                    }
                );
            }
            catch (Exception ex)
            {
                errors.Add($"CSV line {rowIndex}: {ex.Message}");
            }
        }

        return new FileParseResult(scrobbles, errors);
    }
}
