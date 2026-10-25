namespace WinTools.Core.Jobs;

/// <summary>One entry of a pick list: what the dialog shows and the id it answers with.</summary>
public sealed record PickItem(string Id, string Label);

/// <summary>
/// What the host asks the dialogs process to show (V2 PR 1.3): a list to pick one entry from, near a point on screen
/// (physical pixels). One JSON line on the dialog's stdin; it never receives file paths.
/// </summary>
public sealed record PickRequest(string Title, IReadOnlyList<PickItem> Items, int X, int Y);

/// <summary>The dialog's answer, one JSON line on its stdout: the chosen id, or null when the user cancelled.</summary>
public sealed record PickResult(string? Chosen)
{
    /// <summary>The chosen id if it is one of the request's items; anything else counts as cancelled.</summary>
    public string? ChosenFrom(PickRequest request) => Chosen is { } id && request.Items.Any(i => i.Id == id) ? id : null;
}
