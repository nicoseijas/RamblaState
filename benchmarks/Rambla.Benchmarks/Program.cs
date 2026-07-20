using BenchmarkDotNet.Running;
using Rambla.Benchmarks;

// Rich, deterministic ledger (counts, coalescing, break-even, burst shape):
//   dotnet run -c Release --project benchmarks/Rambla.Benchmarks -- profile
// Rigorous time/allocation via BenchmarkDotNet:
//   dotnet run -c Release --project benchmarks/Rambla.Benchmarks -- --filter "*"
if (args.Length > 0 && args[0].Equals("profile", StringComparison.OrdinalIgnoreCase))
{
    ScenarioProfiler.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
