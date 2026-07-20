using BenchmarkDotNet.Running;

// Runs every *Benchmark in this assembly. Example:
//   dotnet run -c Release --project benchmarks/Rambla.Benchmarks
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
