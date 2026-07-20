using Rambla.Diagnostics;

namespace Rambla.Tests.Diagnostics;

/// <summary>A hand-advanced clock so diagnostics rate windows are deterministic.</summary>
internal sealed class FakeClock : IClock
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now += by;
}
