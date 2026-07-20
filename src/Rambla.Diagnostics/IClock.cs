using System;

namespace Rambla.Diagnostics;

/// <summary>
/// Abstracts the current time so diagnostics rate calculations are testable. The
/// production implementation is <see cref="SystemClock"/>; tests inject a fake.
/// </summary>
public interface IClock
{
    /// <summary>The current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock, backed by <see cref="DateTimeOffset.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>A shared instance; the clock is stateless.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
