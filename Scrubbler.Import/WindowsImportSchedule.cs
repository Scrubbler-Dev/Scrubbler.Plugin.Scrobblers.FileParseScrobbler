using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Scrubbler.Import;

public sealed class RunnerSettings
{
    public string? ApiConfigurationFile { get; set; }
    public string? ExecutablePath { get; set; }
    public static RunnerSettings Load(ImportStore store)
    {
        var file = Path.Combine(store.DirectoryPath, "runner.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<RunnerSettings>(File.ReadAllText(file), ImportJson.Options)! : new();
    }
    public void Save(ImportStore store)
    {
        using var lease = store.AcquireLock();
        var file = Path.Combine(store.DirectoryPath, "runner.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, ImportJson.Options));
        File.Move(file + ".tmp", file, true);
    }
}

public static class WindowsImportSchedule
{
    public static string TaskName(ImportStore store) => "Scrubbler Imports " +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.DirectoryPath.ToUpperInvariant())))[..12];

    public static string CreateXml(string executable, string directory, string user, DateTimeOffset now)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, params object[] values) => new(ns + name, values);
        return new XDocument(new XDeclaration("1.0", "utf-16", null),
            E("Task", new XAttribute("version", "1.2"),
                E("RegistrationInfo", E("Description", "Process due Scrubbler imports. The runner enforces the persisted 24-hour cooldown.")),
                E("Triggers", E("CalendarTrigger",
                    E("Repetition", E("Interval", "PT1H"), E("Duration", "P1D"), E("StopAtDurationEnd", "false")),
                    E("StartBoundary", now.LocalDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                    E("Enabled", "true"), E("ScheduleByDay", E("DaysInterval", "1"))),
                    E("LogonTrigger", E("Enabled", "true"), E("UserId", user))),
                E("Principals", E("Principal", new XAttribute("id", "Author"), E("UserId", user), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
                E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                    E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("ExecutionTimeLimit", "PT30M")),
                E("Actions", new XAttribute("Context", "Author"), E("Exec", E("Command", executable),
                    E("Arguments", "imports run-due --store \"" + directory.TrimEnd(Path.DirectorySeparatorChar) + "\""),
                    E("WorkingDirectory", Path.GetDirectoryName(executable)!))))).ToString();
    }

    public static async Task InstallAsync(ImportStore store, string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Automatic task installation is available on Windows.");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the published scrubbler-cli.exe in its permanent location.");
        var xmlPath = Path.Combine(store.DirectoryPath, "schedule.xml");
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        File.WriteAllText(xmlPath, CreateXml(executable, store.DirectoryPath, user, DateTimeOffset.Now), Encoding.Unicode);
        await RunAsync("/Create", "/TN", TaskName(store), "/XML", xmlPath, "/F");
    }

    public static Task RemoveAsync(ImportStore store) => RunAsync("/Delete", "/TN", TaskName(store), "/F");

    // Request a check now; run-due still enforces account cooldowns and recovery rules.
    public static Task RunNowAsync(ImportStore store) => RunAsync("/Run", "/TN", TaskName(store));

    private static async Task RunAsync(params string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Task Scheduler is required.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Task Scheduler.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"Task Scheduler failed: {await output} {await error}");
    }
}
