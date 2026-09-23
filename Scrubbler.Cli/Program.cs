using System.Globalization;
using System.Text.Json;
using Scrubbler.Import;

ConsoleHost.AttachToParent();
if (await WindowsImportNotifications.HandleActivationAsync()) return 0;
return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    if (arguments.Length == 0 || arguments.Contains("--help"))
    {
        Console.WriteLine("""
            Scrubbler persistent file imports
            imports create --file PATH --account USER [--profile PATH] [--max-per-run 600]
                           [--min-interval 24h] [--spacing-seconds 1] [--date-offset-days 0] [--scrobble-time HH:mm]
                           [--allow-parse-errors true]
            imports preview --file PATH [--profile PATH]
            imports run-due [--api-config PATH]
            imports status [--json true]
            imports pause JOB_ID
            imports resume JOB_ID
            imports delete JOB_ID
            imports export JOB_ID --output PATH [--status Pending|Accepted|NeedsAttention|Uncertain]
            imports resolve JOB_ID --entry ENTRY_ID --as Pending|Accepted|Skipped
            profile spotify --output PATH
            schedule install [--executable PATH] [--api-config PATH]
            schedule remove
            Every command accepts --store PATH (default: application data/Scrubbler/Imports).
            API config: Last.fm plugin environment.env or plugin DLL; alternatively LASTFM_API_KEY/SECRET.
            Sign in through Scrubbler first. No credentials are accepted on the command line.
            Resolve Pending explicitly authorizes retrying: an uncertain request may already exist on Last.fm.
            Exit codes: 0 success/no work due, 1 error, 2 busy, 3 needs attention, 130 cancelled.
            """);
        return 0;
    }
    ImportStore? store = null;
    try
    {
        if (arguments.Length < 2) throw new ArgumentException("Specify a command. Use --help.");
        var command = arguments[0] + " " + arguments[1];
        var options = new Dictionary<string, string>();
        var positional = new List<string>();
        for (var i = 2; i < arguments.Length; i++)
        {
            if (!arguments[i].StartsWith("--")) { positional.Add(arguments[i]); continue; }
            var key = arguments[i];
            if (++i == arguments.Length || arguments[i].StartsWith("--")) throw new ArgumentException($"Missing value for {key}.");
            if (!options.TryAdd(key, arguments[i])) throw new ArgumentException($"Duplicate option {key}.");
        }
        var allowed = command switch
        {
            "imports create" => "--file --account --profile --max-per-run --min-interval --spacing-seconds --allow-parse-errors --date-offset-days --scrobble-time",
            "imports preview" => "--file --profile",
            "imports run-due" => "--api-config",
            "imports status" => "--json",
            "imports pause" or "imports resume" or "imports delete" or "schedule remove" => "",
            "imports export" => "--output --status",
            "imports resolve" => "--entry --as",
            "profile spotify" => "--output",
            "schedule install" => "--executable --api-config",
            _ => throw new ArgumentException("Unknown command. Use --help.")
        };
        foreach (var key in options.Keys)
            if (!(allowed + " --store").Split(' ').Contains(key)) throw new ArgumentException($"Unknown option {key}.");
        var expectsId = command is "imports pause" or "imports resume" or "imports delete" or "imports export" or "imports resolve";
        if (positional.Count != (expectsId ? 1 : 0)) throw new ArgumentException("Unexpected or missing job ID.");
        string? Option(string key) => options.GetValueOrDefault(key);
        string Required(string key) => Option(key) ?? throw new ArgumentException($"Missing {key}.");
        const TimestampPolicy policy = TimestampPolicy.Import;
        ImportProfile Profile() => Option("--profile") is { } path ? ImportProfile.Read(path) : new ImportProfile();
        // Preview and profile generation never create a queue or write to Last.fm.
        if (command == "profile spotify") { WriteNew(Required("--output"), new ImportProfile().Serialize()); return 0; }
        if (command == "imports preview")
        {
            var parsed = Profile().Parse(Required("--file"), policy);
            Console.WriteLine(JsonSerializer.Serialize(new { Tracks = parsed.Scrobbles.Count(), Errors = parsed.Errors, Preview = parsed.Scrobbles.Take(10) }, ImportJson.Options));
            return parsed.Errors.Any() ? 3 : 0;
        }
        store = new ImportStore(Option("--store"));
        switch (command)
        {
            case "imports create":
                var interval = Option("--min-interval") ?? "24h";
                if (!interval.EndsWith('h')) throw new ArgumentException("Interval must be expressed in hours, e.g. 24h.");
                var job = store.Create(Required("--file"), Profile(), Required("--account"),
                    int.Parse(Option("--max-per-run") ?? "600", CultureInfo.InvariantCulture),
                    double.Parse(interval[..^1], CultureInfo.InvariantCulture), policy,
                    int.Parse(Option("--spacing-seconds") ?? "1", CultureInfo.InvariantCulture),
                    bool.Parse(Option("--allow-parse-errors") ?? "false"),
                    dateOffsetDays: int.Parse(Option("--date-offset-days") ?? "0", CultureInfo.InvariantCulture),
                    scrobbleTimeOfDay: Option("--scrobble-time") is { } time ? TimeSpan.ParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture) : null);
                Console.WriteLine($"Created job {job.Id}. Source preserved. Configure 'schedule install' to run in the background.");
                break;
            case "imports status":
                var jobs = store.List();
                Console.WriteLine(bool.Parse(Option("--json") ?? "false") ? JsonSerializer.Serialize(jobs, ImportJson.Options) : string.Join(Environment.NewLine, jobs));
                break;
            case "imports pause": store.Pause(positional[0], true); break;
            case "imports delete": store.Delete(positional[0]); break;
            case "imports resume": store.Pause(positional[0], false); break;
            case "imports resolve":
                store.Resolve(positional[0], Required("--entry"), Enum.Parse<EntryStatus>(Required("--as"), true));
                break;
            case "imports export":
                _ = store.Get(positional[0]);
                var entries = store.Entries(positional[0]).AsEnumerable();
                if (Option("--status") is { } status) entries = entries.Where(e => e.Status == Enum.Parse<EntryStatus>(status, true));
                WriteNew(Required("--output"), JsonSerializer.Serialize(entries, ImportJson.Options));
                break;
            case "imports run-due":
                using (var cancellation = new CancellationTokenSource())
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                    var config = Option("--api-config") ?? RunnerSettings.Load(store).ApiConfigurationFile;
                    await new ImportRunner(store, new LastFmSubmitter(http, () => LastFmCredentials.Load(config))).RunDueAsync(cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var summaries = store.List();
                    Log(store, string.Join(Environment.NewLine, summaries));
                    Notify(store, summaries);
                    if (summaries.Any(j => j.Counts.GetValueOrDefault(EntryStatus.Uncertain) > 0 || j.Counts.GetValueOrDefault(EntryStatus.NeedsAttention) > 0 || j.Job.Paused)) return 3;
                }
                break;
            case "schedule install":
                var settings = RunnerSettings.Load(store);
                settings.ExecutablePath = Path.GetFullPath(Option("--executable") ?? Environment.ProcessPath!);
                if (Option("--api-config") is { } api) settings.ApiConfigurationFile = Path.GetFullPath(api);
                _ = LastFmCredentials.Load(settings.ApiConfigurationFile);
                settings.Save(store);
                await WindowsImportSchedule.InstallAsync(store, settings.ExecutablePath);
                Console.WriteLine("Scheduled hourly checks and logon checks. Imports remain at least 24 hours apart.");
                break;
            case "schedule remove": await WindowsImportSchedule.RemoveAsync(store); break;
        }
        return 0;
    }
    catch (ImportBusyException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (OperationCanceledException) { return 130; }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        if (store != null)
        {
            try { Log(store, "Error: " + ex.Message); }
            catch (IOException) { /* Keep the original failure and exit code if the log cannot be written. */ }
            if (arguments.Length >= 2 && arguments[0] == "imports" && arguments[1] == "run-due")
                Notify(store, null, true);
        }
        return 1;
    }
}

static void WriteNew(string path, string content)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    using var writer = new StreamWriter(stream);
    writer.Write(content);
}

static void Notify(ImportStore store, IReadOnlyList<JobSummary>? summaries, bool failed = false)
{
    try
    {
        new ImportNotifications(store, notification => WindowsImportNotifications.Show(store, notification))
            .Report(summaries ?? [], failed);
    }
    catch (Exception ex)
    {
        // Notifications must never turn a successful submission into a retry.
        try { Log(store, "Could not display import notification: " + ex.Message); }
        catch (IOException) { }
    }
}

static void Log(ImportStore store, string message)
{
    Console.WriteLine(message);
    File.AppendAllText(Path.Combine(store.DirectoryPath, "runner-" + DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".log"), $"{DateTimeOffset.UtcNow:u} {message}{Environment.NewLine}");
}
