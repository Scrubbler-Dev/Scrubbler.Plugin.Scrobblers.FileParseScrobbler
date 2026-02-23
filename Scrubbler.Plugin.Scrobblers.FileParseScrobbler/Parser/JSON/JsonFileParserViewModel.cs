using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scrubbler.Abstractions.Services;
using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON
{
	internal sealed partial class JsonFileParserViewModel(IDialogService dialogService, IFileParser<JsonFileParserConfiguration> parser, JsonFileParserConfiguration initialConfig)
		: ObservableObject, IConfigurableFileParserViewModel<JsonFileParserConfiguration>
	{
		#region Properties

		public string Name { get; } = "JSON";

		public JsonFileParserConfiguration Config { get; private set; } = initialConfig;

		public IReadOnlyList<string> SupportedExtensions { get; } = [".json"];

		private readonly IDialogService _dialogService = dialogService;
		private readonly IFileParser<JsonFileParserConfiguration> _parser = parser;

		#endregion Properties

		[RelayCommand]
		private async Task OpenSettings()
		{
			var vm = new JsonFileParserConfigurationEditViewModel(Config);
			var dialog = new JsonFileParserConfigurationEditView
			{
				DataContext = vm
			};

			var result = await _dialogService.ShowDialogAsync(dialog);
			if (result == ContentDialogResult.Primary)
				Config = vm.ToConfiguration();
		}

		public FileParseResult Parse(string file, ScrobbleMode mode)
		{
			return _parser.Parse(file, Config, mode);
		}
	}
}
