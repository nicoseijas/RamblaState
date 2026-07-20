using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rambla.Generators;

/// <summary>
/// Emits observable properties for fields annotated with <c>[State]</c>, routing
/// their setters through <c>RamblaState.SetField</c> so writes participate in
/// batching and coalescing. The generator strips a leading underscore:
/// <c>_bid</c> produces <c>Bid</c>.
/// </summary>
/// <remarks>
/// V1 is deliberately minimal: a field yields exactly one property. No
/// <c>DependsOn</c>, commands, validation, custom names or custom equality. It is
/// fully compile-time (no reflection, no runtime registration) and reports
/// diagnostics RMB001–RMB005 for misuse.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class StateGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "Rambla.StateAttribute";
    private const string BaseTypeMetadataName = "Rambla.RamblaState";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly DiagnosticDescriptor NotPartial = new(
        "RMB001", "Containing type must be partial",
        "'{0}' must be partial for Rambla to generate the '{1}' property", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotInstanceField = new(
        "RMB002", "[State] requires an instance field",
        "[State] can only be applied to instance fields; '{0}' is static", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NameCollision = new(
        "RMB003", "Generated property name collides",
        "The generated property name '{0}' collides with an existing member or another [State] field", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ReadonlyOrConst = new(
        "RMB004", "[State] does not support readonly or const fields",
        "[State] cannot be applied to the readonly or const field '{0}'", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotRamblaState = new(
        "RMB005", "Containing type must derive from RamblaState",
        "'{0}' must derive from Rambla.RamblaState to use [State]", "Rambla",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                predicate: static (_, _) => true,
                transform: static (ctx, _) => Transform(ctx))
            .Where(static r => r is not null)
            .Select(static (r, _) => r!)
            .Collect();

        context.RegisterSourceOutput(results, static (spc, items) => Emit(spc, items));
    }

    private static FieldResult? Transform(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IFieldSymbol field)
        {
            return null;
        }

        Location location = field.Locations.FirstOrDefault() ?? Location.None;
        INamedTypeSymbol containingType = field.ContainingType;
        string typeName = containingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (field.IsStatic)
        {
            return FieldResult.Error(Diagnostic.Create(NotInstanceField, location, field.Name));
        }

        if (field.IsReadOnly || field.IsConst)
        {
            return FieldResult.Error(Diagnostic.Create(ReadonlyOrConst, location, field.Name));
        }

        string propertyName = DerivePropertyName(field.Name);
        if (propertyName.Length == 0 || propertyName == field.Name || HasMemberNamed(containingType, propertyName))
        {
            return FieldResult.Error(Diagnostic.Create(NameCollision, location, propertyName));
        }

        if (!DerivesFromRamblaState(containingType))
        {
            return FieldResult.Error(Diagnostic.Create(NotRamblaState, location, typeName));
        }

        foreach (INamedTypeSymbol type in SelfAndContainingTypes(containingType))
        {
            if (!IsPartial(type))
            {
                return FieldResult.Error(Diagnostic.Create(NotPartial, location, typeName, propertyName));
            }
        }

        var nesting = SelfAndContainingTypes(containingType)
            .Reverse()
            .Select(static t => new TypeLayer(Keyword(t), NameWithTypeParameters(t)))
            .ToImmutableArray();

        string? ns = containingType.ContainingNamespace is { IsGlobalNamespace: false } n
            ? n.ToDisplayString()
            : null;

        var model = new FieldModel(
            TypeKey: containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace: ns,
            Nesting: nesting,
            PropertyType: field.Type.ToDisplayString(TypeFormat),
            PropertyName: propertyName,
            FieldName: field.Name,
            Location: location);

        return FieldResult.Ok(model);
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<FieldResult> items)
    {
        foreach (FieldResult result in items)
        {
            if (result.Diagnostic is { } diagnostic)
            {
                spc.ReportDiagnostic(diagnostic);
            }
        }

        var groups = items
            .Where(static r => r.Model is not null)
            .Select(static r => r.Model!)
            .GroupBy(static m => m.TypeKey);

        foreach (var group in groups)
        {
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            var emit = new System.Collections.Generic.List<FieldModel>();
            foreach (FieldModel model in group)
            {
                if (seen.Add(model.PropertyName))
                {
                    emit.Add(model);
                }
                else
                {
                    spc.ReportDiagnostic(Diagnostic.Create(NameCollision, model.Location, model.PropertyName));
                }
            }

            if (emit.Count == 0)
            {
                continue;
            }

            FieldModel first = emit[0];
            spc.AddSource(HintName(first.TypeKey), BuildSource(first, emit));
        }
    }

    private static string BuildSource(FieldModel type, System.Collections.Generic.List<FieldModel> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> — Rambla [State] source generator. Do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        int indent = 0;
        if (type.Namespace is not null)
        {
            sb.Append("namespace ").Append(type.Namespace).AppendLine();
            sb.AppendLine("{");
            indent = 1;
        }

        foreach (TypeLayer layer in type.Nesting)
        {
            Pad(sb, indent).Append("partial ").Append(layer.Keyword).Append(' ').Append(layer.Name).AppendLine();
            Pad(sb, indent).AppendLine("{");
            indent++;
        }

        for (int i = 0; i < fields.Count; i++)
        {
            FieldModel f = fields[i];
            Pad(sb, indent).Append("public ").Append(f.PropertyType).Append(' ').Append(f.PropertyName).AppendLine();
            Pad(sb, indent).AppendLine("{");
            Pad(sb, indent + 1).Append("get => ").Append(f.FieldName).AppendLine(";");
            Pad(sb, indent + 1).Append("set => SetField(ref ").Append(f.FieldName).AppendLine(", value);");
            Pad(sb, indent).AppendLine("}");
            if (i < fields.Count - 1)
            {
                sb.AppendLine();
            }
        }

        for (int i = 0; i < type.Nesting.Length; i++)
        {
            indent--;
            Pad(sb, indent).AppendLine("}");
        }

        if (type.Namespace is not null)
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static StringBuilder Pad(StringBuilder sb, int indent) => sb.Append(' ', indent * 4);

    private static string DerivePropertyName(string fieldName)
    {
        string raw = fieldName.TrimStart('_');
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
    }

    private static bool HasMemberNamed(INamedTypeSymbol type, string name) => type.GetMembers(name).Length > 0;

    private static bool DerivesFromRamblaState(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::" + BaseTypeMetadataName)
            {
                return true;
            }
        }

        return false;
    }

    private static System.Collections.Generic.IEnumerable<INamedTypeSymbol> SelfAndContainingTypes(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            yield return t;
        }
    }

    private static bool IsPartial(INamedTypeSymbol type) => type.DeclaringSyntaxReferences.Any(static r =>
        r.GetSyntax() is TypeDeclarationSyntax decl && decl.Modifiers.Any(SyntaxKind.PartialKeyword));

    private static string Keyword(INamedTypeSymbol type)
    {
        if (type.IsRecord)
        {
            return type.TypeKind == TypeKind.Struct ? "record struct" : "record";
        }

        return type.TypeKind switch
        {
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => "class",
        };
    }

    private static string NameWithTypeParameters(INamedTypeSymbol type)
    {
        if (type.TypeParameters.Length == 0)
        {
            return type.Name;
        }

        return type.Name + "<" + string.Join(", ", type.TypeParameters.Select(static p => p.Name)) + ">";
    }

    private static string HintName(string typeKey)
    {
        var sb = new StringBuilder(typeKey.Length);
        foreach (char c in typeKey)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.Append(".State.g.cs").ToString();
    }

    private sealed record TypeLayer(string Keyword, string Name);

    private sealed record FieldModel(
        string TypeKey,
        string? Namespace,
        ImmutableArray<TypeLayer> Nesting,
        string PropertyType,
        string PropertyName,
        string FieldName,
        Location Location);

    private sealed class FieldResult
    {
        private FieldResult(FieldModel? model, Diagnostic? diagnostic)
        {
            Model = model;
            Diagnostic = diagnostic;
        }

        public FieldModel? Model { get; }

        public Diagnostic? Diagnostic { get; }

        public static FieldResult Ok(FieldModel model) => new(model, null);

        public static FieldResult Error(Diagnostic diagnostic) => new(null, diagnostic);
    }
}
