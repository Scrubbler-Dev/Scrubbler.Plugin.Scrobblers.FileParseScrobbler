using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Scrubbler.Import;

public sealed record LastFmCredentials(string Account, string SessionKey, string ApiKey, string ApiSecret)
{
    // Read-only compatibility with the account plugin's existing FileSecureStore.
    // Secrets are never copied into jobs, scheduler arguments or logs.
    public static LastFmCredentials Load(string? envFile = null)
    {
        var data = ReadSavedSession();
        var values = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(envFile) && Path.GetExtension(envFile).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            // Read release-injected constants as metadata; never load or execute plugin code in the CLI.
            using var stream = File.OpenRead(envFile);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if (metadata.GetString(type.Name) != "PluginDefaults" || metadata.GetString(type.Namespace) != "Scrubbler.Plugin.Accounts.LastFm") continue;
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = metadata.GetFieldDefinition(fieldHandle);
                    if (field.GetDefaultValue().IsNil) continue;
                    var constant = metadata.GetConstant(field.GetDefaultValue());
                    if (constant.TypeCode != ConstantTypeCode.String) continue;
                    var name = metadata.GetString(field.Name);
                    if (name is "ApiKey" or "ApiSecret")
                        values[name == "ApiKey" ? "LASTFM_API_KEY" : "LASTFM_API_SECRET"] = Encoding.Unicode.GetString(metadata.GetBlobBytes(constant.Value));
                }
            }
            // Development builds use environment.env beside the DLL.
            envFile = Path.Combine(Path.GetDirectoryName(envFile)!, "environment.env");
        }
        if (!string.IsNullOrWhiteSpace(envFile) && File.Exists(envFile))
            foreach (var line in File.ReadLines(envFile))
            {
                if (line.TrimStart().StartsWith('#')) continue;
                var parts = line.Split('=', 2);
                if (parts.Length == 2) values[parts[0].Trim()] = parts[1].Trim().Trim('"', '\'');
            }
        string Key(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : values.TryGetValue(name, out var fileValue) && fileValue.Length > 0 && fileValue != name ? fileValue
            : throw new InvalidOperationException($"Missing {name}. Select the installed Last.fm plugin DLL or environment.env file.");
        return new(data["LastFmAccountId"], data["LastFmSessionKey"], Key("LASTFM_API_KEY"), Key("LASTFM_API_SECRET"));
    }

    public static string SavedAccount() => ReadSavedSession().GetValueOrDefault("LastFmAccountId") ?? "";

    private static Dictionary<string, string> ReadSavedSession()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrubbler", "Plugins", "Last.fm", "settings.dat");
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes("Last.fm"));
        aes.IV = new byte[16];
        using var decryptor = aes.CreateDecryptor();
        var encrypted = File.ReadAllBytes(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length))
            ?? throw new InvalidDataException("No saved Last.fm session.");
    }
}

/// <summary>Thin Last.fm adapter retaining every individual ignored-message result.</summary>
public sealed class LastFmSubmitter(HttpClient http, Func<LastFmCredentials> credentials) : IScrobbleSubmitter
{
    public async Task<IReadOnlyList<SubmissionResult>> SubmitAsync(string account, IReadOnlyList<ImportEntry> entries, CancellationToken cancellationToken)
    {
        LastFmCredentials auth;
        try { auth = credentials(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or KeyNotFoundException or InvalidOperationException or BadImageFormatException)
        {
            return Repeat(entries.Count, SubmissionOutcome.AuthenticationRequired, "Authentication unavailable. Sign in through Scrubbler and configure the API credentials, then resume.");
        }
        if (!string.Equals(auth.Account, account, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(auth.SessionKey))
            return Repeat(entries.Count, SubmissionOutcome.AuthenticationRequired, "Saved Last.fm account does not match this job. Sign in to the job's account.");
        if (entries.Count is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(entries));
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        { ["method"] = "track.scrobble", ["api_key"] = auth.ApiKey, ["sk"] = auth.SessionKey };
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            parameters[$"artist[{i}]"] = entry.Track.Artist;
            parameters[$"track[{i}]"] = entry.Track.Track;
            parameters[$"timestamp[{i}]"] = entry.SubmittedTimestamp!.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(entry.Track.Album)) parameters[$"album[{i}]"] = entry.Track.Album;
            if (!string.IsNullOrEmpty(entry.Track.AlbumArtist)) parameters[$"albumArtist[{i}]"] = entry.Track.AlbumArtist;
        }
        var signature = string.Concat(parameters.Select(p => p.Key + p.Value)) + auth.ApiSecret;
        parameters["api_sig"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await http.PostAsync("https://ws.audioscrobbler.com/2.0/", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseResponse(body, entries);
    }

    public static IReadOnlyList<SubmissionResult> ParseResponse(string xml, IReadOnlyList<ImportEntry> entries)
    {
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("Empty Last.fm response.");
        if (root.Name != "lfm") throw new InvalidDataException("Unexpected Last.fm response.");
        if ((string?)root.Attribute("status") == "failed")
        {
            var code = (int?)root.Element("error")?.Attribute("code");
            var outcome = code switch
            {
                9 or 4 or 10 or 13 or 26 => SubmissionOutcome.AuthenticationRequired,
                11 or 16 or 29 => SubmissionOutcome.Retry,
                2 or 3 or 5 or 6 or 7 => SubmissionOutcome.Rejected,
                _ => SubmissionOutcome.Uncertain
            };
            return Repeat(entries.Count, outcome, $"Last.fm error {code}.");
        }
        if ((string?)root.Attribute("status") != "ok") throw new InvalidDataException("Missing Last.fm status.");
        var scrobbles = root.Element("scrobbles")?.Elements("scrobble").ToArray();
        if (scrobbles == null || scrobbles.Length != entries.Count) throw new InvalidDataException("Incomplete Last.fm response.");
        var results = new List<SubmissionResult>();
        for (var i = 0; i < entries.Count; i++)
        {
            var scrobble = scrobbles[i];
            if ((long?)scrobble.Element("timestamp") != entries[i].SubmittedTimestamp!.Value.ToUnixTimeSeconds())
                throw new InvalidDataException("Response timestamps do not match request order.");
            var ignored = scrobble.Element("ignoredMessage") ?? scrobble.Element("ignoredmessage");
            var code = (int?)ignored?.Attribute("code");
            var outcome = code switch { 0 => SubmissionOutcome.Accepted, 5 => SubmissionOutcome.DailyLimit,
                1 or 2 or 3 or 4 => SubmissionOutcome.Rejected, _ => SubmissionOutcome.Uncertain };
            results.Add(new(outcome, code == 0 ? null : $"Last.fm ignored code {code}."));
        }
        return results;
    }

    private static SubmissionResult[] Repeat(int count, SubmissionOutcome outcome, string message) =>
        Enumerable.Range(0, count).Select(_ => new SubmissionResult(outcome, message)).ToArray();
}
