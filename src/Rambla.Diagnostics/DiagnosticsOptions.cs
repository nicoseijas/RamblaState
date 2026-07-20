namespace Rambla.Diagnostics;

/// <summary>Thresholds that shape a <see cref="DiagnosticsSnapshot"/>.</summary>
public sealed class DiagnosticsOptions
{
    /// <summary>Maximum number of hot properties to include in a snapshot. Default 5.</summary>
    public int MaxHotProperties { get; set; } = 5;

    /// <summary>
    /// Notifications/sec for a single property above which a "batch this" warning
    /// is raised. Default 1,000 — well past what a 60 Hz UI can consume per property.
    /// </summary>
    public double HotNotificationsPerSecond { get; set; } = 1_000d;

    /// <summary>
    /// Fraction of wall time the UI thread may spend flushing before a warning is
    /// raised, 0..1. Default 0.5.
    /// </summary>
    public double UiBudgetWarningFraction { get; set; } = 0.5d;

    /// <summary>The default options.</summary>
    public static DiagnosticsOptions Default { get; } = new();
}
