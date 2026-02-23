using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON;
using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;
using System.Text;

namespace Scrubbler.Test.FileParseScrobblerTest.Parser.JSON;

[TestFixture]
public class JsonFileParserAdditionalTests
{
    private static string CreateTempJson(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, content, Encoding.UTF8);
        return file;
    }

    [Test]
    public void Parse_missingRequiredTrackField_addsError()
    {
        var sut = new JsonFileParser();
        var config = JsonFileParserConfiguration.Default;

        var json = $"[{{ \"{config.ArtistFieldName}\": \"Artist Only\" }}]";

        var file = CreateTempJson(json);
        try
        {
            var result = sut.Parse(file, config, ScrobbleMode.Import);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.Scrobbles, Is.Empty);
				Assert.That(result.Errors, Is.Not.Empty);
			}
			Assert.That(result.Errors.Single(), Does.Contain($"Missing required field '{config.TrackFieldName}'"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void Parse_requiredFieldEmpty_addsError()
    {
        var sut = new JsonFileParser();
        var config = JsonFileParserConfiguration.Default;

        var json = $"[{{ \"{config.TrackFieldName}\": \"\", \"{config.ArtistFieldName}\": \"Artist\", \"{config.TimestampFieldName}\": \"2025-01-02T03:04:05Z\" }}]";
        var file = CreateTempJson(json);
        try
        {
            var result = sut.Parse(file, config, ScrobbleMode.UseScrobbleTimestamp);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.Scrobbles, Is.Empty);
				Assert.That(result.Errors.Single(), Does.Contain($"Field '{config.TrackFieldName}' is empty."));
			}
		}
        finally { File.Delete(file); }
    }

    [Test]
    public void Parse_msPlayedNotInteger_addsError()
    {
        var sut = new JsonFileParser();
        var config = JsonFileParserConfiguration.Default with { FilterShortPlayedSongs = true };

        var json = $"[{{ \"{config.TimestampFieldName}\": \"2025-01-02T03:04:05Z\", \"{config.TrackFieldName}\": \"T\", \"{config.ArtistFieldName}\": \"A\", \"{config.MillisecondsPlayedFieldName}\": \"not-an-int\" }}]";
        var file = CreateTempJson(json);
        try
        {
            var result = sut.Parse(file, config, ScrobbleMode.UseScrobbleTimestamp);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.Scrobbles, Is.Empty);
				Assert.That(result.Errors.Single(), Does.Contain($"Field '{config.MillisecondsPlayedFieldName}' is not a valid integer."));
			}
		}
        finally { File.Delete(file); }
    }

    [Test]
    public void Parse_timestampNumber_parsesUnixSeconds()
    {
        var sut = new JsonFileParser();
        var config = JsonFileParserConfiguration.Default;

        long unix = 1700000000L; // some unix seconds
        var json = $"[{{ \"{config.TimestampFieldName}\": {unix}, \"{config.TrackFieldName}\": \"T\", \"{config.ArtistFieldName}\": \"A\" }}]";
        var file = CreateTempJson(json);
        try
        {
            var result = sut.Parse(file, config, ScrobbleMode.UseScrobbleTimestamp);
            Assert.That(result.Errors, Is.Empty);
            var scrobbles = result.Scrobbles.ToList();
            Assert.That(scrobbles, Has.Count.EqualTo(1));
            var expected = DateTimeOffset.FromUnixTimeSeconds(unix).DateTime;
            Assert.That(scrobbles[0].Timestamp.DateTime, Is.EqualTo(expected));
        }
        finally { File.Delete(file); }
    }

    [Test]
    public void Parse_unsupportedTimestampFormat_addsError()
    {
        var sut = new JsonFileParser();
        var config = JsonFileParserConfiguration.Default;

        var json = $"[{{ \"{config.TimestampFieldName}\": true, \"{config.TrackFieldName}\": \"T\", \"{config.ArtistFieldName}\": \"A\" }}]";
        var file = CreateTempJson(json);
        try
        {
            var result = sut.Parse(file, config, ScrobbleMode.UseScrobbleTimestamp);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(result.Scrobbles, Is.Empty);
				Assert.That(result.Errors.Single(), Does.Contain("unsupported timestamp format"));
			}
		}
        finally { File.Delete(file); }
    }
}
