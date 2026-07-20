using Microsoft.CodeAnalysis;

namespace Rambla.Generators;

/// <summary>
/// Emits observable properties for fields annotated with <c>[State]</c>, wiring
/// their setters through <c>RamblaState.SetField</c> so writes participate in
/// batching and coalescing.
/// </summary>
/// <remarks>
/// Skeleton for Phase 1. The full pipeline (locate <c>[State]</c> fields, strip
/// the leading underscore, generate the property and dependency notifications)
/// lands next; until then, declare properties by hand with
/// <c>SetField</c>. Registered so the analyzer wiring and packaging are exercised
/// by the build from day one.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class StateGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // TODO(Phase 1): register a syntax provider for [State] fields and emit
        // partial-class properties routed through RamblaState.SetField.
    }
}
