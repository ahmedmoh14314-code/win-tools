namespace WinTools.Core.Tests;

/// <summary>Tests that switch the product's language (one per process, UiText) run alone, never beside the others.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageTests;
