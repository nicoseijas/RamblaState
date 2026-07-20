namespace Rambla;

/// <summary>
/// Marks a backing field so the Rambla source generator emits an observable
/// property that routes writes through <see cref="RamblaState"/> batching and
/// coalescing. The generator strips a leading underscore: a field
/// <c>_bid</c> produces the property <c>Bid</c>.
/// </summary>
/// <remarks>
/// This is a marker only; the observable property is emitted at compile time by
/// the Rambla source generator. You can also declare properties by hand with
/// <see cref="RamblaState.SetField{T}"/> when you need custom setter logic.
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class StateAttribute : Attribute
{
    /// <summary>
    /// When <see langword="true"/>, intermediate values are dropped and only the
    /// latest value is notified on the next flush. This is already the natural
    /// behavior under a coalescing scheduler; the flag documents intent and
    /// reserves per-property control for future non-coalescing paths.
    /// </summary>
    public bool Coalesce { get; set; } = true;
}
