using WinTools.Core.Localization;

namespace WinTools.Worker.Tests;

/// <summary>Tests that switch the product's language (one per process, UiText) run alone, never beside the others.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageTests;

/// <summary>The wheel in Arabic: every label of every module fits its sector at every DPI, as the English ones do.</summary>
[Collection("Language")]
public sealed class ArabicWheelTests
{
    [Theory]
    [MemberData(nameof(UiSurfaceTests.Dpis), MemberType = typeof(UiSurfaceTests))]
    public void Every_Arabic_label_fits_its_box_without_an_ellipsis(uint dpi)
    {
        try
        {
            UiText.Use(UiText.Arabic);
            UiSurfaceTests.AssertEveryLabelFits(dpi);
        }
        finally { UiText.Use(UiText.English); }
    }
}
