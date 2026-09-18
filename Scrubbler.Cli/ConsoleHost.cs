using System.Runtime.InteropServices;

internal static class ConsoleHost
{
    public static void AttachToParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Preserve redirected stdout/stderr (scripts and automated callers).
        // Never allocate a console: scheduled runs continue using their log file.
        if (IsValid(GetStdHandle(-11)) || IsValid(GetStdHandle(-12))) return;
        _ = AttachConsole(uint.MaxValue);
    }

    private static bool IsValid(nint handle) => handle != 0 && handle != -1;

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
