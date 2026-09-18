namespace Scrubbler.Import;

/// <summary>Resolves installed components rather than temporary shadow copies.
/// The runner is shipped inside the File Parse plugin, including its .NET runtime.</summary>
public static class AutomaticImportSetup
{
    public static string PluginRoot => Environment.GetEnvironmentVariable("SCRUBBLER_PLUGIN_MODE") == "Debug"
        && Environment.GetEnvironmentVariable("SOLUTIONDIR") is { Length: > 0 } solution
        ? Path.Combine(solution, "DebugPlugins")
        : Path.Combine(AppContext.BaseDirectory, "Plugins");

    public static RunnerSettings Resolve(string pluginRoot)
    {
        var root = Path.GetFullPath(pluginRoot);
        var executable = FindInstalledFile(root, Path.Combine("ImportRunner", "win-x64", "scrubbler-cli.exe"));
        var accountAssembly = FindInstalledFile(root, "Scrubbler.Plugin.Accounts.LastFm.dll");
        if (executable == null)
            throw new InvalidOperationException("Background imports are missing from this installation. Update Scrubbler's File Parse component and try again.");
        if (accountAssembly == null)
            throw new InvalidOperationException("Install the Last.fm plugin from Plugin Manager before starting an import.");
        return new RunnerSettings { ExecutablePath = executable, ApiConfigurationFile = accountAssembly };
    }

    private static string? FindInstalledFile(string root, string relativePath)
    {
        if (!Directory.Exists(root)) return null;
        // Plugin Manager uses plugin IDs as directory names; developer builds use
        // assembly names. Search installed folders only, never recursive shadow copies.
        return Directory.EnumerateDirectories(root)
            .Where(directory => !Path.GetFileName(directory).StartsWith('.'))
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .Select(directory => Path.Combine(directory, relativePath))
            .FirstOrDefault(File.Exists);
    }

    public static LastFmCredentials CheckAccount(RunnerSettings settings)
    {
        try
        {
            var credentials = LastFmCredentials.Load(settings.ApiConfigurationFile);
            if (string.IsNullOrWhiteSpace(credentials.Account) || string.IsNullOrWhiteSpace(credentials.SessionKey))
                throw new InvalidDataException("No signed-in Last.fm account.");
            return credentials;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Sign in to Last.fm in Scrubbler's Accounts page before starting an import.", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or BadImageFormatException)
        {
            throw new InvalidOperationException("Scrubbler couldn't prepare the Last.fm connection. Update the Last.fm integration and sign in again.", ex);
        }
    }

    public static async Task EnableAsync(ImportStore store, RunnerSettings settings, string expectedAccount)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Background imports currently require Windows.");
        var account = CheckAccount(settings);
        if (!string.Equals(account.Account, expectedAccount, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Sign in as {expectedAccount} before resuming this import.");
        settings.Save(store);
        try { await WindowsImportSchedule.InstallAsync(store, settings.ExecutablePath!); }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Windows couldn't enable background imports. Your import is paused; try Resume again.", ex);
        }
    }
}
