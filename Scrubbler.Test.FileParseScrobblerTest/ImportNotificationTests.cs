using Microsoft.Data.Sqlite;
using Scrubbler.Import;

namespace Scrubbler.Test.FileParseScrobblerTest;

[TestFixture]
public sealed class ImportNotificationTests
{
    private string _directory = null!;
    private ImportStore _store = null!;
    private readonly List<ImportNotification> _sent = [];

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "scrubbler-notification-tests-" + Guid.NewGuid().ToString("N"));
        _store = new ImportStore(_directory);
        _sent.Clear();
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private static JobSummary Job(EntryStatus status, bool paused = false, string? message = null) =>
        new(new ImportJob { Id = "test", Account = "test", SourceHash = "hash", SourceName = "history.csv",
            ProfileJson = "{}", Paused = paused, LastMessage = message },
            new Dictionary<EntryStatus, int> { [status] = 10 });

    [TestCase(EntryStatus.Accepted, "Import complete")]
    [TestCase(EntryStatus.Uncertain, "Import needs review")]
    [TestCase(EntryStatus.NeedsAttention, "Some tracks need attention")]
    public void Notifications_survive_process_restart_without_repeating(EntryStatus status, string title)
    {
        new ImportNotifications(_store, _sent.Add).Report([Job(status)]);
        new ImportNotifications(new ImportStore(_directory), _sent.Add).Report([Job(status)]);
        Assert.That(_sent, Has.Count.EqualTo(1));
        Assert.That(_sent[0].Title, Is.EqualTo(title));
    }

    [Test]
    public void Authentication_pause_alerts_but_manual_pause_and_cooldown_are_quiet()
    {
        var reporter = new ImportNotifications(_store, _sent.Add);
        reporter.Report([Job(EntryStatus.Pending, true, "Created with 10 entries; 0 parse errors.")]);
        reporter.Report([Job(EntryStatus.Pending, true, "Paused by user.")]);
        reporter.Report([Job(EntryStatus.Pending) with { EffectiveNextRunUtc = DateTimeOffset.UtcNow.AddDays(1) }]);
        Assert.That(_sent, Is.Empty);
        reporter.Report([Job(EntryStatus.Pending, true, "AuthenticationRequired: 10")]);
        Assert.That(_sent.Single().Title, Is.EqualTo("Import paused"));
    }

    [Test]
    public void Recovery_allows_a_new_alert_for_the_same_problem()
    {
        var reporter = new ImportNotifications(_store, _sent.Add);
        reporter.Report([Job(EntryStatus.Uncertain)]);
        reporter.Report([Job(EntryStatus.Pending)]);
        reporter.Report([Job(EntryStatus.Uncertain)]);
        Assert.That(_sent, Has.Count.EqualTo(2));
    }

    [Test]
    public void Failed_delivery_is_retried()
    {
        Assert.Throws<IOException>(() => new ImportNotifications(_store, _ => throw new IOException("Unavailable"))
            .Report([Job(EntryStatus.Accepted)]));
        new ImportNotifications(_store, _sent.Add).Report([Job(EntryStatus.Accepted)]);
        Assert.That(_sent, Has.Count.EqualTo(1));
    }

    [Test]
    public void Unexpected_failure_is_reported_once_without_forgetting_completed_notifications()
    {
        var reporter = new ImportNotifications(_store, _sent.Add);
        reporter.Report([Job(EntryStatus.Accepted)]);
        reporter.Report([], true);
        reporter.Report([], true);
        reporter.Report([Job(EntryStatus.Accepted)]);
        Assert.That(_sent, Has.Count.EqualTo(2));
        Assert.That(_sent[1].Title, Is.EqualTo("Scheduled import failed"));
    }
}
