using System.Globalization;
using System.Xml.Linq;

namespace Scrubbler.Import;

/// <summary>Reads complete, bounded history ranges; never submits or edits remote plays.</summary>
public sealed class LastFmHistoryReader(HttpClient http, Func<LastFmCredentials> credentials) : ILastFmHistoryReader
{
    public async Task<IReadOnlyList<HistoryPlay>> GetAsync(string account, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var auth = credentials();
        if (!string.Equals(account, auth.Account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Sign in as {account} before reviewing this import.");
        var plays = new List<HistoryPlay>();
        int? expectedPages = null;
        int? expectedTotal = null;
        for (var page = 1; ; page++)
        {
            var query = new Dictionary<string, string>
            {
                ["method"] = "user.getRecentTracks", ["user"] = account, ["api_key"] = auth.ApiKey,
                ["from"] = from.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["to"] = to.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["limit"] = "200", ["page"] = page.ToString(CultureInfo.InvariantCulture)
            };
            string body;
            try
            {
                using var response = await http.GetAsync("https://ws.audioscrobbler.com/2.0/?" +
                    string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))), cancellationToken);
                if (!response.IsSuccessStatusCode) throw new IOException($"Last.fm history is unavailable (HTTP {(int)response.StatusCode}). Try checking again.");
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException) { throw new IOException("Could not connect to Last.fm. Try checking again."); }
            var root = XDocument.Parse(body).Root ?? throw new InvalidDataException("Empty Last.fm history response.");
            if (root.Name != "lfm" || (string?)root.Attribute("status") != "ok")
                throw new InvalidDataException($"Last.fm could not read history (error {(int?)root.Element("error")?.Attribute("code")}). Try checking again.");
            var recent = root.Element("recenttracks") ?? throw new InvalidDataException("Last.fm returned incomplete history. Try checking again.");
            if (!string.Equals((string?)recent.Attribute("user"), account, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Last.fm returned history for a different account.");
            var pages = (int?)recent.Attribute("totalPages");
            var total = (int?)recent.Attribute("total");
            if ((int?)recent.Attribute("page") != page || pages is null or < 0 or > 100 || total is null or < 0
                || (pages == 0 && total != 0) || (page > pages && total != 0))
                throw new InvalidDataException("Last.fm history pagination is incomplete or too large. Try checking again with fewer entries.");
            if (expectedPages.HasValue && (expectedPages != pages || expectedTotal != total))
                throw new InvalidDataException("Last.fm history changed during review. Try checking again.");
            expectedPages = pages;
            expectedTotal = total;
            foreach (var track in recent.Elements("track"))
            {
                if ((string?)track.Attribute("nowplaying") == "true") continue;
                if ((long?)track.Element("date")?.Attribute("uts") is not { } timestamp
                    || track.Element("artist") is not { } artist || track.Element("name") is not { } name)
                    throw new InvalidDataException("Last.fm returned a play without its timestamp or metadata. Try checking again.");
                var date = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                if (date < from || date > to) throw new InvalidDataException("Last.fm returned history outside the requested dates.");
                plays.Add(new(artist.Value, name.Value, track.Element("album")?.Value, date));
            }
            if (page >= pages)
            {
                if (plays.Count != total) throw new InvalidDataException("Last.fm returned incomplete history. Try checking again.");
                return plays;
            }
        }
    }
}
