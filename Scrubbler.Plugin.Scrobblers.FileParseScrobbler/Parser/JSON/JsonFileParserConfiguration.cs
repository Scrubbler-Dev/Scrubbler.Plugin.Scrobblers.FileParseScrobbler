namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON
{
	internal sealed record JsonFileParserConfiguration : IFileParserConfiguration
	{
		#region Properties

		public required string TimestampFieldName { get; init; }

		public required string TrackFieldName { get; init; }

		public required string ArtistFieldName { get; init; }

		public string AlbumFieldName { get; init; } = string.Empty;

		public string AlbumArtistFieldName { get; init; } = string.Empty;

		public bool FilterShortPlayedSongs { get; init; }

		public string MillisecondsPlayedFieldName { get; init; } = string.Empty;

		public int MillisecondsPlayedThreshold { get; init; } = 30_000;

		#endregion Properties

		public static JsonFileParserConfiguration Default => new()
		{
			TimestampFieldName = "ts",
			TrackFieldName = "master_metadata_track_name",
			ArtistFieldName = "master_metadata_album_artist_name",
			AlbumFieldName = "master_metadata_album_album_name",
			AlbumArtistFieldName = "master_metadata_album_artist_name",
			MillisecondsPlayedFieldName = "ms_played"
		};

		/// <summary>
		/// Validates configuration consistency.
		/// </summary>
		public void Validate()
		{
			if (string.IsNullOrWhiteSpace(TimestampFieldName))
				throw new InvalidOperationException("Timestamp field name must not be empty.");

			if (string.IsNullOrWhiteSpace(TrackFieldName))
				throw new InvalidOperationException("Track field name must not be empty.");

			if (string.IsNullOrWhiteSpace(ArtistFieldName))
				throw new InvalidOperationException("Artist field name must not be empty.");

			if (FilterShortPlayedSongs && MillisecondsPlayedThreshold <= 0)
				throw new InvalidOperationException("MillisecondsPlayedThreshold must be > 0.");
		}
	}
}
