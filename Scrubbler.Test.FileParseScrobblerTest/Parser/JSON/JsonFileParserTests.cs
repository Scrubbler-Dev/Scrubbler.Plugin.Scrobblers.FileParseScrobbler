using Scrubbler.Abstractions;
using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;
using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON;
using System.Globalization;
using System.Text;


namespace Scrubbler.Test.FileParseScrobblerTest.Parser.JSON;

/// <summary>
/// Tests for JsonFileParser.Parse focused on argument validation, root JSON shape,
/// error capture for invalid items, filtering behavior and timestamp handling.
/// </summary>
[TestFixture]
public class JsonFileParserTests
{
	/// <summary>
	/// Ensures Parse throws ArgumentNullException for null/empty/whitespace file paths.
	/// Input: file parameter null/empty/whitespace, valid configuration.
	/// Expected: ArgumentNullException is thrown.
	/// </summary>
	[TestCase(null)]
	[TestCase("")]
	[TestCase("   ")]
	public void Parse_fileIsNullOrWhitespace_throwsArgumentNullException(string? file)
	{
		// Arrange
		var sut = new JsonFileParser();
		var config = JsonFileParserConfiguration.Default;

		// Act / Assert
		Assert.Throws<ArgumentNullException>(() => sut.Parse(file!, config, ScrobbleMode.UseScrobbleTimestamp));
	}

	/// <summary>
	/// Ensures Parse throws when root JSON element is not an array.
	/// Input: JSON file with an object at root.
	/// Expected: InvalidOperationException with expected message.
	/// </summary>
	[Test]
	public void Parse_rootIsNotArray_throwsInvalidOperationException()
	{
		// Arrange
		var sut = new JsonFileParser();
		var config = JsonFileParserConfiguration.Default;
		var file = CreateTempJson("{ \"a\": 1 }");

		try
		{
			// Act & Assert
			var ex = Assert.Throws<InvalidOperationException>(() => sut.Parse(file, config, ScrobbleMode.UseScrobbleTimestamp));
			Assert.That(ex!.Message, Is.EqualTo("Expected a JSON array at the root."));
		}
		finally
		{
			File.Delete(file);
		}
	}

	/// <summary>
	/// Ensures entries with ms_played lower than threshold are filtered out when FilterShortPlayedSongs is true.
	/// Input: JSON array with one item having ms_played below default threshold.
	/// Expected: No scrobbles returned and no errors.
	/// </summary>
	[Test]
	public void Parse_filterShortPlayedSongs_filtersOutShortPlayed()
	{
		// Arrange
		var sut = new JsonFileParser();
		var config = JsonFileParserConfiguration.Default with { FilterShortPlayedSongs = true };
		var json = $@"[
                    {{
                        ""ts"": ""2025-01-02T03:04:05Z"",
                        ""{config.TrackFieldName}"": ""Track A"",
                        ""{config.ArtistFieldName}"": ""Artist A"",
                        ""{config.MillisecondsPlayedFieldName}"": 1000
                    }}
                ]";
		var file = CreateTempJson(json);

		try
		{
			// Act
			var result = sut.Parse(file, config, ScrobbleMode.UseScrobbleTimestamp);

			using (Assert.EnterMultipleScope())
			{
				// Assert
				Assert.That(result.Errors, Is.Empty);
				Assert.That(result.Scrobbles, Is.Empty);
			}
		}
		finally
		{
			File.Delete(file);
		}
	}

	/// <summary>
	/// Ensures Parse reads required and optional string fields and parses timestamp correctly for non-Import mode.
	/// Input: JSON array with timestamp string, required track/artist and non-empty album and album artist.
	/// Expected: One scrobble created with Album and AlbumArtist assigned, timestamp parsed to expected DateTime (UTC).
	/// </summary>
	[Test]
	public void Parse_validItem_parsesTimestampAndOptionalFields()
	{
		// Arrange
		var sut = new JsonFileParser();
		var config = JsonFileParserConfiguration.Default;
		var timestampString = "2025-01-02T03:04:05Z";
		var json = $@"[
                    {{
                        ""ts"": ""{timestampString}"",
                        ""{config.TrackFieldName}"": ""Track B"",
                        ""{config.ArtistFieldName}"": ""Artist B"",
                        ""{config.AlbumFieldName}"": ""Album B"",
                        ""{config.AlbumArtistFieldName}"": ""AlbumArtist B"",
                        ""{config.MillisecondsPlayedFieldName}"": 60000
                    }}
                ]";
		var file = CreateTempJson(json);

		try
		{
			// Act
			var result = sut.Parse(file, config, ScrobbleMode.UseScrobbleTimestamp);

			using (Assert.EnterMultipleScope())
			{
				// Assert
				Assert.That(result.Errors, Is.Empty);
				Assert.That(result.Scrobbles, Is.Not.Empty);
			}
			var scrobbles = new List<ScrobbleData>(result.Scrobbles);
			Assert.That(scrobbles, Has.Count.EqualTo(1));
			var s = scrobbles[0];
			using (Assert.EnterMultipleScope())
			{
				Assert.That(s.Album, Is.EqualTo("Album B"));
				Assert.That(s.AlbumArtist, Is.EqualTo("AlbumArtist B"));
			}

			// Compute expected DateTime using same parsing rules as production (UTC)
			var expectedDto = DateTimeOffset.Parse(timestampString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
			Assert.That(s.Timestamp.DateTime, Is.EqualTo(expectedDto.DateTime));
		}
		finally
		{
			File.Delete(file);
		}
	}

	/// <summary>
	/// Ensures Import mode uses current time instead of reading timestamp from JSON.
	/// Input: JSON object missing timestamp but with required fields; mode = Import.
	/// Expected: A scrobble is created and timestamp is approximately DateTime.Now (within a small tolerance).
	/// </summary>
	[Test]
	public void Parse_modeImport_usesCurrentTimeForTimestamp()
	{
		// Arrange
		var sut = new JsonFileParser();
		var config = JsonFileParserConfiguration.Default;
		var json = $@"[
                    {{
                        ""{config.TrackFieldName}"": ""Track C"",
                        ""{config.ArtistFieldName}"": ""Artist C""
                    }}
                ]";
		var file = CreateTempJson(json);

		try
		{
			var before = DateTime.Now;
			// Act
			var result = sut.Parse(file, config, ScrobbleMode.Import);
			var after = DateTime.Now;

			// Assert
			Assert.That(result.Errors, Is.Empty);
			var scrobbles = new List<ScrobbleData>(result.Scrobbles);
			Assert.That(scrobbles, Has.Count.EqualTo(1));
			var s = scrobbles[0];

			// Timestamp should be between before and after (allowing for slight clock differences)
			Assert.That(s.Timestamp.DateTime, Is.GreaterThanOrEqualTo(before.AddSeconds(-1)));
			Assert.That(s.Timestamp.DateTime, Is.LessThanOrEqualTo(after.AddSeconds(1)));
		}
		finally
		{
			File.Delete(file);
		}
	}

	// Helper: create a temp file containing JSON and return its path.
	private static string CreateTempJson(string content)
	{
		var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
		File.WriteAllText(file, content, Encoding.UTF8);
		return file;
	}
}