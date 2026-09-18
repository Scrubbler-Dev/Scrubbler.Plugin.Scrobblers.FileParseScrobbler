using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser;

public interface IFileParser<T> where T : IFileParserConfiguration
{
    FileParseResult Parse(string file, T config, ScrobbleMode mode);
}
