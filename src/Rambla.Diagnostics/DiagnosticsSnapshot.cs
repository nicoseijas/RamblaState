using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rambla.Diagnostics;

/// <summary>
/// An immutable reading of a <see cref="RamblaState"/>'s diagnostics over a sample
/// window: the rates coalescing collapsed, where the UI thread spent its time, the
/// hottest properties, and actionable recommendations. Its <see cref="ToString"/>
/// renders the familiar console report.
/// </summary>
public readonly struct DiagnosticsSnapshot
{
    internal DiagnosticsSnapshot(
        string stateName,
        TimeSpan window,
        long totalMutations,
        long totalNotifications,
        double mutationsPerSecond,
        double notificationsPerSecond,
        double flushesPerSecond,
        double coalescingRatio,
        TimeSpan longestFlush,
        double uiThreadBudget,
        bool hasDispatcherMetrics,
        double dispatcherHopsPerSecond,
        TimeSpan averageDispatcherLatency,
        TimeSpan peakDispatcherLatency,
        IReadOnlyList<HotProperty> hotProperties,
        IReadOnlyList<Recommendation> recommendations)
    {
        StateName = stateName;
        Window = window;
        TotalMutations = totalMutations;
        TotalNotifications = totalNotifications;
        MutationsPerSecond = mutationsPerSecond;
        NotificationsPerSecond = notificationsPerSecond;
        FlushesPerSecond = flushesPerSecond;
        CoalescingRatio = coalescingRatio;
        LongestFlush = longestFlush;
        UiThreadBudget = uiThreadBudget;
        HasDispatcherMetrics = hasDispatcherMetrics;
        DispatcherHopsPerSecond = dispatcherHopsPerSecond;
        AverageDispatcherLatency = averageDispatcherLatency;
        PeakDispatcherLatency = peakDispatcherLatency;
        HotProperties = hotProperties;
        Recommendations = recommendations;
    }

    /// <summary>The observed state's type name.</summary>
    public string StateName { get; }

    /// <summary>Wall-clock time covered by this snapshot's rates.</summary>
    public TimeSpan Window { get; }

    /// <summary>Cumulative mutations observed since the session was attached.</summary>
    public long TotalMutations { get; }

    /// <summary>
    /// Cumulative <c>PropertyChanged</c> notifications actually raised since the
    /// session was attached. Zero while the state has no subscriber — flushes
    /// still run and coalesce, but no UI notification occurs.
    /// </summary>
    public long TotalNotifications { get; }

    /// <summary>Accepted mutations per second in the window.</summary>
    public double MutationsPerSecond { get; }

    /// <summary>Notifications actually raised per second in the window (zero with nothing bound).</summary>
    public double NotificationsPerSecond { get; }

    /// <summary>Coalesced flushes per second in the window.</summary>
    public double FlushesPerSecond { get; }

    /// <summary>
    /// Fraction of mutations coalescing dropped before the flush, 0..1. An engine
    /// property, measured against flushed properties rather than raised
    /// notifications, so it stays meaningful with no subscriber attached.
    /// </summary>
    public double CoalescingRatio { get; }

    /// <summary>Longest single UI flush observed since the session attached (not per window).</summary>
    public TimeSpan LongestFlush { get; }

    /// <summary>Fraction of wall time the UI thread spent flushing, 0..1.</summary>
    public double UiThreadBudget { get; }

    /// <summary>Whether dispatcher metrics are present (a <see cref="DiagnosticsScheduler"/> was supplied).</summary>
    public bool HasDispatcherMetrics { get; }

    /// <summary>Dispatcher hops per second in the window (0 without a <see cref="DiagnosticsScheduler"/>).</summary>
    public double DispatcherHopsPerSecond { get; }

    /// <summary>Average post-to-run dispatcher latency in the window.</summary>
    public TimeSpan AverageDispatcherLatency { get; }

    /// <summary>Peak post-to-run dispatcher latency observed since attach.</summary>
    public TimeSpan PeakDispatcherLatency { get; }

    /// <summary>The hottest properties by notification rate, most-notified first.</summary>
    public IReadOnlyList<HotProperty> HotProperties { get; }

    /// <summary>Actionable recommendations derived from this snapshot.</summary>
    public IReadOnlyList<Recommendation> Recommendations { get; }

    /// <summary>Renders the console diagnostics report.</summary>
    public override string ToString()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(StateName);
        sb.AppendLine(Row("Incoming state mutations", MutationsPerSecond.ToString("N0", c) + " / sec"));
        sb.AppendLine(Row("UI notifications", NotificationsPerSecond.ToString("N0", c) + " / sec"));
        sb.AppendLine(Row("Coalescing", CoalescingRatio.ToString("P2", c)));
        if (HasDispatcherMetrics)
        {
            sb.AppendLine(Row("Dispatcher hops", DispatcherHopsPerSecond.ToString("N0", c) + " / sec"));
            sb.AppendLine(Row("Dispatcher latency", Ms(AverageDispatcherLatency, c)));
        }

        sb.AppendLine(Row("Longest UI flush", Ms(LongestFlush, c)));
        sb.AppendLine(Row("UI thread budget", UiThreadBudget.ToString("P0", c)));

        foreach (Recommendation r in Recommendations)
        {
            string mark = r.Severity == RecommendationSeverity.Warning ? "  ⚠ " : "  • ";
            sb.AppendLine();
            sb.AppendLine(mark + r.Message);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Row(string label, string value)
        => "  " + label.PadRight(26) + ": " + value.PadLeft(12);

    private static string Ms(TimeSpan span, CultureInfo c)
        => span.TotalMilliseconds.ToString("0.0", c) + " ms";
}
