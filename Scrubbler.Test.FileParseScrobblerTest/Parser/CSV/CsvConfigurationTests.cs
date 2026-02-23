using Scrubbler.Plugin.Scrobblers.FileParseScrobbler.Parser.CSV;
using System.Text;

namespace Scrubbler.Test.FileParseScrobblerTest.Parser.CSV;

[TestFixture]
public class CsvConfigurationTests
{
    [Test]
    public void Default_hasExpectedEncodingAndDelimiter()
    {
        var def = CsvFileParserConfiguration.Default;
		using (Assert.EnterMultipleScope())
		{
			Assert.That(def.Encoding.CodePage, Is.EqualTo(Encoding.Unicode.CodePage));
			Assert.That(def.Delimiter, Is.EqualTo(";"));
			Assert.That(def.TimestampFieldIndex, Is.GreaterThanOrEqualTo(0));
		}
	}

    [Test]
    public void Validate_emptyDelimiter_throws()
    {
        var cfg = CsvFileParserConfiguration.Default with { Delimiter = "" };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Test]
    public void Validate_negativeFieldIndices_throw()
    {
        var baseCfg = CsvFileParserConfiguration.Default;
        var cfg1 = baseCfg with { TimestampFieldIndex = -1 };
        Assert.Throws<InvalidOperationException>(() => cfg1.Validate());

        var cfg2 = baseCfg with { TrackFieldIndex = -1 };
        Assert.Throws<InvalidOperationException>(() => cfg2.Validate());

        var cfg3 = baseCfg with { ArtistFieldIndex = -1 };
        Assert.Throws<InvalidOperationException>(() => cfg3.Validate());
    }

    [Test]
    public void Validate_filterWithNonPositiveThreshold_throws()
    {
        var cfg = CsvFileParserConfiguration.Default with { FilterShortPlayedSongs = true, MillisecondsPlayedThreshold = 0 };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }
}
