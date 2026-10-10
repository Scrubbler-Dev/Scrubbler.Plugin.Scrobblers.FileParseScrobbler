using Microsoft.Data.Sqlite;
using Scrubbler.Import;
using Scrubbler.Plugin.Scrobbler.FileParseScrobbler;
using System.Net;
using System.Xml.Linq;

namespace Scrubbler.Test.FileParseScrobblerTest;

[TestFixture]
public sealed class ImportReviewTests
{
    private string _directory = null!;
    private ImportStore _store = null!;
    private readonly FixedClock _clock = new();

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "scrubbler-review-" + Guid.NewGuid().ToString("N"));
        _store = new ImportStore(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private (ImportJob Job, ImportEntry[] Entries) Create(int count = 3, EntryStatus status = EntryStatus.Uncertain)
    {
        var file = Path.Combine(_directory, "source.json");
        File.WriteAllText(file, "[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $$"""{"master_metadata_track_name":"Track {{i}}","master_metadata_album_artist_name":"Artist","master_metadata_album_album_name":"Album"}""")) + "]");
        var job = _store.Create(file, new ImportProfile(), "alice", initiallyPaused: true);
        var entries = _store.Entries(job.Id).ToArray();
        foreach (var entry in entries)
        {
            entry.Status = status;
            entry.SubmittedTimestamp = _clock.GetUtcNow().AddHours(-1).AddSeconds(entry.Track.SourceIndex);
        }
        job.NextEligibleRunUtc = _clock.GetUtcNow().AddHours(12);
        using (_store.AcquireLock()) _store.Save(job, entries, "Fixture", _clock.GetUtcNow().AddHours(24));
        return (job, entries);
    }

    private static HistoryPlay Play(ImportEntry entry, string? artist = null, string? track = null, string? album = null)
        => new(artist ?? entry.Track.Artist, track ?? entry.Track.Track, album ?? entry.Track.Album, entry.SubmittedTimestamp!.Value);

    [Test]
    public async Task Review_finds_matches_and_leaves_missing_or_corrected_metadata_for_user_review()
    {
        var (job, entries) = Create(4);
        var reader = new HistoryStub([Play(entries[0], artist: "ARTIST", album: "album"), Play(entries[2], track: "Corrected title"), Play(entries[3], album: "Other album")]);
        var result = await new ImportReviewService(_store, reader, _clock).CheckAsync(job.Id);
        Assert.That(result.ConfirmedCount, Is.EqualTo(1));
        Assert.That(result.Entries.Select(e => e.Match), Is.EqualTo(new[] { HistoryMatch.Missing, HistoryMatch.Ambiguous, HistoryMatch.Ambiguous }));
        Assert.That(reader.From, Is.EqualTo(entries[0].SubmittedTimestamp!.Value.AddSeconds(-1)));
        Assert.That(reader.To, Is.EqualTo(entries[3].SubmittedTimestamp!.Value.AddSeconds(1)));
        Assert.That(_store.Entries(job.Id).Select(e => e.Status), Is.EqualTo(new[] { EntryStatus.Accepted, EntryStatus.Uncertain, EntryStatus.Uncertain, EntryStatus.Uncertain }));
        Assert.That(_store.Get(job.Id).Paused, Is.True);
    }

    [Test]
    public async Task All_verified_plays_are_recorded_without_manual_decisions_and_leave_an_empty_review()
    {
        var (job, entries) = Create(status: EntryStatus.InFlight);
        var review = await new ImportReviewService(_store, new HistoryStub(entries.Select(e => Play(e)).ToArray()), _clock).CheckAsync(job.Id);
        Assert.That(review.Entries, Is.Empty);
        Assert.That(review.ConfirmedCount, Is.EqualTo(entries.Length));
        Assert.That(_store.Entries(job.Id).Select(e => e.Status), Is.All.EqualTo(EntryStatus.Accepted));
        Assert.That(_store.Entries(job.Id).Select(e => e.SubmittedTimestamp), Is.EqualTo(entries.Select(e => e.SubmittedTimestamp)));
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        Assert.That(_store.Get(job.Id).NextEligibleRunUtc, Is.EqualTo(job.NextEligibleRunUtc));
        Assert.That(_store.AccountNextRun("alice"), Is.EqualTo(_clock.GetUtcNow().AddHours(24)));
        var dialog = new ImportReviewDialogViewModel(review, _clock.GetUtcNow());
        Assert.That(dialog.HasEntries, Is.False);
        Assert.That(dialog.CanSave, Is.False);
        Assert.That(dialog.SelectedEntry, Is.Null);
        Assert.That(dialog.Summary, Is.EqualTo("Nothing left to review."));
    }

    [Test]
    public async Task Subsequent_checks_only_review_entries_that_remain_unresolved()
    {
        var (job, entries) = Create();
        var reader = new HistoryStub([Play(entries[0])]);
        var service = new ImportReviewService(_store, reader, _clock);
        await service.CheckAsync(job.Id);
        var secondReview = await service.CheckAsync(job.Id);
        Assert.That(secondReview.ConfirmedCount, Is.Zero);
        Assert.That(secondReview.Entries.Select(e => e.Entry.Id), Is.EqualTo(entries.Skip(1).Select(e => e.Id)));
        Assert.That(reader.From, Is.EqualTo(entries[1].SubmittedTimestamp!.Value.AddSeconds(-1)));
        Assert.That(_store.Entries(job.Id)[0].Status, Is.EqualTo(EntryStatus.Accepted));
    }

    [Test]
    public void Automatic_confirmation_rejects_stale_matches_without_partial_changes()
    {
        var (job, entries) = Create(2);
        var reader = new HistoryStub(entries.Select(e => Play(e)).ToArray(), beforeReturn: () => _store.Resolve(job.Id, entries[1].Id, EntryStatus.Skipped));
        var service = new ImportReviewService(_store, reader, _clock);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CheckAsync(job.Id));
        Assert.That(_store.Entries(job.Id).Select(e => e.Status), Is.EqualTo(new[] { EntryStatus.Uncertain, EntryStatus.Skipped }));
    }

    [Test]
    public void Incomplete_history_cannot_confirm_matches_from_an_earlier_page()
    {
        var (job, entries) = Create(2);
        var first = Track(entries[0].SubmittedTimestamp!.Value.ToUnixTimeSeconds());
        first.Element("name")!.Value = entries[0].Track.Track;
        var second = Track(entries[1].SubmittedTimestamp!.Value.ToUnixTimeSeconds());
        second.Element("date")!.Remove();
        using var http = new HttpClient(new ResponseHandler([Page(1, 2, 2, first), Page(2, 2, 2, second)]));
        var service = new ImportReviewService(_store, new LastFmHistoryReader(http, () => new("alice", "unused", "key", "unused")), _clock);
        Assert.ThrowsAsync<InvalidDataException>(async () => await service.CheckAsync(job.Id));
        Assert.That(_store.Entries(job.Id).Select(e => e.Status), Is.All.EqualTo(EntryStatus.Uncertain));
    }

    [Test]
    public async Task Duplicate_remote_plays_or_local_timestamps_are_ambiguous()
    {
        var (job, entries) = Create();
        entries[2].SubmittedTimestamp = entries[1].SubmittedTimestamp;
        using (_store.AcquireLock()) _store.Save(job, entries, "Duplicate timestamp fixture");
        var review = await new ImportReviewService(_store, new HistoryStub([Play(entries[0]), Play(entries[0]), Play(entries[1])]), _clock).CheckAsync(job.Id);
        Assert.That(review.Entries.Select(e => e.Match), Is.All.EqualTo(HistoryMatch.Ambiguous));
    }

    [Test]
    public async Task In_flight_review_pauses_and_checkpoints_uncertainty_without_changing_cooldowns()
    {
        var (job, entries) = Create(status: EntryStatus.InFlight);
        job.Paused = false;
        using (_store.AcquireLock()) _store.Save(job, [], "Running fixture");
        var review = await new ImportReviewService(_store, new HistoryStub([]), _clock).CheckAsync(job.Id);
        Assert.That(review.Entries.Select(e => e.Entry.Status), Is.All.EqualTo(EntryStatus.Uncertain));
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        Assert.That(_store.Get(job.Id).NextEligibleRunUtc, Is.EqualTo(job.NextEligibleRunUtc));
        Assert.That(_store.AccountNextRun("alice"), Is.EqualTo(_clock.GetUtcNow().AddHours(24)));
    }

    [Test]
    public void Failed_history_lookup_does_not_resolve_any_entries()
    {
        var (job, _) = Create();
        var service = new ImportReviewService(_store, new HistoryStub([], fail: true), _clock);
        Assert.ThrowsAsync<IOException>(async () => await service.CheckAsync(job.Id));
        Assert.That(_store.Entries(job.Id).Select(e => e.Status), Is.All.EqualTo(EntryStatus.Uncertain));
        Assert.That(_store.Get(job.Id).Paused, Is.True);
    }

    [Test]
    public void Review_cannot_interrupt_an_active_runner()
    {
        var (job, _) = Create();
        _store.Pause(job.Id, false);
        using var runnerLease = _store.AcquireLock();
        var reader = new HistoryStub([]);
        var service = new ImportReviewService(_store, reader, _clock);
        Assert.ThrowsAsync<ImportBusyException>(async () => await service.CheckAsync(job.Id));
        Assert.That(reader.Calls, Is.Zero);
        Assert.That(_store.Get(job.Id).Paused, Is.False);
    }

    [Test]
    public async Task Missing_timestamp_is_not_reported_as_a_failed_LastFm_lookup()
    {
        var (job, entries) = Create(1);
        entries[0].SubmittedTimestamp = null;
        using (_store.AcquireLock()) _store.Save(job, entries, "No timestamp");
        var reader = new HistoryStub([], fail: true);
        var review = await new ImportReviewService(_store, reader, _clock).CheckAsync(job.Id);
        Assert.That(reader.Calls, Is.Zero);
        Assert.That(review.Entries.Single().Match, Is.EqualTo(HistoryMatch.NoTimestamp));
    }

    [Test]
    public async Task Saving_imported_skip_and_retry_decisions_is_atomic_and_preserves_pause_dates_and_cooldowns()
    {
        var (job, entries) = Create();
        var service = new ImportReviewService(_store, new HistoryStub([Play(entries[0], track: "Corrected title")]), _clock);
        var review = await service.CheckAsync(job.Id);
        service.Apply(review, [new(entries[0].Id, ReviewAction.MarkImported), new(entries[1].Id, ReviewAction.Skip), new(entries[2].Id, ReviewAction.Retry)]);
        var saved = _store.Entries(job.Id);
        Assert.That(saved.Select(e => e.Status), Is.EqualTo(new[] { EntryStatus.Accepted, EntryStatus.Skipped, EntryStatus.Pending }));
        Assert.That(saved.Select(e => e.SubmittedTimestamp), Is.EqualTo(entries.Select(e => e.SubmittedTimestamp)));
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        Assert.That(_store.Get(job.Id).NextEligibleRunUtc, Is.EqualTo(job.NextEligibleRunUtc));
        Assert.That(_store.AccountNextRun("alice"), Is.EqualTo(_clock.GetUtcNow().AddHours(24)));
    }

    [Test]
    public async Task Partial_review_does_not_resolve_unselected_entries()
    {
        var (job, entries) = Create();
        var service = new ImportReviewService(_store, new HistoryStub([Play(entries[0])]), _clock);
        var review = await service.CheckAsync(job.Id);
        service.Apply(review, [new(entries[1].Id, ReviewAction.KeepUnresolved)]);
        Assert.That(_store.Entries(job.Id).Select(e => e.Status), Is.EqualTo(new[] { EntryStatus.Accepted, EntryStatus.Uncertain, EntryStatus.Uncertain }));
    }

    [Test]
    public async Task Stale_review_cannot_partially_overwrite_newer_entry_state()
    {
        var (job, entries) = Create();
        var service = new ImportReviewService(_store, new HistoryStub([]), _clock);
        var review = await service.CheckAsync(job.Id);
        _store.Resolve(job.Id, entries[1].Id, EntryStatus.Skipped);
        Assert.Throws<InvalidOperationException>(() => service.Apply(review, [new(entries[0].Id, ReviewAction.MarkImported), new(entries[1].Id, ReviewAction.MarkImported)]));
        Assert.That(_store.Entries(job.Id).Select(e => e.Status), Is.EqualTo(new[] { EntryStatus.Uncertain, EntryStatus.Skipped, EntryStatus.Uncertain }));
    }

    [Test]
    public async Task Review_cannot_be_applied_after_the_job_resumes_or_under_another_account()
    {
        var (job, entries) = Create();
        var service = new ImportReviewService(_store, new HistoryStub([]), _clock);
        var review = await service.CheckAsync(job.Id);
        Assert.Throws<InvalidOperationException>(() => service.Apply(review with { Account = "bob" }, [new(entries[0].Id, ReviewAction.Skip)]));
        _store.Pause(job.Id, false);
        Assert.Throws<InvalidOperationException>(() => service.Apply(review, [new(entries[0].Id, ReviewAction.Skip)]));
    }

    [Test]
    public async Task Matched_or_ambiguous_plays_cannot_be_retried()
    {
        var (job, entries) = Create();
        var service = new ImportReviewService(_store, new HistoryStub([Play(entries[0]), Play(entries[1], track: "Corrected")]), _clock);
        var review = await service.CheckAsync(job.Id);
        foreach (var entry in entries.Take(2))
            Assert.Throws<InvalidOperationException>(() => service.Apply(review, [new(entry.Id, ReviewAction.Retry)]));
    }

    [Test]
    public async Task Expired_timestamp_cannot_be_retried()
    {
        var (job, entries) = Create(1);
        entries[0].SubmittedTimestamp = _clock.GetUtcNow().AddDays(-15);
        using (_store.AcquireLock()) _store.Save(job, entries, "Expired timestamp");
        var service = new ImportReviewService(_store, new HistoryStub([]), _clock);
        var review = await service.CheckAsync(job.Id);
        Assert.Throws<InvalidOperationException>(() => service.Apply(review, [new(entries[0].Id, ReviewAction.Retry)]));
    }

    [TestCase(HistoryMatch.Found, ReviewAction.MarkImported, false)]
    [TestCase(HistoryMatch.Missing, ReviewAction.KeepUnresolved, true)]
    [TestCase(HistoryMatch.Ambiguous, ReviewAction.KeepUnresolved, false)]
    [TestCase(HistoryMatch.NoTimestamp, ReviewAction.KeepUnresolved, true)]
    public void Review_choices_require_explicit_decisions_for_unmatched_entries(HistoryMatch match, ReviewAction defaultAction, bool canRetry)
    {
        var (_, entries) = Create(1);
        var vm = new ImportReviewEntryViewModel(new(entries[0], match, []), _clock.GetUtcNow());
        Assert.That(vm.SelectedChoice.Action, Is.EqualTo(defaultAction));
        Assert.That(vm.Choices.Any(c => c.Action == ReviewAction.Retry), Is.EqualTo(canRetry));
    }

    [TestCase(-15)]
    [TestCase(1)]
    public void Review_explains_why_invalid_timestamps_cannot_be_retried(int offsetDays)
    {
        var (_, entries) = Create(1);
        entries[0].SubmittedTimestamp = _clock.GetUtcNow().AddDays(offsetDays);
        var vm = new ImportReviewEntryViewModel(new(entries[0], HistoryMatch.Missing, []), _clock.GetUtcNow());
        Assert.That(vm.Choices.Any(c => c.Action == ReviewAction.Retry), Is.False);
        Assert.That(vm.RetryUnavailableReason, Does.Contain("outside Last.fm's accepted window"));
    }

    [TestCase(ReviewAction.KeepUnresolved)]
    [TestCase(ReviewAction.MarkImported)]
    [TestCase(ReviewAction.Skip)]
    [TestCase(ReviewAction.Retry)]
    public void Clearing_dropdown_selection_preserves_the_entry_decision(ReviewAction action)
    {
        var (_, entries) = Create(1);
        var vm = new ImportReviewEntryViewModel(new(entries[0], HistoryMatch.Missing, []), _clock.GetUtcNow());
        var choice = vm.Choices.Single(c => c.Action == action);
        vm.SelectedChoice = choice;
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        vm.SelectedChoice = null;
        Assert.That(vm.SelectedChoice, Is.SameAs(choice));
        Assert.That(notifications, Is.Empty);
    }

    [Test]
    public async Task Switching_between_entries_preserves_retry_and_saves_each_entries_own_decision()
    {
        var (job, entries) = Create(2);
        var service = new ImportReviewService(_store, new HistoryStub([Play(entries[1], track: "Corrected title")]), _clock);
        var review = await service.CheckAsync(job.Id);
        var dialog = new ImportReviewDialogViewModel(review, _clock.GetUtcNow());
        var first = dialog.Entries[0];
        var second = dialog.Entries[1];
        var retry = first.Choices.Single(c => c.Action == ReviewAction.Retry);
        var imported = second.Choices.Single(c => c.Action == ReviewAction.MarkImported);
        Assert.That(dialog.SelectedEntry, Is.SameAs(first));
        Assert.That(dialog.CanSave, Is.False);

        first.SelectedChoice = retry;
        dialog.SelectedEntry = second;
        // Model the ComboBox's two-way writes while its DataContext and ItemsSource change.
        first.SelectedChoice = null;
        second.SelectedChoice = retry;
        second.SelectedChoice = null;
        Assert.That(first.SelectedChoice, Is.SameAs(retry));
        Assert.That(second.SelectedChoice.Action, Is.EqualTo(ReviewAction.KeepUnresolved));
        Assert.That(dialog.CanSave, Is.True);
        Assert.That(dialog.Summary, Does.Contain("1 to retry").And.Contain("1 unresolved"));

        second.SelectedChoice = imported;
        dialog.SelectedEntry = first;
        second.SelectedChoice = null;
        // The previous row's imported choice is equal by value to this row's choice,
        // but its instance must not overwrite this row's saved Retry decision.
        first.SelectedChoice = imported;
        first.SelectedChoice = null;
        Assert.That(first.SelectedChoice, Is.SameAs(retry));
        Assert.That(second.SelectedChoice, Is.SameAs(imported));
        Assert.That(dialog.Summary, Does.Contain("1 to mark imported").And.Contain("1 to retry").And.Contain("0 unresolved"));

        service.Apply(review, dialog.CaptureDecisions());
        Assert.That(_store.Entries(job.Id).Select(e => e.Status), Is.EqualTo(new[] { EntryStatus.Pending, EntryStatus.Accepted }));
        Assert.That(_store.Get(job.Id).Paused, Is.True);
        Assert.That(_store.Get(job.Id).NextEligibleRunUtc, Is.EqualTo(job.NextEligibleRunUtc));
    }

    [Test]
    public async Task Popup_decisions_are_captured_for_confirmation_without_changing_the_queue()
    {
        var (job, _) = Create(1);
        var review = await new ImportReviewService(_store, new HistoryStub([]), _clock).CheckAsync(job.Id);
        var dialog = new ImportReviewDialogViewModel(review, _clock.GetUtcNow());
        var row = dialog.Entries.Single();
        var notifications = new List<string?>();
        dialog.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        row.SelectedChoice = row.Choices.Single(c => c.Action == ReviewAction.Retry);
        var captured = dialog.CaptureDecisions();
        row.SelectedChoice = row.Choices.Single(c => c.Action == ReviewAction.Skip);
        Assert.That(captured.Single().Action, Is.EqualTo(ReviewAction.Retry));
        Assert.That(dialog.CaptureDecisions().Single().Action, Is.EqualTo(ReviewAction.Skip));
        Assert.That(notifications, Does.Contain(nameof(ImportReviewDialogViewModel.CanSave)).And.Contain(nameof(ImportReviewDialogViewModel.Summary)));
        // Dismissing the popup or cancelling confirmation does not apply these choices.
        Assert.That(_store.Entries(job.Id).Single().Status, Is.EqualTo(EntryStatus.Uncertain));
        Assert.That(_store.Get(job.Id).Paused, Is.True);
    }

    [Test]
    public async Task History_reader_accepts_a_complete_empty_history_window()
    {
        using var http = new HttpClient(new ResponseHandler([Page(1, 0, 0)]));
        var reader = new LastFmHistoryReader(http, () => new("alice", "unused", "key", "unused"));
        var plays = await reader.GetAsync("alice", DateTimeOffset.FromUnixTimeSeconds(99), DateTimeOffset.FromUnixTimeSeconds(103));
        Assert.That(plays, Is.Empty);
    }

    [Test]
    public async Task History_reader_fetches_every_page_and_ignores_now_playing()
    {
        var handler = new ResponseHandler([
            Page(1, 2, 3, Track(100), Track(101), new XElement("track", new XAttribute("nowplaying", "true"))),
            Page(2, 2, 3, Track(102))
        ]);
        using var http = new HttpClient(handler);
        var reader = new LastFmHistoryReader(http, () => new("alice", "unused", "key", "unused"));
        var plays = await reader.GetAsync("alice", DateTimeOffset.FromUnixTimeSeconds(99), DateTimeOffset.FromUnixTimeSeconds(103));
        Assert.That(plays.Select(p => p.Timestamp.ToUnixTimeSeconds()), Is.EqualTo(new[] { 100L, 101L, 102L }));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests.Select(r => r.Method), Is.All.EqualTo(HttpMethod.Get));
        Assert.That(handler.Requests[1].RequestUri!.Query, Does.Contain("page=2").And.Contain("from=99").And.Contain("to=103"));
    }

    [TestCase("missing-timestamp")]
    [TestCase("incomplete")]
    [TestCase("wrong-account")]
    [TestCase("wrong-page")]
    [TestCase("missing-pagination")]
    [TestCase("api-error")]
    [TestCase("outside-window")]
    public void Unreliable_history_is_rejected_instead_of_reported_as_missing(string failure)
    {
        var body = Page(1, 1, 1, Track(100));
        var recent = body.Element("recenttracks")!;
        switch (failure)
        {
            case "missing-timestamp": recent.Element("track")!.Element("date")!.Remove(); break;
            case "incomplete": recent.SetAttributeValue("total", "2"); break;
            case "wrong-account": recent.SetAttributeValue("user", "bob"); break;
            case "wrong-page": recent.SetAttributeValue("page", "2"); break;
            case "missing-pagination": recent.Attribute("totalPages")!.Remove(); break;
            case "api-error": body = new("lfm", new XAttribute("status", "failed"), new XElement("error", new XAttribute("code", "29"), "Rate limit")); break;
            case "outside-window": recent.Element("track")!.Element("date")!.SetAttributeValue("uts", "999"); break;
        }
        using var http = new HttpClient(new ResponseHandler([body]));
        var reader = new LastFmHistoryReader(http, () => new("alice", "unused", "key", "unused"));
        Assert.ThrowsAsync<InvalidDataException>(async () => await reader.GetAsync("alice", DateTimeOffset.FromUnixTimeSeconds(99), DateTimeOffset.FromUnixTimeSeconds(103)));
    }

    [Test]
    public void Pagination_changes_are_rejected()
    {
        using var http = new HttpClient(new ResponseHandler([Page(1, 2, 2, Track(100)), Page(2, 2, 3, Track(101))]));
        var reader = new LastFmHistoryReader(http, () => new("alice", "unused", "key", "unused"));
        Assert.ThrowsAsync<InvalidDataException>(async () => await reader.GetAsync("alice", DateTimeOffset.FromUnixTimeSeconds(99), DateTimeOffset.FromUnixTimeSeconds(103)));
    }

    [Test]
    public void Account_mismatch_is_rejected_without_network_requests()
    {
        var handler = new ResponseHandler([]);
        using var http = new HttpClient(handler);
        var reader = new LastFmHistoryReader(http, () => new("bob", "unused", "key", "unused"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.GetAsync("alice", _clock.GetUtcNow(), _clock.GetUtcNow()));
        Assert.That(handler.Requests, Is.Empty);
    }

    private static XElement Track(long timestamp) => new("track", new XElement("artist", "Artist"), new XElement("name", "Track"), new XElement("album", "Album"), new XElement("date", new XAttribute("uts", timestamp)));
    private static XElement Page(int page, int pages, int total, params XElement[] tracks) => new("lfm", new XAttribute("status", "ok"), new XElement("recenttracks",
        new XAttribute("user", "alice"), new XAttribute("page", page), new XAttribute("totalPages", pages), new XAttribute("total", total), tracks));

    private sealed class ResponseHandler(IReadOnlyList<XElement> responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Requests.Count - 1].ToString()) });
        }
    }

    private sealed class HistoryStub(IReadOnlyList<HistoryPlay> plays, bool fail = false, Action? beforeReturn = null) : ILastFmHistoryReader
    {
        public int Calls { get; private set; }
        public DateTimeOffset From { get; private set; }
        public DateTimeOffset To { get; private set; }
        public Task<IReadOnlyList<HistoryPlay>> GetAsync(string account, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            Calls++; From = from; To = to;
            if (fail) throw new IOException("History unavailable");
            beforeReturn?.Invoke();
            return Task.FromResult(plays);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    }
}
