using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser;

internal interface IFileParser<T> where T : IFileParserConfiguration
{
    FileParseResult Parse(string file, T config, ScrobbleMode mode);
}
