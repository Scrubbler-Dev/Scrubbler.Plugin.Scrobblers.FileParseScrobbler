using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Scrubbler.Import;

namespace Scrubbler.Test.FileParseScrobblerTest;

[TestFixture]
public sealed class ImportTests
{
    private string _directory = null!;
    private ImportStore _store = null!;
    private FakeClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "scrubbler-import-tests-" + Guid.NewGuid().ToString("N"));
        _store = new ImportStore(_directory);
        _clock = new FakeClock { Now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero) };
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private ImportJob Create(int count = 3, int amount = 500, string account = "alice", TimestampPolicy policy = TimestampPolicy.Import, DateTimeOffset? firstRunUtc = null, int dateOffsetDays = 0, TimeSpan? scrobbleTime = null)
    {
        var file = Path.Combine(_directory, Guid.NewGuid() + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new
        {
            ts = "2020-01-01T01:00:00Z", master_metadata_track_name = "Track " + i,
            master_metadata_album_artist_name = "Artist", ms_played = 100000
        })));
        return _store.Create(file, new ImportProfile(), account, amount, policy: policy, firstRunUtc: firstRunUtc, dateOffsetDays: dateOffsetDays, scrobbleTimeOfDay: scrobbleTime);
    }

    [Test]
    public async Task Fixed_scrobble_time_and_date_offset_give_repeated_tracks_distinct_seconds_across_batches()
    {
        var time = new TimeSpan(9, 30, 0);
        var job = Create(55, dateOffsetDays: 10, scrobbleTime: time);
        var entries = _store.Entries(job.Id);
        foreach (var entry in entries) entry.Track.Track = "Repeated track";
        using (_store.AcquireLock()) _store.Save(job, entries, "Repeated track fixture.");
        var reopened = new ImportStore(_directory);
        Assert.That(reopened.Get(job.Id).ScrobbleTimeOfDay, Is.EqualTo(time));
        var sender = new FakeSubmitter();
        await new ImportRunner(reopened, sender, _clock).RunDueAsync();
        var timestamps = reopened.Entries(job.Id).Select(e => e.SubmittedTimestamp!.Value).ToArray();
        var start = ImportTimestamp.AtTime(_clock.Now, 10, time, TimeZoneInfo.Local);
        Assert.That(timestamps, Is.EqualTo(Enumerable.Range(0, 55).Select(i => start.AddSeconds(i - 54))));
        Assert.That(timestamps, Is.All.LessThanOrEqualTo(start));
        Assert.That(timestamps.Distinct().Count(), Is.EqualTo(55));
        Assert.That(sender.Batches, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Future_scrobble_time_waits_and_keeps_timestamps_across_restart_and_midnight()
    {
        var localNow = new DateTimeOffset(2026, 9, 16, 22, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 16)));
        _clock.Now = localNow.ToUniversalTime();
        var job = Create(scrobbleTime: new TimeSpan(23, 59, 59));
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Is.Empty);
        var timestamps = _store.Entries(job.Id).Select(e => e.SubmittedTimestamp).ToArray();
        _clock.Now = _store.Get(job.Id).NextEligibleRunUtc;
        await new ImportRunner(new ImportStore(_directory), sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Has.Count.EqualTo(1));
        Assert.That(_store.Entries(job.Id).Select(e => e.SubmittedTimestamp), Is.EqualTo(timestamps));
    }

    [TestCase(-1)]
    [TestCase(86400)]
    public void Invalid_fixed_time_is_rejected(int seconds)
    {
        Assert.Throws<ArgumentException>(() => Create(scrobbleTime: TimeSpan.FromSeconds(seconds)));
    }

    [TestCase(0)]
    [TestCase(10)]
    public async Task Date_offset_is_persisted_and_applied_without_changing_cooldown(int days)
    {
        var job = Create(dateOffsetDays: days);
        var reopened = new ImportStore(_directory);
        Assert.That(reopened.Get(job.Id).DateOffsetDays, Is.EqualTo(days));
        await new ImportRunner(reopened, new FakeSubmitter(), _clock).RunDueAsync();
        var entries = reopened.Entries(job.Id);
        for (var i = 0; i < entries.Count; i++)
            Assert.That(entries[i].SubmittedTimestamp, Is.EqualTo(
                ImportTimestamp.Backdate(_clock.Now.AddSeconds(-entries.Count + i), days, TimeZoneInfo.Local)));
        Assert.That(reopened.Get(job.Id).NextEligibleRunUtc, Is.EqualTo(_clock.Now.AddHours(24).AddMinutes(1)));
        Assert.That(entries.All(e => e.Track.OriginalTimestamp!.Value.Year == 2020), Is.True);
    }

    [TestCase(-1)]
    [TestCase(11)]
    public void Invalid_date_offset_is_rejected(int days)
    {
        Assert.Throws<ArgumentException>(() => Create(dateOffsetDays: days));
        Assert.That(_store.List(), Is.Empty);
    }

    [Test]
    public void Backdating_preserves_Berlin_clock_time_across_daylight_saving_change()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var timestamp = new DateTimeOffset(2026, 10, 28, 15, 30, 0, TimeSpan.FromHours(1));
        var result = TimeZoneInfo.ConvertTime(ImportTimestamp.Backdate(timestamp, 10, berlin), berlin);
        Assert.That(result.DateTime, Is.EqualTo(new DateTime(2026, 10, 18, 15, 30, 0)));
        Assert.That(result.Offset, Is.EqualTo(TimeSpan.FromHours(2)));
    }

    [Test]
    public void Nonexistent_backdated_clock_time_requires_review_instead_of_silently_changing_time()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var timestamp = new DateTimeOffset(2026, 4, 1, 2, 30, 0, TimeSpan.FromHours(2));
        Assert.Throws<ArgumentException>(() => ImportTimestamp.Backdate(timestamp, 3, berlin));
    }

    [Test]
    public async Task Future_first_run_survives_restart_and_resume_without_running_early()
    {
        var firstRun = _clock.Now.AddHours(3).ToOffset(TimeSpan.FromHours(2));
        var job = Create(firstRunUtc: firstRun);
        var sender = new FakeSubmitter();
        _store.Pause(job.Id, true);
        _store.Pause(job.Id, false);
        var reopened = new ImportStore(_directory);
        Assert.That(reopened.Get(job.Id).NextEligibleRunUtc, Is.EqualTo(firstRun.ToUniversalTime()));
        await new ImportRunner(reopened, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Is.Empty);
        _clock.Now = firstRun;
        await new ImportRunner(reopened, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Has.Count.EqualTo(1));
    }

    [Test]
    public void Scheduler_first_boundary_preserves_the_chosen_instant()
    {
        var firstRun = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(1));
        var xml = XDocument.Parse(WindowsImportSchedule.CreateXml(@"C:\Runner\scrubbler-cli.exe", @"C:\Imports", "User", firstRun));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var boundary = xml.Descendants(ns + "StartBoundary").Single().Value;
        Assert.That(DateTimeOffset.Parse(boundary), Is.EqualTo(firstRun));
        Assert.That(boundary, Does.EndWith("+01:00"));
    }

    [Test]
    public async Task Acceptance_is_checkpointed_and_cooldown_survives_new_runner()
    {
        var job = Create(55, 51);
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches.Select(b => b.Count), Is.EqualTo(new[] { 50, 1 }));
        Assert.That(_store.Entries(job.Id).Count(e => e.Status == EntryStatus.Accepted), Is.EqualTo(51));
        Assert.That(_store.Get(job.Id).NextEligibleRunUtc, Is.EqualTo(_clock.Now.AddHours(24).AddMinutes(1)));
        _clock.Now = _clock.Now.AddHours(24);
        await new ImportRunner(new ImportStore(_directory), sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Has.Count.EqualTo(2));
        _clock.Now = _clock.Now.AddMinutes(1);
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(_store.Entries(job.Id).All(e => e.Status == EntryStatus.Accepted), Is.True);
    }

    [Test]
    public async Task Mixed_acceptance_and_daily_limit_preserve_unsent_rows()
    {
        var job = Create(60);
        var sender = new FakeSubmitter { Outcomes = b => b.Select((_, i) => new SubmissionResult(i == 0 ? SubmissionOutcome.Accepted : SubmissionOutcome.DailyLimit)).ToArray() };
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        var entries = _store.Entries(job.Id);
        Assert.That(sender.Batches, Has.Count.EqualTo(1));
        Assert.That(entries.Count(e => e.Status == EntryStatus.Accepted), Is.EqualTo(1));
        Assert.That(entries.Count(e => e.Status == EntryStatus.Pending), Is.EqualTo(59));
        Assert.That(entries.Where(e => e.Status == EntryStatus.Pending).All(e => e.SubmittedTimestamp == null), Is.True);
    }

    [Test]
    public async Task Timeout_after_successful_batch_never_replays_accepted_or_uncertain_entries()
    {
        var job = Create(60);
        var calls = 0;
        var sender = new FakeSubmitter { Outcomes = b => ++calls == 2 ? throw new HttpRequestException("Lost response") : b.Select(_ => new SubmissionResult(SubmissionOutcome.Accepted)).ToArray() };
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(_store.Entries(job.Id).Count(e => e.Status == EntryStatus.Accepted), Is.EqualTo(50));
        Assert.That(_store.Entries(job.Id).Count(e => e.Status == EntryStatus.Uncertain), Is.EqualTo(10));
        var timestamps = _store.Entries(job.Id).Select(e => e.SubmittedTimestamp).ToArray();
        _clock.Now = _clock.Now.AddDays(2);
        _store.Pause(job.Id, false);
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(calls, Is.EqualTo(2));
        Assert.That(_store.Entries(job.Id).Select(e => e.SubmittedTimestamp), Is.EqualTo(timestamps));
    }

    [Test]
    public async Task Crash_between_request_and_checkpoint_requires_review()
    {
        var job = Create();
        var entry = _store.Entries(job.Id)[0];
        entry.Status = EntryStatus.InFlight;
        entry.SubmittedTimestamp = _clock.Now;
        using (_store.AcquireLock()) _store.Save(job, [entry], "Simulate persisted intent.");
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Is.Empty);
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        Assert.That(_store.Entries(job.Id)[0].Status, Is.EqualTo(EntryStatus.Uncertain));
    }

    [Test]
    public async Task Account_cooldown_is_shared_across_jobs_but_not_accounts()
    {
        var first = Create(1);
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(_store.Entries(first.Id).Single().Status, Is.EqualTo(EntryStatus.Accepted));
        var second = Create(2);
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(_store.Entries(second.Id).All(e => e.Status == EntryStatus.Pending), Is.True);
        _store.Delete(second.Id);
        var third = Create(1, account: "bob");
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Has.Count.EqualTo(2));
        Assert.That(_store.Entries(third.Id).Single().Status, Is.EqualTo(EntryStatus.Accepted));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Unfinished_import_blocks_another_even_for_a_different_account(bool paused)
    {
        var job = Create();
        _store.Pause(job.Id, paused);
        Assert.Throws<InvalidOperationException>(() => Create(2, account: "bob"));
        Assert.That(_store.List(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Deleting_import_frees_slot_without_resetting_cooldown()
    {
        var job = Create(3, 1);
        await new ImportRunner(_store, new FakeSubmitter(), _clock).RunDueAsync();
        var cooldown = _store.AccountNextRun(job.Account);
        _store.Delete(job.Id);
        Assert.That(_store.List(), Is.Empty);
        Assert.That(_store.Entries(job.Id), Is.Empty);
        Assert.That(File.Exists(Path.Combine(_directory, job.SourceName)), Is.True);
        Assert.That(File.Exists(Path.Combine(_directory, job.Id + ".source")), Is.True);
        Assert.That(_store.AccountNextRun(job.Account), Is.EqualTo(cooldown));
        _ = Create(2);
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Is.Empty);
    }

    [Test]
    public void Delete_cannot_race_a_running_import()
    {
        var job = Create();
        using var lease = _store.AcquireLock();
        Assert.Throws<ImportBusyException>(() => _store.Delete(job.Id));
        Assert.That(_store.List(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Writer_lock_is_held_while_network_request_is_pending()
    {
        _ = Create();
        var sender = new FakeSubmitter { Outcomes = b =>
        {
            Assert.Throws<ImportBusyException>(() => new ImportStore(_directory).AcquireLock());
            return b.Select(_ => new SubmissionResult(SubmissionOutcome.Accepted)).ToArray();
        } };
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.DoesNotThrow(() => _store.AcquireLock().Dispose());
    }

    [Test]
    public async Task Source_is_snapshotted_original_dates_retained_and_generated_dates_ordered()
    {
        var job = Create();
        var snapshot = File.ReadAllBytes(Path.Combine(_directory, job.Id + ".source"));
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        var entries = _store.Entries(job.Id);
        Assert.That(entries.Select(e => e.Track.OriginalTimestamp!.Value.Year), Is.All.EqualTo(2020));
        Assert.That(entries.Select(e => e.SubmittedTimestamp), Is.Ordered.Ascending);
        Assert.That(entries.All(e => e.SubmittedTimestamp < _clock.Now), Is.True);
        Assert.That(File.ReadAllBytes(Path.Combine(_directory, job.Id + ".source")), Is.EqualTo(snapshot));
    }

    [Test]
    public async Task Expired_retry_timestamps_need_attention_without_network_calls()
    {
        var job = Create();
        var entries = _store.Entries(job.Id);
        foreach (var entry in entries) entry.SubmittedTimestamp = _clock.Now.AddDays(-15);
        using (_store.AcquireLock()) _store.Save(job, entries, "Previously attempted entries authorized for retry.");
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Is.Empty);
        Assert.That(_store.Entries(job.Id).All(e => e.Status == EntryStatus.NeedsAttention), Is.True);
    }

    [Test]
    public void Scheduled_jobs_cannot_be_created_with_original_timestamps()
    {
        Assert.Throws<ArgumentException>(() => Create(policy: TimestampPolicy.Original));
        Assert.That(_store.List(), Is.Empty);
    }

    [Test]
    public async Task Legacy_original_timestamp_jobs_are_paused_without_submitting_or_redating()
    {
        var job = Create();
        job.TimestampPolicy = TimestampPolicy.Original;
        using (_store.AcquireLock()) _store.Save(job, [], "Simulate a previously saved original-timestamp job.");
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Is.Empty);
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        Assert.That(_store.Entries(job.Id).All(e => e.Status == EntryStatus.Pending && e.SubmittedTimestamp == null), Is.True);
        Assert.Throws<ArgumentException>(() => _store.Pause(job.Id, false));
    }

    [Test]
    public async Task Invalid_authentication_pauses_job_and_preserves_entries()
    {
        var job = Create();
        var sender = new FakeSubmitter { Outcomes = b => b.Select(_ => new SubmissionResult(SubmissionOutcome.AuthenticationRequired)).ToArray() };
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        Assert.That(_store.Entries(job.Id).All(e => e.Status == EntryStatus.Pending), Is.True);
    }

    [Test]
    public void Duplicate_file_is_rejected_but_repeated_listens_are_preserved()
    {
        var file = Path.Combine(_directory, "repeats.json");
        File.WriteAllText(file, """[{"master_metadata_track_name":"Same","master_metadata_album_artist_name":"Artist"},{"master_metadata_track_name":"Same","master_metadata_album_artist_name":"Artist"}]""");
        var job = _store.Create(file, new ImportProfile(), "Alice");
        Assert.That(_store.Entries(job.Id), Has.Count.EqualTo(2));
        Assert.That(_store.Entries(job.Id).Select(e => e.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.Throws<InvalidOperationException>(() => _store.Create(file, new ImportProfile(), "alice"));
    }

    [Test]
    public void Parse_errors_require_explicit_opt_in_and_are_archived()
    {
        var file = Path.Combine(_directory, "bad.json");
        File.WriteAllText(file, """[{"master_metadata_track_name":"Valid","master_metadata_album_artist_name":"Artist"},{}]""");
        Assert.Throws<InvalidOperationException>(() => _store.Create(file, new ImportProfile(), "alice"));
        Assert.That(_store.List(), Is.Empty);
        var job = _store.Create(file, new ImportProfile(), "alice", allowParseErrors: true);
        Assert.That(File.Exists(Path.Combine(_directory, job.Id + ".errors.txt")), Is.True);
        Assert.That(_store.Entries(job.Id), Has.Count.EqualTo(1));
    }

    [Test]
    public void Invalid_intervals_and_amounts_are_rejected()
    {
        var job = Create();
        job.IntervalHours = 23;
        Assert.Throws<ArgumentException>(job.Validate);
        job.IntervalHours = double.NaN;
        Assert.Throws<ArgumentException>(job.Validate);
        job.IntervalHours = 24;
        job.MaxPerRun = 0;
        Assert.Throws<ArgumentException>(job.Validate);
    }

    [Test]
    public void Individual_response_codes_override_top_level_success()
    {
        var entries = new[] { new ImportEntry { Track = new("A", "B", _clock.Now), SubmittedTimestamp = DateTimeOffset.FromUnixTimeSeconds(123) },
            new ImportEntry { Track = new("C", "D", _clock.Now), SubmittedTimestamp = DateTimeOffset.FromUnixTimeSeconds(124) } };
        var results = LastFmSubmitter.ParseResponse("""<lfm status="ok"><scrobbles><scrobble><timestamp>123</timestamp><ignoredMessage code="0" /></scrobble><scrobble><timestamp>124</timestamp><ignoredMessage code="5" /></scrobble></scrobbles></lfm>""", entries);
        Assert.That(results.Select(r => r.Outcome), Is.EqualTo(new[] { SubmissionOutcome.Accepted, SubmissionOutcome.DailyLimit }));
        Assert.Throws<InvalidDataException>(() => LastFmSubmitter.ParseResponse("""<lfm status="ok"><scrobbles /></lfm>""", entries));
    }

    [Test]
    public async Task Account_mismatch_does_not_send_network_request()
    {
        using var http = new HttpClient(new RejectNetwork());
        var submitter = new LastFmSubmitter(http, () => new("bob", "session", "key", "secret"));
        var result = await submitter.SubmitAsync("alice", [new ImportEntry { Track = new("A", "B", _clock.Now) }], CancellationToken.None);
        Assert.That(result.Single().Outcome, Is.EqualTo(SubmissionOutcome.AuthenticationRequired));
    }

    [TestCase("Scrubbler.Plugin.Scrobblers.FileParseScrobbler", "Scrubbler.Plugin.Accounts.LastFm")]
    [TestCase("Scrubbler.Plugin.Scrobblers.FileParseScrobbler", "scrubbler.plugin.accounts.lastfm.lastfmaccountplugin")]
    [TestCase("scrubbler.plugin.scrobbler.fileparsescrobbler.fileparsescrobbleplugin", "scrubbler.plugin.accounts.lastfm.lastfmaccountplugin")]
    public void Automatic_setup_finds_bundled_runner_and_installed_account_without_user_paths(string filePluginFolder, string accountPluginFolder)
    {
        var root = Path.Combine(_directory, "Plugins");
        var runner = Path.Combine(root, filePluginFolder, "ImportRunner", "win-x64", "scrubbler-cli.exe");
        var account = Path.Combine(root, accountPluginFolder, "Scrubbler.Plugin.Accounts.LastFm.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(runner)!);
        Directory.CreateDirectory(Path.GetDirectoryName(account)!);
        File.WriteAllText(runner, "bundled runner");
        File.WriteAllText(account, "installed account");
        var settings = AutomaticImportSetup.Resolve(root);
        Assert.That(settings.ExecutablePath, Is.EqualTo(runner));
        Assert.That(settings.ApiConfigurationFile, Is.EqualTo(account));
    }

    [Test]
    public void Automatic_setup_does_not_use_temporary_shadow_copies()
    {
        var root = Path.Combine(_directory, "Plugins");
        var shadowRunner = Path.Combine(root, ".shadow", "old-install", "ImportRunner", "win-x64", "scrubbler-cli.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(shadowRunner)!);
        File.WriteAllText(shadowRunner, "temporary runner");
        Assert.Throws<InvalidOperationException>(() => AutomaticImportSetup.Resolve(root));
    }

    [Test]
    public async Task Job_waiting_for_automatic_setup_cannot_run_until_explicitly_resumed()
    {
        var source = Path.Combine(_directory, "waiting.json");
        File.WriteAllText(source, """[{"master_metadata_track_name":"Track","master_metadata_album_artist_name":"Artist"}]""");
        var job = _store.Create(source, new ImportProfile(), "alice", initiallyPaused: true);
        var sender = new FakeSubmitter();
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Is.Empty);
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        _store.Pause(job.Id, false);
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(sender.Batches, Has.Count.EqualTo(1));
    }

    [Test]
    public void Csv_headers_optional_columns_and_numeric_milliseconds_are_supported()
    {
        var file = Path.Combine(_directory, "spotify.csv");
        File.WriteAllText(file, "timestamp,track,artist,played\n2020-01-01T00:00:00Z,Short,Artist,1000\n2020-01-01T00:00:00Z,Long,Artist,90000\n");
        var profile = new ImportProfile { Format = "csv", Csv = new()
        {
            EncodingCodePage = 65001, Delimiter = ",", HasHeaderRecord = true,
            TimestampFieldIndex = 0, TrackFieldIndex = 1, ArtistFieldIndex = 2, MillisecondsPlayedFieldIndex = 3,
            FilterShortPlayedSongs = true
        } };
        var job = _store.Create(file, profile, "alice");
        Assert.That(_store.Entries(job.Id).Single().Track.Track, Is.EqualTo("Long"));
        Assert.That(_store.Entries(job.Id).Single().Track.Album, Is.Null.Or.Empty);
    }

    [Test]
    public async Task Explicit_resolution_keeps_request_timestamp_for_authorized_retry()
    {
        var job = Create(1);
        var sender = new FakeSubmitter { Outcomes = _ => throw new HttpRequestException("Lost response") };
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        var entry = _store.Entries(job.Id).Single();
        var timestamp = entry.SubmittedTimestamp;
        _store.Resolve(job.Id, entry.Id, EntryStatus.Pending);
        _store.Pause(job.Id, false);
        _clock.Now = _clock.Now.AddDays(2);
        await new ImportRunner(_store, new FakeSubmitter(), _clock).RunDueAsync();
        Assert.That(_store.Entries(job.Id).Single().Status, Is.EqualTo(EntryStatus.Accepted));
        Assert.That(_store.Entries(job.Id).Single().SubmittedTimestamp, Is.EqualTo(timestamp));
    }

    [Test]
    public async Task Cancellation_during_request_records_uncertainty_before_releasing_lock()
    {
        var job = Create();
        var sender = new FakeSubmitter { Outcomes = _ => throw new OperationCanceledException() };
        await new ImportRunner(_store, sender, _clock).RunDueAsync();
        Assert.That(_store.Entries(job.Id).All(e => e.Status == EntryStatus.Uncertain), Is.True);
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        Assert.DoesNotThrow(() => _store.AcquireLock().Dispose());
    }

    [Test]
    public void Schedule_xml_escapes_paths_and_uses_interactive_account_and_missed_start_policy()
    {
        var xml = XDocument.Parse(WindowsImportSchedule.CreateXml(@"C:\Music & Tools\scrubbler-cli.exe", @"C:\User Data\Imports", @"DOMAIN\User", _clock.Now));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.That(xml.Descendants(ns + "Command").Single().Value, Is.EqualTo(@"C:\Music & Tools\scrubbler-cli.exe"));
        Assert.That(xml.Descendants(ns + "Arguments").Single().Value, Does.Contain("\"C:\\User Data\\Imports\""));
        Assert.That(xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value, Is.EqualTo("IgnoreNew"));
        Assert.That(xml.Descendants(ns + "StartWhenAvailable").Single().Value, Is.EqualTo("true"));
        Assert.That(xml.Descendants(ns + "LogonType").Single().Value, Is.EqualTo("InteractiveToken"));
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSubmitter : IScrobbleSubmitter
    {
        public List<IReadOnlyList<ImportEntry>> Batches { get; } = [];
        public Func<IReadOnlyList<ImportEntry>, IReadOnlyList<SubmissionResult>> Outcomes { get; init; } = b => b.Select(_ => new SubmissionResult(SubmissionOutcome.Accepted)).ToArray();
        public Task<IReadOnlyList<SubmissionResult>> SubmitAsync(string account, IReadOnlyList<ImportEntry> entries, CancellationToken cancellationToken)
        {
            Batches.Add(entries);
            return Task.FromResult(Outcomes(entries));
        }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new AssertionException("Unexpected network request.");
    }
}
