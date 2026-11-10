using Microsoft.Win32;
using WinTools.Core.Capabilities;

namespace WinTools.Worker.Common;

/// <summary>
/// The worker's own look for the user's LibreOffice (ADR-0033): it never takes a program path from a request. The same
/// reads as the host's (<see cref="LibreOfficeLocator"/>), with the registry read here so no worker loads the host's
/// Windows-integration code.
/// </summary>
public static class InstalledLibreOffice
{
    public static string? Find() => LibreOfficeLocator.Find(ReadDefault, File.Exists,
        [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]);

    /// <summary>The Microsoft Office programs found, by the same reads as the host's.</summary>
    public static IReadOnlySet<string> MicrosoftOffice() => MicrosoftOfficeLocator.Find(ReadDefault, File.Exists);

    /// <summary>Edge's msedge.exe from its App Paths entry, as a local program that exists; null otherwise.</summary>
    public static string? Edge()
    {
        foreach (var root in new[] { RegistryRoot.CurrentUser, RegistryRoot.LocalMachine })
            foreach (var wow32 in new[] { false, true })
                if (ReadDefault(root, wow32, MicrosoftOfficeLocator.AppPathKey("msedge.exe")) is { } value
                    && value.Trim().Trim('"') is var p && LibreOfficeLocator.IsLocalProgram(p, "msedge.exe") && File.Exists(p))
                    return p;
        return null;
    }

    private static string? ReadDefault(RegistryRoot root, bool wow32, string key)
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
