using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.JSON;

namespace Scrubbler.Test.FileParseScrobblerTest.Parser.JSON;

[TestFixture]
public class JsonConfigurationTests
{
    [Test]
    public void Default_configuration_has_expected_field_names()
    {
        var def = JsonFileParserConfiguration.Default;
		using (Assert.EnterMultipleScope())
		{
			Assert.That(def.TimestampFieldName, Is.Not.Null.And.Not.Empty);
			Assert.That(def.TrackFieldName, Is.Not.Null.And.Not.Empty);
			Assert.That(def.ArtistFieldName, Is.Not.Null.And.Not.Empty);
		}
	}

    [Test]
    public void Validate_emptyTimestampName_throws()
    {
        var cfg = JsonFileParserConfiguration.Default with { TimestampFieldName = "" };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Test]
    public void Validate_emptyTrackName_throws()
    {
        var cfg = JsonFileParserConfiguration.Default with { TrackFieldName = "   " };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Test]
    public void Validate_emptyArtistName_throws()
    {
        var cfg = JsonFileParserConfiguration.Default with { ArtistFieldName = string.Empty };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Test]
    public void Validate_filterWithNonPositiveThreshold_throws()
    {
        var cfg = JsonFileParserConfiguration.Default with { FilterShortPlayedSongs = true, MillisecondsPlayedThreshold = 0 };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }
}
