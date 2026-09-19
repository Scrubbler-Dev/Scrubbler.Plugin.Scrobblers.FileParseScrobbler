using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scrubbler.Import;

/// <summary>All writers hold AcquireLock for their full operation, including network requests.
/// SQLite transactions make each checkpoint durable; readers may inspect progress while a runner works.</summary>
public sealed class ImportStore
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrubbler", "Imports");
    public string DirectoryPath { get; }

    public ImportStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? DefaultDirectory);
        Directory.CreateDirectory(DirectoryPath);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, account TEXT NOT NULL, source_hash TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS source_account ON jobs(account, source_hash);
            CREATE TABLE IF NOT EXISTS entries(id TEXT PRIMARY KEY, job_id TEXT NOT NULL, position INTEGER NOT NULL, status TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS entries_job ON entries(job_id, position);
            CREATE TABLE IF NOT EXISTS accounts(account TEXT PRIMARY KEY, next_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY, job_id TEXT NOT NULL, utc TEXT NOT NULL, message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attempts(id INTEGER PRIMARY KEY, job_id TEXT NOT NULL, utc TEXT NOT NULL, payload TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "imports.db"), DefaultTimeout = 5 }.ToString());
        db.Open();
        return db;
    }

    public IDisposable AcquireLock()
    {
        try { return new FileStream(Path.Combine(DirectoryPath, "runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new ImportBusyException("Another import operation is running. Try again when it finishes.", ex); }
    }

    public ImportJob Create(string file, ImportProfile profile, string account, int amount = 600, double intervalHours = 24,
        TimestampPolicy policy = TimestampPolicy.Import, int spacingSeconds = 1, bool allowParseErrors = false, bool initiallyPaused = false)
    {
        using var lease = AcquireLock();
        if (List().Any(j => !j.Completed))
            throw new InvalidOperationException("An unfinished import already exists. Finish or delete it before starting another.");
        var job = new ImportJob { Account = account.Trim().ToLowerInvariant(), SourceHash = "", SourceName = Path.GetFileName(file),
            ProfileJson = profile.Serialize(), MaxPerRun = amount, IntervalHours = intervalHours, TimestampPolicy = policy, SpacingSeconds = spacingSeconds, Paused = initiallyPaused };
        job.Validate();
        // Parse the exact bytes saved as the snapshot, even if the external source changes later.
        var temporary = Path.Combine(DirectoryPath, $"{job.Id}.source.tmp");
        try
        {
            File.Copy(file, temporary, false);
            using (var stream = File.OpenRead(temporary)) job.SourceHash = Convert.ToHexString(SHA256.HashData(stream));
            if (List().Any(j => j.Job.Account == job.Account && j.Job.SourceHash == job.SourceHash))
                throw new InvalidOperationException("This source is already queued for this account. Resume the existing job.");
            var parsed = profile.Parse(temporary, policy);
            var errors = parsed.Errors.ToArray();
            if (errors.Length > 0 && !allowParseErrors)
                throw new InvalidOperationException($"{errors.Length} parsing errors. Fix the profile or explicitly allow valid rows only. First error: {errors[0]}");
            var entries = parsed.Scrobbles.Select(t => new ImportEntry { Track = t }).ToArray();
            if (entries.Length == 0) throw new InvalidOperationException("The file contains no importable tracks.");
            File.Move(temporary, Path.Combine(DirectoryPath, job.Id + ".source"));
            File.WriteAllText(Path.Combine(DirectoryPath, job.Id + ".profile.json"), job.ProfileJson);
            if (errors.Length > 0) File.WriteAllLines(Path.Combine(DirectoryPath, job.Id + ".errors.txt"), errors);
            Save(job, entries, $"Created with {entries.Length} entries; {errors.Length} parse errors.");
            return job;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<JobSummary> List()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM jobs ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var jobs = new List<ImportJob>();
        while (reader.Read()) jobs.Add(JsonSerializer.Deserialize<ImportJob>(reader.GetString(0), ImportJson.Options)!);
        reader.Close();
        var summaries = new List<JobSummary>();
        foreach (var job in jobs)
        {
            using var countsCommand = db.CreateCommand();
            countsCommand.CommandText = "SELECT status,COUNT(*) FROM entries WHERE job_id=$id GROUP BY status";
            countsCommand.Parameters.AddWithValue("$id", job.Id);
            using var countsReader = countsCommand.ExecuteReader();
            var counts = new Dictionary<EntryStatus, int>();
            while (countsReader.Read()) counts.Add(Enum.Parse<EntryStatus>(countsReader.GetString(0)), countsReader.GetInt32(1));
            var accountNext = AccountNextRun(job.Account);
            summaries.Add(new(job, counts, accountNext > job.NextEligibleRunUtc ? accountNext : job.NextEligibleRunUtc));
        }
        return summaries;
    }

    public ImportJob Get(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ImportJob>(json, ImportJson.Options)!
            : throw new ArgumentException("Unknown job ID.");
    }

    public IReadOnlyList<ImportEntry> Entries(string jobId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM entries WHERE job_id=$id ORDER BY position, rowid";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        var entries = new List<ImportEntry>();
        while (reader.Read()) entries.Add(JsonSerializer.Deserialize<ImportEntry>(reader.GetString(0), ImportJson.Options)!);
        return entries;
    }

    public DateTimeOffset AccountNextRun(string account)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT next_utc FROM accounts WHERE account=$account";
        command.Parameters.AddWithValue("$account", account.ToLowerInvariant());
        return command.ExecuteScalar() is string value ? DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.MinValue;
    }

    public void Save(ImportJob job, IEnumerable<ImportEntry> changedEntries, string message, DateTimeOffset? accountNextRun = null)
    {
        var changed = changedEntries.ToArray();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        void Execute(string sql, params (string Name, object Value)[] args)
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
            command.ExecuteNonQuery();
        }
        job.LastMessage = message;
        Execute("INSERT INTO jobs VALUES($id,$account,$hash,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload",
            ("$id", job.Id), ("$account", job.Account), ("$hash", job.SourceHash), ("$payload", JsonSerializer.Serialize(job, ImportJson.Options)));
        foreach (var entry in changed)
            Execute("INSERT INTO entries VALUES($id,$job,$position,$status,$payload) ON CONFLICT(id) DO UPDATE SET status=$status,payload=$payload",
                ("$id", entry.Id), ("$job", job.Id), ("$position", entry.Track.SourceIndex), ("$status", entry.Status.ToString()), ("$payload", JsonSerializer.Serialize(entry, ImportJson.Options)));
        if (changed.Any(e => e.Status == EntryStatus.InFlight))
            Execute("INSERT INTO attempts(job_id,utc,payload) VALUES($job,$utc,$payload)", ("$job", job.Id),
                ("$utc", DateTimeOffset.UtcNow.ToString("O")), ("$payload", JsonSerializer.Serialize(changed, ImportJson.Options)));
        if (accountNextRun.HasValue)
            Execute("INSERT INTO accounts VALUES($account,$next) ON CONFLICT(account) DO UPDATE SET next_utc=MAX(next_utc,$next)",
                ("$account", job.Account), ("$next", accountNextRun.Value.ToUniversalTime().ToString("O")));
        Execute("INSERT INTO audit(job_id,utc,message) VALUES($job,$utc,$message)", ("$job", job.Id), ("$utc", DateTimeOffset.UtcNow.ToString("O")), ("$message", message));
        transaction.Commit();
    }

    public void Delete(string id)
    {
        using var lease = AcquireLock();
        _ = Get(id);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var table in new[] { "entries", "attempts", "audit", "jobs" })
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE {(table == "jobs" ? "id" : "job_id")}=$id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        // Retain source snapshots and account cooldowns. Removing a task must
        // neither remove the user's backup nor allow bypassing the daily limit.
    }

    public void Pause(string id, bool paused)
    {
        using var lease = AcquireLock();
        var job = Get(id);
        if (!paused) job.Validate();
        job.Paused = paused;
        Save(job, [], paused ? "Paused by user." : "Resumed by user.");
    }

    // Explicit manual resolution only. Retrying an uncertain request may duplicate a remote play.
    public void Resolve(string id, string entryId, EntryStatus status)
    {
        if (status is not (EntryStatus.Pending or EntryStatus.Accepted or EntryStatus.Skipped)) throw new ArgumentException("Choose Pending, Accepted or Skipped.");
        using var lease = AcquireLock();
        var job = Get(id);
        var entry = Entries(id).Single(e => e.Id == entryId);
        if (entry.Status is not (EntryStatus.Uncertain or EntryStatus.NeedsAttention or EntryStatus.InFlight)) throw new InvalidOperationException("Only unresolved entries can be resolved.");
        entry.Status = status;
        entry.Message = "Manually resolved by user.";
        Save(job, [entry], $"Entry {entry.Id} manually resolved as {status}.");
    }
}

public sealed class ImportBusyException(string message, Exception inner) : IOException(message, inner);
