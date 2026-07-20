using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rambla.Generators;
using Xunit;

namespace Rambla.Tests;

public sealed class GeneratorTests
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private sealed record Run(string Generated, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> AllDiagnostics)
    {
        public bool HasError(string id) => GeneratorDiagnostics.Any(d => d.Id == id);

        public IEnumerable<Diagnostic> Errors => AllDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    }

    // --- success cases ---

    [Fact]
    public void Generates_property_routed_through_SetField()
    {
        Run run = Generate("""
            using Rambla;
            namespace Demo;
            public partial class Quote : RamblaState
            {
                [State] private decimal _bid;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public decimal Bid");
        run.Generated.Should().Contain("get => _bid;");
        run.Generated.Should().Contain("set => SetField(ref _bid, value);");
    }

    [Fact]
    public void Strips_leading_underscore_and_pascal_cases()
    {
        Run run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private decimal _bidPrice;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public decimal BidPrice");
    }

    [Fact]
    public void Preserves_nullable_reference_type()
    {
        Run run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private string? _label;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public string? Label");
    }

    [Fact]
    public void Generates_a_property_per_state_field()
    {
        Run run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private decimal _bid;
                [State] private decimal _ask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public decimal Bid");
        run.Generated.Should().Contain("public decimal Ask");
    }

    [Fact]
    public void Supports_generic_and_nested_containing_types()
    {
        Run run = Generate("""
            using Rambla;
            namespace Demo;
            public partial class Outer<T>
            {
                public partial class Inner : RamblaState
                {
                    [State] private int _count;
                }
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("partial class Outer<T>");
        run.Generated.Should().Contain("partial class Inner");
        run.Generated.Should().Contain("public int Count");
    }

    // --- diagnostics ---

    [Fact]
    public void RMB001_when_containing_type_not_partial()
    {
        Run run = Generate("""
            using Rambla;
            public class Quote : RamblaState
            {
                [State] private decimal _bid;
            }
            """);

        run.HasError("RMB001").Should().BeTrue();
    }

    [Fact]
    public void RMB002_when_field_is_static()
    {
        Run run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private static decimal _bid;
            }
            """);

        run.HasError("RMB002").Should().BeTrue();
    }

    [Fact]
    public void RMB003_when_name_collides_with_existing_member()
    {
        Run run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private decimal _bid;
                public decimal Bid => 0m;
            }
            """);

        run.HasError("RMB003").Should().BeTrue();
    }

    [Fact]
    public void RMB004_when_field_is_readonly()
    {
        Run run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private readonly decimal _bid;
            }
            """);

        run.HasError("RMB004").Should().BeTrue();
    }

    [Fact]
    public void RMB005_when_type_does_not_derive_from_RamblaState()
    {
        Run run = Generate("""
            using Rambla;
            public partial class Quote
            {
                [State] private decimal _bid;
            }
            """);

        run.HasError("RMB005").Should().BeTrue();
    }

    private static Run Generate(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new StateGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        GeneratorDriverRunResult result = driver.GetRunResult();
        string generated = string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));

        return new Run(generated, result.Diagnostics, output.GetDiagnostics());
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var byName = new Dictionary<string, MetadataReference>(System.StringComparer.OrdinalIgnoreCase);
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (string path in tpa.Split(Path.PathSeparator))
        {
            if (path.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
            {
                byName[Path.GetFileName(path)] = MetadataReference.CreateFromFile(path);
            }
        }

        string ramblaPath = typeof(RamblaState).Assembly.Location;
        byName[Path.GetFileName(ramblaPath)] = MetadataReference.CreateFromFile(ramblaPath);

        return byName.Values.ToImmutableArray();
    }
}
