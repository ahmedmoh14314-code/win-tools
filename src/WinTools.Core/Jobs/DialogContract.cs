namespace WinTools.Core.Jobs;

/// <summary>One entry of a pick list: what the dialog shows and the id it answers with.</summary>
public sealed record PickItem(string Id, string Label);

/// <summary>
/// What the host asks the dialogs process to show (V2 PR 1.3): a list to pick one entry from, near a point on screen
/// (physical pixels). One JSON line on the dialog's stdin; it never receives file paths.
/// </summary>
public sealed record PickRequest(string Title, IReadOnlyList<PickItem> Items, int X, int Y, string Kind = DialogKinds.Pick);

/// <summary>Which dialog a request asks for, so the dialogs process reads the right request (V2 PR 2.2).</summary>
public static class DialogKinds
{
    public const string Pick = "pick", Secret = "secret", Rename = "rename", Form = "form";
}

/// <summary>
/// The parameter a command that asks a dialog first gets (V2 PR 7.2): the dialog's answer, as the command reads it (Batch
/// rename: its rule). Only such a command takes it, and only it.
/// </summary>
public static class DialogParameters
{
    public const string Name = "dialog";
}

/// <summary>
/// Batch rename's dialog (V2 PR 7.2): the names to preview, never their folder or paths, in the order dropped; at most
/// <see cref="MaxNames"/>, a bound the host enforces before asking.
/// </summary>
public sealed record RenameRequest(string Title, IReadOnlyList<string> Names, int X, int Y, string Kind = DialogKinds.Rename)
{
    public const int MaxNames = 1000;
}

/// <summary>The rename dialog's answer: the rule (serialized, <see cref="Files.RenameRule"/>), or null when cancelled.</summary>
public sealed record RenameResult(string? Rule);

/// <summary>Only the kind of a request, read first: the rest is read as that kind's request.</summary>
public sealed record DialogEnvelope(string? Kind);

/// <summary>
/// A secret to type (V2 PR 2.2, ADR-0026): a password, entered twice when <paramref name="Confirm"/> (a new one). Like
/// every dialog request it carries no file path: only what the window shows and where (physical pixels).
/// </summary>
public sealed record SecretRequest(string Title, bool Confirm, int X, int Y, string Kind = DialogKinds.Secret);

/// <summary>The secret dialog's answer, one JSON line on its stdout: the secret, or null when the user cancelled.</summary>
public sealed record SecretResult(string? Secret);

/// <summary>The dialog's answer, one JSON line on its stdout: the chosen id, or null when the user cancelled.</summary>
public sealed record PickResult(string? Chosen)
{
    /// <summary>The chosen id if it is one of the request's items; anything else counts as cancelled.</summary>
    public string? ChosenFrom(PickRequest request) => Chosen is { } id && request.Items.Any(i => i.Id == id) ? id : null;
}
