using System.Diagnostics;
using Microsoft.Toolkit.Uwp.Notifications;
using Scrubbler.Import;

internal static class WindowsImportNotifications
{
    private static readonly TaskCompletionSource ActivationHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool _registered;

    public static async Task<bool> HandleActivationAsync()
    {
        // Registration is lazy for ordinary CLI commands.
        if (!ToastNotificationManagerCompat.WasCurrentProcessToastActivated()) return false;
        Register();
        await ActivationHandled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return true;
    }

    private static void Register()
    {
        if (_registered) return;
        ToastNotificationManagerCompat.OnActivated += activation =>
        {
            try
            {
                var arguments = ToastArguments.Parse(activation.Argument);
                var log = Path.GetFullPath(arguments["log"]);
                if (Path.GetFileName(log).StartsWith("runner-", StringComparison.Ordinal) &&
                    Path.GetExtension(log) == ".log" && File.Exists(log))
                    Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
            }
            catch (Exception ex) { Console.Error.WriteLine("Could not open the import log: " + ex.Message); }
            finally { ActivationHandled.TrySetResult(); }
        };
        _registered = true;
    }

    public static void Show(ImportStore store, ImportNotification notification)
    {
        Register();
        new ToastContentBuilder()
            .AddArgument("log", Path.Combine(store.DirectoryPath, "runner-" + DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) + ".log"))
            .AddText(notification.Title)
            .AddText(notification.Message)
            .Show();
    }
}
