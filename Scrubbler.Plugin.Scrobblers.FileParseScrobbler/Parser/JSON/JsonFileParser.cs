using Scrubbler.Abstractions;
using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;
using System.Globalization;
using System.Text.Json;

namespace Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON
{
	internal class JsonFileParser : IFileParser<JsonFileParserConfiguration>
	{
		public FileParseResult Parse(string file, JsonFileParserConfiguration config, ScrobbleMode mode)
		{
			if (string.IsNullOrWhiteSpace(file))
				throw new ArgumentNullException(nameof(file));

			config.Validate();

			var scrobbles = new List<ScrobbleData>();
			var errors = new List<string>();

			var json = File.ReadAllText(file);

			using var doc = JsonDocument.Parse(json);

			if (doc.RootElement.ValueKind != JsonValueKind.Array)
				throw new InvalidOperationException("Expected a JSON array at the root.");

			var index = 0;

			foreach (var element in doc.RootElement.EnumerateArray())
			{
				try
				{
					var timestamp = mode == ScrobbleMode.Import ? DateTime.Now : ReadDateTimeOffset(element, config.TimestampFieldName).DateTime;
					var track = ReadRequiredString(element, config.TrackFieldName);
					var artist = ReadRequiredString(element, config.ArtistFieldName);

					var scrobble = new ScrobbleData(track, artist, timestamp);

					var album = ReadOptionalString(element, config.AlbumFieldName);
					if (!string.IsNullOrWhiteSpace(album))
						scrobble.Album = album;

					var albumArtist = ReadOptionalString(element, config.AlbumArtistFieldName);
					if (!string.IsNullOrEmpty(albumArtist))
						scrobble.AlbumArtist = albumArtist;

					if (config.FilterShortPlayedSongs)
					{
						var msPlayed = ReadOptionalInt(element, config.MillisecondsPlayedFieldName);

						if (msPlayed.HasValue && msPlayed.Value < config.MillisecondsPlayedThreshold)
							continue;
					}

					scrobbles.Add(scrobble);
				}
				catch (Exception ex)
				{
					errors.Add($"Object Number: {index} | Error: {ex.Message}");
				}
				finally
				{
					index++;
				}
			}

			return new FileParseResult(scrobbles, errors);
		}

		private static string ReadRequiredString(JsonElement element, string fieldName)
		{
			if (string.IsNullOrWhiteSpace(fieldName))
				throw new InvalidOperationException("Field name in configuration must not be empty.");

			if (!element.TryGetProperty(fieldName, out var prop) || prop.ValueKind == JsonValueKind.Null)
				throw new InvalidOperationException($"Missing required field '{fieldName}'.");

			var value = prop.GetString();

			if (string.IsNullOrWhiteSpace(value))
				throw new InvalidOperationException($"Field '{fieldName}' is empty.");

			return value;
		}

		private static string? ReadOptionalString(JsonElement element, string fieldName)
		{
			if (string.IsNullOrWhiteSpace(fieldName))
				return null;

			if (!element.TryGetProperty(fieldName, out var prop) || prop.ValueKind == JsonValueKind.Null)
				return null;

			return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
		}

		private static int? ReadOptionalInt(JsonElement element, string fieldName)
		{
			if (string.IsNullOrWhiteSpace(fieldName))
				return null;

			if (!element.TryGetProperty(fieldName, out var prop) || prop.ValueKind == JsonValueKind.Null)
				return null;

			if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i))
				return i;

			if (prop.ValueKind == JsonValueKind.String
				&& int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
				return parsed;

			throw new InvalidOperationException($"Field '{fieldName}' is not a valid integer.");
		}

		private static DateTimeOffset ReadDateTimeOffset(JsonElement element, string fieldName)
		{
			if (!element.TryGetProperty(fieldName, out var prop) || prop.ValueKind == JsonValueKind.Null)
				throw new InvalidOperationException($"Missing required field '{fieldName}'.");

			// common case: ISO-8601 string ("2024-01-01T12:34:56Z")
			if (prop.ValueKind == JsonValueKind.String)
			{
				var s = prop.GetString();
				if (string.IsNullOrWhiteSpace(s))
					throw new InvalidOperationException($"Field '{fieldName}' is empty.");

				if (DateTimeOffset.TryParse(
						s,
						CultureInfo.InvariantCulture,
						DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
						out var dto))
				{
					return dto;
				}

				throw new InvalidOperationException($"Field '{fieldName}' is not a valid timestamp.");
			}

			// if you ever encounter unix timestamps
			if (prop.ValueKind == JsonValueKind.Number)
			{
				if (prop.TryGetInt64(out var unixSeconds))
					return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

				throw new InvalidOperationException($"Field '{fieldName}' is not a valid unix timestamp.");
			}

			throw new InvalidOperationException($"Field '{fieldName}' has an unsupported timestamp format.");
		}
	}
}
