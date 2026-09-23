# File Parse Scrobbler and scheduled imports

The File Parse plugin and `scrubbler-cli` share the CSV/JSON parsers and a persistent import engine. Background imports currently target Last.fm. Source files are copied into an archive and never edited or deleted. Progress is stored in SQLite.

## Setup through Scrubbler

1. Sign in to Last.fm in Scrubbler's **Accounts** page.
2. Open File Parse's **Scheduled imports** tab and choose your history file. Configure the parser if needed; file/parser settings are shared with **Manual scrobbling**, where you can preview the tracks.
3. Choose the tracks per run and interval, then click **Start import**. Scrubbler uses your signed-in account and enables background importing automatically.
4. View progress, pause, resume or delete under **Your imports**.

Only one unfinished import is allowed at a time, including paused imports. Finish or delete it before starting another. Deleting removes its queue and progress; the source file, backup and account cooldown remain intact. The CLI provides the same action with `imports delete JOB_ID`.

**Date offset (days)** backdates generated scrobble timestamps by 0–10 calendar days (default 0), preserving local time of day. It applies to each run and does not change the schedule or original archived timestamps. The CLI equivalent is `imports create --date-offset-days 10`. A backdated local time that does not exist during a daylight-saving transition is marked for review instead of silently changing its time.

**Scrobble time** defaults to **Use run time**. Uncheck it to choose a local time for the last track on the offset date; earlier tracks are spaced backward by at least one second, including across submission batches and for identical tracks. The CLI equivalent is `--scrobble-time 09:30`. If a batch would contain future timestamps, it waits until they are in the past, retaining the chosen timestamps across restarts. A batch ending near midnight may extend into the previous day.

No executable paths, plugin files or separate CLI installation are needed. The Windows runner, including its .NET runtime, ships inside the plugin. Authentication is saved when signing in, without requiring a restart. A failed setup leaves the new import paused; **Resume** retries setup automatically. Parsing errors block creation unless you explicitly enable importing valid rows only; excluded rows get an error report. The entire file is imported, regardless of manual table selection.

The Windows task checks hourly and at logon, runs under the current user without elevation, and catches up after missed starts. It works with Scrubbler closed while that Windows user is logged in. It cannot run while the computer is off. Pausing imports preserves the queue.

## Build and publish

```powershell
dotnet build -c Release
dotnet test -c Release --no-build
dotnet publish Scrubbler.Cli/Scrubbler.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/cli-win-x64
```

Normal plugin builds automatically publish the Windows runner into `ImportRunner/win-x64` and include it in the plugin package and debug installation. The explicit publish command above is only for standalone CLI use. The host must include native dependency resolution and recursive shadow copying. Runtime discovery uses original installed plugin folders, never temporary shadow copies. After moving an installation, use **Resume** to register its new location. Queue data lives separately under `%APPDATA%\Scrubbler\Imports`.

## Optional CLI for advanced use

Run these commands from the published folder. `--help` lists all options. Options use separate values; boolean options take `true` or `false`.

```powershell
.\scrubbler-cli.exe profile spotify --output spotify-profile.json
.\scrubbler-cli.exe imports preview --file history.json --profile spotify-profile.json
.\scrubbler-cli.exe imports create --file history.json --profile spotify-profile.json --account your-username --max-per-run 600 --min-interval 24h
.\scrubbler-cli.exe schedule install --api-config "C:\Scrubbler\Plugins\Scrubbler.Plugin.Accounts.LastFm\Scrubbler.Plugin.Accounts.LastFm.dll"
.\scrubbler-cli.exe imports status
.\scrubbler-cli.exe imports pause JOB_ID
.\scrubbler-cli.exe imports resume JOB_ID
.\scrubbler-cli.exe schedule remove
```

`imports run-due` runs one pass and exits. It is suitable for other operating systems' schedulers too. Every command supports `--store PATH` for an alternative queue location; use the same location consistently. Different stores do not coordinate quotas. Prefer the default store for all jobs.

The default profile matches Spotify extended-history JSON and excludes plays shorter than 30 seconds. For CSV, set `Format` to `csv`, configure the `Csv` object's zero-based indices, encoding code page and delimiter, and set `HasHeaderRecord` if needed. Optional columns use `-1`. Numeric played durations are milliseconds; duration strings are supported too. Parsing failures stop creation unless `--allow-parse-errors true` is explicitly supplied, in which case errors are archived beside the source.

