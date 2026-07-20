namespace Rambla;

/// <summary>
/// An immutable snapshot of a <see cref="RamblaState"/>'s lifetime counters.
/// Cheap to read; only collected when metrics are enabled (see
/// <see cref="RamblaOptions.CollectMetrics"/>). Flush <em>duration</em> is
/// deliberately not measured here — time it at the scheduler boundary, which is
/// where the UI cost actually lands.
/// </summary>
public readonly struct StateMetrics : IEquatable<StateMetrics>
{
    /// <param name="mutations">Successful value changes accepted by the state.</param>
    /// <param name="flushes">Flush passes that produced at least one notification.</param>
    /// <param name="notifications"><see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> events raised.</param>
    public StateMetrics(long mutations, long flushes, long notifications)
    {
        Mutations = mutations;
        Flushes = flushes;
        Notifications = notifications;
    }

    /// <summary>Value changes accepted by the state (a no-op write is not counted).</summary>
    public long Mutations { get; }

    /// <summary>Flush passes that delivered at least one notification.</summary>
    public long Flushes { get; }

    /// <summary><c>PropertyChanged</c> events actually raised to observers.</summary>
    public long Notifications { get; }

    /// <summary>Intermediate values that never became a notification because coalescing dropped them.</summary>
    public long CoalescedUpdates => Mutations - Notifications;

    /// <summary>Fraction of mutations avoided by coalescing, in the range 0..1.</summary>
    public double CoalescingRatio => Mutations == 0 ? 0d : (double)CoalescedUpdates / Mutations;

    /// <inheritdoc />
    public bool Equals(StateMetrics other)
        => Mutations == other.Mutations && Flushes == other.Flushes && Notifications == other.Notifications;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StateMetrics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + Mutations.GetHashCode();
            hash = (hash * 31) + Flushes.GetHashCode();
            hash = (hash * 31) + Notifications.GetHashCode();
            return hash;
        }
    }

    /// <summary>Compares two snapshots for equality.</summary>
    public static bool operator ==(StateMetrics left, StateMetrics right) => left.Equals(right);

    /// <summary>Compares two snapshots for inequality.</summary>
    public static bool operator !=(StateMetrics left, StateMetrics right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"mutations={Mutations}, flushes={Flushes}, notifications={Notifications}, coalescing={CoalescingRatio:P2}";
}
