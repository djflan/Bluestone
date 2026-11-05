namespace Bluestone.Tests.Unit;

/// <summary>Skip reasons for tests of features that are kept in the code but not currently supported.</summary>
internal static class DeferredFeatures
{
    /// <summary>XG-specific behavior is deferred project history (ADR 0041); the code is kept for a later return.</summary>
    public const string Xg = "XG-specific support is deferred (ADR 0041).";
}
