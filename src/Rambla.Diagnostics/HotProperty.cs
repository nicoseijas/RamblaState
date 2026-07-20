namespace Rambla.Diagnostics;

/// <summary>
/// A property ranked by how much notification traffic it produced in the sample
/// window. Hot properties are the first place to look when the UI thread is busy.
/// </summary>
public readonly struct HotProperty
{
    internal HotProperty(string name, double mutationsPerSecond, double notificationsPerSecond, double coalescingRatio)
    {
        Name = name;
        MutationsPerSecond = mutationsPerSecond;
        NotificationsPerSecond = notificationsPerSecond;
        CoalescingRatio = coalescingRatio;
    }

    /// <summary>The property name.</summary>
    public string Name { get; }

    /// <summary>Accepted mutations per second for this property in the window.</summary>
    public double MutationsPerSecond { get; }

    /// <summary>Notifications actually raised per second for this property in the window.</summary>
    public double NotificationsPerSecond { get; }

    /// <summary>Fraction of this property's mutations that coalescing dropped, 0..1.</summary>
    public double CoalescingRatio { get; }
}
