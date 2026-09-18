using System.Text.Json;
using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;
using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser;
using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.CSV;
using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON;

namespace Scrubbler.Import;

public sealed class ImportProfile
{
    public string Format { get; set; } = "json";
    public CsvFileParserConfiguration Csv { get; set; } = CsvFileParserConfiguration.Default;
    public JsonFileParserConfiguration Json { get; set; } = JsonFileParserConfiguration.Default with { FilterShortPlayedSongs = true };

    public FileParseResult Parse(string path, TimestampPolicy policy)
    {
        var mode = policy == TimestampPolicy.Import ? ScrobbleMode.Import : ScrobbleMode.UseScrobbleTimestamp;
        return Format.ToLowerInvariant() switch
        {
            "json" => new JsonFileParser().Parse(path, Json, mode),
            "csv" => new CsvFileParser().Parse(path, Csv, mode),
            _ => throw new ArgumentException("Format must be json or csv.")
        };
    }

    public string Serialize() => JsonSerializer.Serialize(this, ImportJson.Options);
    public static ImportProfile Read(string path) => JsonSerializer.Deserialize<ImportProfile>(File.ReadAllText(path), ImportJson.Options)
        ?? throw new ArgumentException("Profile is empty.");
}
