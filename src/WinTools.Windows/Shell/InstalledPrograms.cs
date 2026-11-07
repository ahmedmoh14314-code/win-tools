using Microsoft.Win32;
using WinTools.Core.Capabilities;

namespace WinTools.Windows.Shell;

/// <summary>
/// The programs the user installed that some commands need (ADR-0033), found with a few registry reads and file checks:
/// the host asks when it starts and when the settings change, the Features page when it opens. Never a disk search.
/// </summary>
public static class InstalledPrograms
{
    /// <summary>The user's soffice.exe, or null.</summary>
    public static string? LibreOffice() => LibreOfficeLocator.Find(ReadDefault, File.Exists,
        [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]);

    /// <summary>What the resolver takes: the names of the programs found.</summary>
    public static IReadOnlySet<string> Found() => ExternalPrograms.Set(LibreOffice());

    /// <summary>A key's default value, read only; null when there is no such key or it cannot be read.</summary>
    public static string? ReadDefault(RegistryRoot root, bool wow32, string key)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(root == RegistryRoot.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
                wow32 ? RegistryView.Registry32 : RegistryView.Registry64);
            using var sub = hive.OpenSubKey(key, writable: false);
            return sub?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
