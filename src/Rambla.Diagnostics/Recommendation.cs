namespace Rambla.Diagnostics;

/// <summary>How strongly a <see cref="Recommendation"/> should be surfaced.</summary>
public enum RecommendationSeverity
{
    /// <summary>An observation worth noting; no action required yet.</summary>
    Info,

    /// <summary>A condition likely to cost UI responsiveness under load.</summary>
    Warning,
}

/// <summary>
/// An actionable hint derived from a <see cref="DiagnosticsSnapshot"/> — e.g.
/// "property X generated N notifications/sec; batch or replace the snapshot".
/// </summary>
public readonly struct Recommendation
{
    internal Recommendation(RecommendationSeverity severity, string message)
    {
        Severity = severity;
        Message = message;
    }

    /// <summary>How strongly to surface this recommendation.</summary>
    public RecommendationSeverity Severity { get; }

    /// <summary>The human-readable, actionable message.</summary>
    public string Message { get; }
}
