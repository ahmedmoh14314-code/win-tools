using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using WinTools.Core.Capabilities;

namespace WinTools.Worker.Text;

/// <summary>
/// One conversion through the user's own Microsoft Word, Excel or PowerPoint, by their automation (ADR-0033 addendum).
/// The program opens the work folder's copy read-only, hidden, with macros forced off
/// (<c>AutomationSecurity = msoAutomationSecurityForceDisable</c>), alerts off, links never updated, and a wrong password
/// given up front, so a protected file fails instead of waiting for someone to type one. Nothing of the user's Office
/// settings is changed: what is set on the program is set back before it is let go.
/// <para>
/// Word and Excel start a new, hidden instance for automation, which is quit afterwards. PowerPoint keeps one instance: when
/// the user already has it open, only the copy this opened is closed, and the program is never quit or stopped. On the
/// time limit, an instance this started is stopped; the user's own PowerPoint is not.
/// </para>
/// </summary>
internal static class MsOffice
{
    /// <summary>A password no document has, so a protected one fails at once instead of asking.</summary>
    private const string WrongPassword = "\u0001wintools-no-password\u0001";

    private const int MsoAutomationSecurityForceDisable = 3;
    private static readonly object Missing = System.Reflection.Missing.Value;

    private static readonly Dictionary<string, (string ProgId, string Process)> Programs = new(StringComparer.Ordinal)
    {
        [ExternalPrograms.Word] = ("Word.Application", "WINWORD"),
        [ExternalPrograms.Excel] = ("Excel.Application", "EXCEL"),
        [ExternalPrograms.PowerPoint] = ("PowerPoint.Application", "POWERPNT"),
    };

    /// <summary>The program that opens an input of that extension (a PDF: Word), or null.</summary>
    public static string? ProgramFor(string extension, bool fromPdf) => fromPdf ? ExternalPrograms.Word : extension.ToLowerInvariant() switch
    {
        ".docx" or ".doc" or ".rtf" or ".odt" or ".txt" => ExternalPrograms.Word,
        ".xlsx" or ".xls" or ".ods" => ExternalPrograms.Excel,
        ".pptx" or ".ppt" or ".odp" => ExternalPrograms.PowerPoint,
        _ => null,
    };

    /// <summary>
    /// Converts <paramref name="input"/> to <paramref name="output"/> (both absolute, in the work folder): to a PDF, or, for
    /// Word and a PDF input, to a .docx. Exit code 0 is done; anything else failed, with the reason in the output text.
    /// </summary>
    public static ProgramRun Convert(string program, string input, string output, bool toPdf, TimeSpan timeout)
    {
        var (progId, processName) = Programs[program];
        var before = Ids(processName);
        var shared = program == ExternalPrograms.PowerPoint && before.Count > 0;
        var started = 0; // the process this run started, when it can be told
        var exit = 1;
        var why = "";
        var thread = new Thread(() =>
        {
            object? app = null;
            try
            {
                var type = Type.GetTypeFromProgID(progId, throwOnError: false) ?? throw new InvalidOperationException("The program is not registered.");
                app = Activator.CreateInstance(type) ?? throw new InvalidOperationException("The program did not start.");
                if (!shared && Ids(processName).Except(before).ToArray() is [var fresh]) Volatile.Write(ref started, fresh);
                if (program == ExternalPrograms.Word) WordRun(app, input, output, toPdf);
                else if (program == ExternalPrograms.Excel) ExcelRun(app, input, output);
                else PowerPointRun(app, input, output, shared);
                exit = 0;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
            {
                why = $"{ex.GetType().Name} 0x{ex.HResult:X8}";
            }
            finally
            {
                if (app is not null)
                {
                    try { if (!shared) ((dynamic)app).Quit(); }
                    catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    try { Marshal.FinalReleaseComObject(app); } catch (ArgumentException) { }
                }
            }
        }) { IsBackground = true, Name = "office-automation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (thread.Join(timeout)) return new ProgramRun(exit, why, TimedOut: false);
        // Too long: stop the instance this started; never the user's own PowerPoint.
        if (Volatile.Read(ref started) is > 0 and var pid)
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return new ProgramRun(-1, "timed out", TimedOut: true);
    }

    private static HashSet<int> Ids(string processName)
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(processName)) using (p) ids.Add(p.Id);
        return ids;
    }

    private static void WordRun(dynamic word, string input, string output, bool toPdf)
    {
        word.Visible = false;
        word.DisplayAlerts = 0; // wdAlertsNone
        word.AutomationSecurity = MsoAutomationSecurityForceDisable;
        // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate, Revert,
        //      WritePasswordDocument, WritePasswordTemplate, Format, Encoding, Visible, OpenAndRepair, DocumentDirection, NoEncodingDialog)
        dynamic doc = word.Documents.Open(input, false, true, false, WrongPassword, WrongPassword, false, WrongPassword, WrongPassword,
            0, Missing, false, false, Missing, true);
        try
        {
            if (toPdf) doc.ExportAsFixedFormat(output, 17); // wdExportFormatPDF
            else doc.SaveAs2(output, 16); // wdFormatDocumentDefault (.docx)
        }
        finally { doc.Close(0); } // wdDoNotSaveChanges
    }

    private static void ExcelRun(dynamic excel, string input, string output)
    {
        excel.Visible = false;
        excel.DisplayAlerts = false;
        excel.EnableEvents = false;
        excel.AutomationSecurity = MsoAutomationSecurityForceDisable;
        // Open(Filename, UpdateLinks, ReadOnly, Format, Password, WriteResPassword, IgnoreReadOnlyRecommended, Origin,
        //      Delimiter, Editable, Notify, Converter, AddToMru)
        dynamic book = excel.Workbooks.Open(input, 0, true, Missing, WrongPassword);
        try { book.ExportAsFixedFormat(0, output); } // xlTypePDF
        finally { book.Close(false); }
    }

    private static void PowerPointRun(dynamic powerPoint, string input, string output, bool shared)
    {
        // The user's own PowerPoint may be this instance: what is changed is set back.
        int alerts = powerPoint.DisplayAlerts;
        int security = powerPoint.AutomationSecurity;
        powerPoint.DisplayAlerts = 1; // ppAlertsNone
        powerPoint.AutomationSecurity = MsoAutomationSecurityForceDisable;
        try
        {
            // Open(FileName, ReadOnly, Untitled, WithWindow); "file::password::" gives the open password up front.
            dynamic deck = powerPoint.Presentations.Open(input + "::" + WrongPassword + "::", -1, 0, 0);
            try { deck.SaveAs(output, 32); } // ppSaveAsPDF
            finally { deck.Close(); }
        }
        finally
        {
            if (shared)
            {
                powerPoint.DisplayAlerts = alerts;
                powerPoint.AutomationSecurity = security;
            }
        }
    }
}
