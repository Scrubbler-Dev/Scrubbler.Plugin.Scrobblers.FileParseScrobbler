using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;
using System.ComponentModel;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser;

internal interface IFileParserViewModel : INotifyPropertyChanged
{
    string Name { get; }

    IReadOnlyList<string> SupportedExtensions { get; }

    FileParseResult Parse(string file, ScrobbleMode mode);
}
