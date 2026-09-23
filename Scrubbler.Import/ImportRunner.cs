namespace Scrubbler.Import;

public sealed class ImportRunner(ImportStore store, IScrobbleSubmitter submitter, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task RunDueAsync(CancellationToken cancellationToken = default)
    {
        using var lease = store.AcquireLock();
        foreach (var summary in store.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = summary.Job;
            var entries = store.Entries(job.Id);
            var interrupted = entries.Where(e => e.Status == EntryStatus.InFlight).ToArray();
            if (interrupted.Length > 0)
            {
                foreach (var entry in interrupted)
                {
                    entry.Status = EntryStatus.Uncertain;
                    entry.Message = "Runner stopped before recording the response. Review Last.fm before resolving.";
                }
                job.Paused = true;
                store.Save(job, interrupted, "Interrupted request requires review.");
            }
            if (job.TimestampPolicy != TimestampPolicy.Import)
            {
                if (!job.Paused)
                {
                    job.Paused = true;
                    store.Save(job, [], "Scheduled imports only support import mode. Use manual scrobbling to preserve original timestamps.");
                }
                continue;
            }
            if (job.Paused || job.NextEligibleRunUtc > _clock.GetUtcNow() || store.AccountNextRun(job.Account) > _clock.GetUtcNow()) continue;
            // An unresolved request blocks all jobs for its destination account.
            if (store.List().Where(j => j.Job.Account == job.Account)
                .Any(j => j.Counts.GetValueOrDefault(EntryStatus.Uncertain) + j.Counts.GetValueOrDefault(EntryStatus.InFlight) > 0)) continue;

            job.Validate();
            var selected = entries.Where(e => e.Status == EntryStatus.Pending).Take(job.MaxPerRun).ToArray();
            var now = _clock.GetUtcNow();
            var usedTimestamps = entries.Where(e => e.SubmittedTimestamp.HasValue)
                .Select(e => e.SubmittedTimestamp!.Value.ToUnixTimeSeconds()).ToHashSet();
            for (var i = 0; i < selected.Length; i++)
            {
                var entry = selected[i];
                if (entry.SubmittedTimestamp == null)
                {
                    var timestamp = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() - 1 - (long)(selected.Length - 1 - i) * job.SpacingSeconds);
                    try
                    {
                        var candidate = job.ScrobbleTimeOfDay is { } time
                            ? ImportTimestamp.AtTime(now, job.DateOffsetDays, time, TimeZoneInfo.Local).AddSeconds(-(long)(selected.Length - 1 - i) * job.SpacingSeconds)
                            : ImportTimestamp.Backdate(timestamp, job.DateOffsetDays, TimeZoneInfo.Local);
                        while (!usedTimestamps.Add(candidate.ToUnixTimeSeconds()))
                            candidate = candidate.AddSeconds(-job.SpacingSeconds);
                        entry.SubmittedTimestamp = candidate;
                    }
                    catch (ArgumentException ex)
                    {
                        entry.Status = EntryStatus.NeedsAttention;
                        entry.Message = ex.Message;
                        continue;
                    }
                }
                if (entry.SubmittedTimestamp < now.AddDays(-14) || (job.ScrobbleTimeOfDay == null && entry.SubmittedTimestamp > now))
                {
                    entry.Status = EntryStatus.NeedsAttention;
                    entry.Message = "Submission timestamp is outside Last.fm's accepted window. Review timestamp policy.";
                }
            }
            var invalid = selected.Where(e => e.Status == EntryStatus.NeedsAttention).ToArray();
            if (invalid.Length > 0) store.Save(job, invalid, $"{invalid.Length} entries need timestamp review.");
            var future = selected.Where(e => e.Status == EntryStatus.Pending && e.SubmittedTimestamp > now).ToArray();
            if (future.Length > 0)
            {
                job.NextEligibleRunUtc = future.Max(e => e.SubmittedTimestamp!.Value).AddSeconds(1);
                // Preserve this run's chosen date across waits, including midnight.
                store.Save(job, selected, "Waiting until the selected scrobble timestamps are in the past.");
                continue;
            }
            foreach (var batch in selected.Where(e => e.Status == EntryStatus.Pending).Chunk(50))
            {
                cancellationToken.ThrowIfCancellationRequested();
                now = _clock.GetUtcNow();
                var previousAccountCooldown = store.AccountNextRun(job.Account);
                var previousJobCooldown = job.NextEligibleRunUtc;
                foreach (var entry in batch) { entry.Status = EntryStatus.InFlight; entry.AttemptedAtUtc = now; }
                job.NextEligibleRunUtc = now.AddHours(job.IntervalHours).AddMinutes(1);
                // Durable intent and cooldown precede any network I/O.
                store.Save(job, batch, $"Submitting {batch.Length} entries.", job.NextEligibleRunUtc);

                IReadOnlyList<SubmissionResult> results;
                try
                {
                    results = await submitter.SubmitAsync(job.Account, batch, cancellationToken);
                    if (results.Count != batch.Length) throw new InvalidDataException($"Response count does not match request: expected {batch.Length}, received {results.Count}.");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Xml.XmlException or FormatException or OverflowException)
                {
                    var reason = ex is OperationCanceledException
                        ? cancellationToken.IsCancellationRequested ? "Submission cancelled before a reliable response was recorded." : "Submission timed out before a reliable response was recorded."
                        : $"{ex.GetType().Name}: {ex.Message}";
                    results = batch.Select(_ => new SubmissionResult(SubmissionOutcome.Uncertain, reason + " Review Last.fm before retrying.")).ToArray();
                }

                for (var i = 0; i < batch.Length; i++)
                {
                    var entry = batch[i];
                    var result = results[i];
                    entry.Message = result.Message;
                    entry.Status = result.Outcome switch
                    {
                        SubmissionOutcome.Accepted => EntryStatus.Accepted,
                        SubmissionOutcome.Retry or SubmissionOutcome.DailyLimit or SubmissionOutcome.AuthenticationRequired => EntryStatus.Pending,
                        SubmissionOutcome.Rejected => EntryStatus.NeedsAttention,
                        _ => EntryStatus.Uncertain
                    };
                    // Only regenerate timestamps when rejection is confirmed, never for an uncertain request.
                    if (entry.Status == EntryStatus.Pending) entry.SubmittedTimestamp = null;
                }
                if (results.Any(r => r.Outcome is SubmissionOutcome.Uncertain or SubmissionOutcome.AuthenticationRequired)) job.Paused = true;
                var keepCooldown = results.Any(r => r.Outcome is SubmissionOutcome.Accepted or SubmissionOutcome.Uncertain or SubmissionOutcome.DailyLimit);
                job.NextEligibleRunUtc = keepCooldown
                    ? _clock.GetUtcNow().AddHours(job.IntervalHours).AddMinutes(1)
                    : results.Any(r => r.Outcome == SubmissionOutcome.Retry)
                        ? _clock.GetUtcNow().AddHours(1) : previousJobCooldown;
                // Definitive rejection consumed no allowance. Restore only this
                // batch's reservation; earlier accepted batches retain their cooldown.
                var outcomes = string.Join(", ", results.GroupBy(r => r.Outcome).Select(g => $"{g.Key}: {g.Count()}"));
                var reasons = results.Where(r => r.Outcome != SubmissionOutcome.Accepted && !string.IsNullOrWhiteSpace(r.Message))
                    .Select(r => $"{r.Outcome}: {r.Message}".Replace('\r', ' ').Replace('\n', ' ')).Distinct();
                var details = string.Join("; ", reasons);
                store.Save(job, batch, $"Batch size: {batch.Length} | {outcomes}" + (details.Length > 0 ? $" | Details: {details}" : ""),
                    keepCooldown ? job.NextEligibleRunUtc : previousAccountCooldown, restoreAccountCooldown: !keepCooldown);
                if (results.Any(r => r.Outcome is SubmissionOutcome.Uncertain or SubmissionOutcome.AuthenticationRequired or SubmissionOutcome.DailyLimit or SubmissionOutcome.Retry)) break;
            }
        }
    }
}
