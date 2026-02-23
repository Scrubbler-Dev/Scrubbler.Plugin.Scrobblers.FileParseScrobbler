using CommunityToolkit.Mvvm.Input;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser;

internal interface IConfigurableFileParserViewModel<T> : IFileParserViewModel where T : IFileParserConfiguration
{
    T Config { get; }

    IAsyncRelayCommand OpenSettingsCommand { get; }
}
