using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

public enum OperationKind { Convert }

/// <summary>
/// A declarative description of one command the radial menu can offer. Adding a format or an operation adds rows,
/// not code in the UI.
/// </summary>
public sealed record Operation(string Id, string Label, OperationKind Kind, ImageFormat Target)
{
    /// <summary>Whether this operation is valid for a file whose content is <paramref name="source"/>.</summary>
    public bool AppliesTo(ImageFormat source) => Kind switch
    {
        OperationKind.Convert => source != Target && ImageFormats.CanDecode(source) && ImageFormats.CanEncode(Target),
        _ => false,
    };
}

/// <summary>The operations this build supports, generated from the format tables (the capability matrix).</summary>
public static class OperationCatalog
{
    public static IReadOnlyList<Operation> All { get; } =
        ImageFormats.Encodable.Select(t => new Operation(ConvertId(t), "To " + ImageFormats.DisplayName(t), OperationKind.Convert, t)).ToArray();

    public static string ConvertId(ImageFormat target) => "convert.to-" + target.ToString().ToLowerInvariant();

    public static Operation Convert(ImageFormat target) => Find(ConvertId(target)) ?? throw new ArgumentOutOfRangeException(nameof(target));

    public static Operation? Find(string id) => All.FirstOrDefault(o => o.Id == id);
}
