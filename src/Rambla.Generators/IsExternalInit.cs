// Polyfill: init-only setters and records require this type, which does not
// exist in netstandard2.0. Roslyn analyzers/generators must target ns2.0.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
