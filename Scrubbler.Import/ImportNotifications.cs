using System.Text.Json;

namespace Scrubbler.Import;

public sealed record ImportNotification(string Title, string Message);

/// <summary>Persists notification state across hourly runner processes.</summary>
public sealed class ImportNotifications(ImportStore store, Action<ImportNotification> show)
{
    public void Report(IReadOnlyList<JobSummary> jobs, bool runFailed = false)
    {
        using var lease = store.AcquireLock();
        var path = Path.Combine(store.DirectoryPath, "notifications.json");
        var previous = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
            : new Dictionary<string, string>();
        var current = runFailed ? new Dictionary<string, string>(previous) : new Dictionary<string, string>();
        foreach (var job in jobs)
        {
            var state = Classify(job);
            if (state == null) continue;
            current[job.Job.Id] = state;
            if (previous.GetValueOrDefault(job.Job.Id) == state) continue;
            var notification = state switch
            {
                "complete" => new ImportNotification("Import complete", $"{job.Job.SourceName}: {job.Counts.GetValueOrDefault(EntryStatus.Accepted):N0} tracks imported. Click to view the run log."),
                "uncertain" => new ImportNotification("Import needs review", $"{job.Job.SourceName}: a submission could not be confirmed. Review it in Scrubbler before retrying. Click for the run log."),
                "rejected" => new ImportNotification("Some tracks need attention", $"{job.Job.SourceName}: some tracks could not be imported. Click for the run log."),
                _ => new ImportNotification("Import paused", $"{job.Job.SourceName}: open Scheduled imports in Scrubbler to resolve the problem and resume. Click for the run log.")
            };
            show(notification);
        }
        if (runFailed)
        {
            current["runner"] = "failed";
            if (!previous.ContainsKey("runner"))
                show(new("Scheduled import failed", "Scrubbler could not finish its background check. Click to view the run log."));
        }
        // Write only after delivery succeeded, allowing failed delivery to retry.
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(current));
        File.Move(path + ".tmp", path, true);
    }

    public static string? Classify(JobSummary summary)
    {
        if (summary.Completed) return "complete";
        if (summary.Counts.GetValueOrDefault(EntryStatus.Uncertain) + summary.Counts.GetValueOrDefault(EntryStatus.InFlight) > 0) return "uncertain";
        if (summary.Counts.GetValueOrDefault(EntryStatus.NeedsAttention) > 0) return "rejected";
        if (summary.Job.Paused && summary.Job.LastMessage != "Paused by user." &&
            summary.Job.LastMessage?.StartsWith("Created with ", StringComparison.Ordinal) != true) return "paused";
        return null;
    }
}
