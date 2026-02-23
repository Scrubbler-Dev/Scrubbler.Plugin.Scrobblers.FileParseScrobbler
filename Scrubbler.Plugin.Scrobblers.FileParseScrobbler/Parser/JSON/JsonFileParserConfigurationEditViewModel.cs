using CommunityToolkit.Mvvm.ComponentModel;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON;

/// <summary>
/// Editable ViewModel for JSON file parser configuration.
/// </summary>
internal sealed partial class JsonFileParserConfigurationEditViewModel : ObservableObject
{
	#region Properties

	[ObservableProperty]
	private string _timestampFieldName = "ts";

	[ObservableProperty]
	private string _trackFieldName = "master_metadata_track_name";

	[ObservableProperty]
	private string _artistFieldName = "master_metadata_album_artist_name";

	[ObservableProperty]
	private string _albumFieldName = string.Empty;

	[ObservableProperty]
	private string _albumArtistFieldName = string.Empty;

	[ObservableProperty]
	private string _millisecondsPlayedFieldName = string.Empty;

	[ObservableProperty]
	private bool _filterShortPlayedSongs;

	[ObservableProperty]
	private int _millisecondsPlayedThreshold = 30_000;

	#endregion Properties

	#region Construction

	public JsonFileParserConfigurationEditViewModel(JsonFileParserConfiguration config)
	{
		TimestampFieldName = config.TimestampFieldName;
		TrackFieldName = config.TrackFieldName;
		ArtistFieldName = config.ArtistFieldName;
		AlbumFieldName = config.AlbumFieldName;
		AlbumArtistFieldName = config.AlbumArtistFieldName;
		MillisecondsPlayedFieldName = config.MillisecondsPlayedFieldName;
		FilterShortPlayedSongs = config.FilterShortPlayedSongs;
		MillisecondsPlayedThreshold = config.MillisecondsPlayedThreshold;
	}

	#endregion Construction

	#region Conversion

	/// <summary>
	/// Creates an immutable parser configuration snapshot.
	/// </summary>
	public JsonFileParserConfiguration ToConfiguration()
	{
		var config = new JsonFileParserConfiguration
		{
			TimestampFieldName = TimestampFieldName,
			TrackFieldName = TrackFieldName,
			ArtistFieldName = ArtistFieldName,
			AlbumFieldName = AlbumFieldName ?? string.Empty,
			AlbumArtistFieldName = AlbumArtistFieldName ?? string.Empty,
			MillisecondsPlayedFieldName = MillisecondsPlayedFieldName ?? string.Empty,
			FilterShortPlayedSongs = FilterShortPlayedSongs,
			MillisecondsPlayedThreshold = MillisecondsPlayedThreshold
		};

		config.Validate();
		return config;
	}

	#endregion Conversion
}