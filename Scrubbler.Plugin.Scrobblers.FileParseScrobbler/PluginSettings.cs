using Scrubbler.Abstractions.Settings;
using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.CSV;
using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler;

internal sealed class PluginSettings : IPluginSettings
{
    public CsvFileParserConfiguration CsvConfig { get; set; } = CsvFileParserConfiguration.Default;

    public JsonFileParserConfiguration JsonConfig { get; set; } = JsonFileParserConfiguration.Default;
}
