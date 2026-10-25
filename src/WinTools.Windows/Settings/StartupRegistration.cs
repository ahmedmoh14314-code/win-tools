using Microsoft.Win32;

namespace WinTools.Windows.Settings;

public enum StartupState { Off, On, OtherCopy }

/// <summary>
/// "Start with Windows": a value named WinTools under the current user's Run key
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run), holding the quoted path of this copy's host and
/// <c>--background</c>, so signing in shows the tray icon and not the window. Per user, no elevation, no scheduled task or
/// service. Only that one value is ever written or deleted. A value that points to another copy is reported as such and
/// replaced only when the user turns the option on here. A value an older version wrote (the path alone) counts as on.
/// </summary>
public static class StartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "WinTools";

    /// <summary>The command line stored for <paramref name="hostPath"/>: the quoted full path and --background.</summary>
    public static string CommandFor(string hostPath) => $"\"{Path.GetFullPath(hostPath)}\" --background";

    public static StartupState State(string hostPath, string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? StartupState.Off
        : string.Equals(stored.Trim(), CommandFor(hostPath), StringComparison.OrdinalIgnoreCase)
          || string.Equals(stored.Trim(), $"\"{Path.GetFullPath(hostPath)}\"", StringComparison.OrdinalIgnoreCase) ? StartupState.On
        : StartupState.OtherCopy;

    public static StartupState Read(string hostPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return State(hostPath, key?.GetValue(ValueName) as string);
    }

    /// <summary>Turns the option on or off for <paramref name="hostPath"/>. Returns false when the registry refused.</summary>
    public static bool Set(string hostPath, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, CommandFor(hostPath), RegistryValueKind.String);
            else if (State(hostPath, key.GetValue(ValueName) as string) == StartupState.On) key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