Authentication reuses the existing Last.fm account plugin's saved session, read-only. The API configuration can be that plugin's release DLL (constants are read without executing it) or its `environment.env`. `LASTFM_API_KEY` and `LASTFM_API_SECRET` environment variables override file configuration. Use credentials for the same application that authenticated the saved session. Secrets are never stored in job records or command arguments. If the saved account changes or authentication fails, the job pauses instead of redirecting submissions.

## Timing, dates and quotas

- Each job permits 1–600 tracks per run (default 600), leaving room for normal listening. The interval is configurable from 24 hours upwards, with an additional one-minute buffer.
- A durable cooldown is reserved before each request and extended after its response. It is shared across jobs for the same account, so a crash, another job or another CLI process cannot bypass it. Jobs are processed in creation order; a large earlier job can delay later jobs for the same account.
- GUI/manual scrobbles and other clients are not counted by this queue. The configured amount is a local import budget, not a claim about Last.fm's current server quota. The server's daily-limit response always stops the run.
- Scheduled jobs always use import mode, generating ordered timestamps shortly before each run. This changes the dates visible on Last.fm; source dates remain in the local archive. `--spacing-seconds` defaults to 1 and must fit the run inside 24 hours. The manual-scrobbling timestamp selector does not affect scheduled jobs.
- Existing scheduled jobs configured with original timestamps are paused without sending or redating their entries. Preserving original timestamps remains available through manual scrobbling.
- The same source content cannot be queued twice for the same account, even under another filename. Distinct repeated listens inside a file remain distinct. Overlapping contents of different exports are not automatically deduplicated.

## Failure recovery and exports

Every request contains at most 50 tracks. Each Last.fm result is checked separately. Only confirmed acceptance marks an entry `Accepted`. Known transient errors and daily-limit responses leave entries pending until the next eligible run. Authentication errors pause the job. Invalid metadata needs attention.

A lost response or a process termination after sending leaves `Uncertain` entries. They block further imports for that account until reviewed. The runner deliberately does not automatically replay or remap uncertain requests: Last.fm provides no client idempotency key, so exactly-once delivery cannot be guaranteed.

Export entries to inspect their IDs, source positions, submission timestamps and outcomes:

```powershell
.\scrubbler-cli.exe imports export JOB_ID --output review.json
.\scrubbler-cli.exe imports export JOB_ID --output accepted.json --status Accepted
.\scrubbler-cli.exe imports export JOB_ID --output remaining.json --status Pending
```

Review uncertain entries against the destination account, then explicitly resolve each one:

```powershell
.\scrubbler-cli.exe imports resolve JOB_ID --entry ENTRY_ID --as Accepted
# Or Skipped to retain the record without sending it.
# Pending authorizes a retry and can duplicate a remotely accepted play.
.\scrubbler-cli.exe imports resolve JOB_ID --entry ENTRY_ID --as Pending
.\scrubbler-cli.exe imports resume JOB_ID
```

Manual retry preserves the original request timestamp. If that timestamp has expired it needs attention again; no automatic redating takes place. Exported JSON is an audit format, not the original vendor format. Source snapshots (`JOB_ID.source`), configuration snapshots, parse errors and the SQLite audit table remain available locally. Back up the whole imports directory while the runner is stopped; do not back up only the main SQLite file while its WAL is active.

Exit codes: `0` success/no work due, `1` error, `2` another operation is running, `3` paused/needs attention, `130` cancelled. Scheduled runs write monthly log files in the imports directory.

The Windows runner sends native notifications when an import completes, authentication or another error pauses it, submissions need review, or a background check fails unexpectedly. Clicking a notification opens that run's monthly log. Notification state is saved across hourly runs to suppress repeated alerts until the condition changes. Manual pauses, cooldowns and routine retries are silent. Windows notification settings and Do Not Disturb control banner visibility; logs remain available if notifications are disabled or delivery fails.

The CLI targets Windows 10 version 1809 or later for native notification support. Import parsing, storage and runner logic remain in the platform-independent `Scrubbler.Import` library.

## Validation

Tests use a fake clock and submission transport; no tests scrobble to a real account. Coverage includes batch boundaries, partial acceptance, interrupted requests, persisted cooldowns, shared account budgets, locks, source preservation, duplicates, expired timestamps, wrong-account protection, and Task Scheduler XML.
