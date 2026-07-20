using System.Diagnostics;
using System.Globalization;

namespace Rambla.Benchmarks;

/// <summary>
/// A deterministic, non-BenchmarkDotNet runner that reports the <em>rich</em>
/// metrics the story needs — mutations, effective notifications, coalescing
/// ratio, allocated bytes and time — which BenchmarkDotNet's Mean/Alloc columns
/// alone don't convey. Run with <c>dotnet run -c Release -- profile</c>.
/// </summary>
public static class ScenarioProfiler
{
    private const int Writes = 100_000;
    private const int WritesPerFlush = 1_000;

    private readonly record struct Result(long Mutations, long Notifications, double TimeMs, long AllocBytes)
    {
        public double CoalescingPercent => Mutations == 0 ? 0 : (1.0 - ((double)Notifications / Mutations)) * 100.0;
    }

    public static void Run()
    {
        Console.WriteLine($"Rambla scenario profiler — {Writes:N0} writes, flush every {WritesPerFlush:N0}\n");
        PrintLedger();
        PrintBreakEven();
        PrintBurstShape();
    }

    private static void PrintLedger()
    {
        Console.WriteLine("## Ledger — 100k writes to a single property\n");
        Console.WriteLine("| Scenario | Engine | Mutations | Notifications | Coalescing | Time | Alloc |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");

        (string label, SubscriberKind kind, int work)[] scenarios =
        {
            ("no-op subscriber", SubscriberKind.NoOp, 0),
            ("CPU work = 64", SubscriberKind.Cpu, 64),
            ("UI-like fan-out (x5)", SubscriberKind.UiFanout, 0),
        };

        foreach ((string label, SubscriberKind kind, int work) in scenarios)
        {
            Result naive = MeasureNaiveSingle(kind, work);
            Result rambla = MeasureRamblaSingle(kind, work);
            PrintRow($"Naive / {label}", "naive", naive);
            PrintRow($"Rambla / {label}", "coalesced", rambla);
        }

        Console.WriteLine();
    }

    private static void PrintBreakEven()
    {
        Console.WriteLine("## Break-even — CPU downstream work per notification\n");
        Console.WriteLine("At what per-notification cost does coalescing overtake direct notification?\n");
        Console.WriteLine("| CPU work | ~ns / notification | Naive time | Rambla time | Winner |");
        Console.WriteLine("| ---: | ---: | ---: | ---: | :--- |");

        int[] works = { 0, 4, 8, 16, 32, 64, 128, 256 };
        int? crossover = null;
        foreach (int work in works)
        {
            Result naive = MeasureNaiveSingle(SubscriberKind.Cpu, work);
            Result rambla = MeasureRamblaSingle(SubscriberKind.Cpu, work);
            double nsPerNotification = naive.TimeMs * 1_000_000.0 / naive.Notifications;
            bool ramblaWins = rambla.TimeMs <= naive.TimeMs;
            if (ramblaWins && crossover is null)
            {
                crossover = work;
            }

            Console.WriteLine(
                $"| {work} | {nsPerNotification,8:N0} | {FormatMs(naive.TimeMs)} | {FormatMs(rambla.TimeMs)} | {(ramblaWins ? "Rambla" : "Naive")} |");
        }

        Console.WriteLine(crossover is null
            ? "\nRambla did not overtake naive in the sampled range (raise the work).\n"
            : $"\nCrossover at CPU work ≈ {crossover}: beyond this per-notification cost, coalescing wins overall.\n");
    }

    private static void PrintBurstShape()
    {
        Console.WriteLine("## Burst shape — 100k writes spread across N distinct properties\n");
        Console.WriteLine("Coalescing depends on update-stream entropy: it helps only when writes repeat properties within a flush window.\n");
        Console.WriteLine("| Distinct props | Engine | Notifications | Coalescing | Time | Alloc |");
        Console.WriteLine("| ---: | --- | ---: | ---: | ---: | ---: |");

        int[] distinct = { 1, 10, 100, 1_000, 10_000, 100_000 };
        foreach (int d in distinct)
        {
            Result naive = MeasureNaiveEntropy(d);
            Result rambla = MeasureRamblaEntropy(d);
            Console.WriteLine(
                $"| {d,7:N0} | naive | {naive.Notifications,7:N0} | {naive.CoalescingPercent,6:N1}% | {FormatMs(naive.TimeMs)} | {FormatBytes(naive.AllocBytes)} |");
            Console.WriteLine(
                $"| {d,7:N0} | Rambla | {rambla.Notifications,7:N0} | {rambla.CoalescingPercent,6:N1}% | {FormatMs(rambla.TimeMs)} | {FormatBytes(rambla.AllocBytes)} |");
        }

        Console.WriteLine("\nAs distinct properties per window approach the write count, coalescing → 0% and Rambla's benefit disappears. That is the boundary of where Rambla belongs.\n");
    }

    private static Result MeasureNaiveSingle(SubscriberKind kind, int work)
    {
        static Result Body(SubscriberKind kind, int work)
        {
            var row = new NaiveRow();
            long notifications = 0;
            row.PropertyChanged += (_, _) =>
            {
                notifications++;
                Workload.Consume(kind, work, row.Bid);
            };

            long alloc0 = GcAlloc();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Writes; i++)
            {
                row.Bid = i + 1;
            }

            sw.Stop();
            return new Result(Writes, notifications, sw.Elapsed.TotalMilliseconds, GcAlloc() - alloc0);
        }

        return BestOf(() => Body(kind, work));
    }

    private static Result MeasureRamblaSingle(SubscriberKind kind, int work)
    {
        static Result Body(SubscriberKind kind, int work)
        {
            var scheduler = new IntervalScheduler();
            var row = new BenchRow(scheduler);
            long notifications = 0;
            row.PropertyChanged += (_, _) =>
            {
                notifications++;
                Workload.Consume(kind, work, row.Bid);
            };

            long alloc0 = GcAlloc();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Writes; i++)
            {
                row.Bid = i + 1;
                if (i % WritesPerFlush == 0)
                {
                    scheduler.Tick();
                }
            }

            scheduler.Tick();
            sw.Stop();
            return new Result(Writes, notifications, sw.Elapsed.TotalMilliseconds, GcAlloc() - alloc0);
        }

        return BestOf(() => Body(kind, work));
    }

    private static Result MeasureNaiveEntropy(int distinct)
    {
        Result Body()
        {
            var row = new NaiveEntropyRow(distinct);
            long notifications = 0;
            row.PropertyChanged += (_, _) => notifications++;

            long alloc0 = GcAlloc();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Writes; i++)
            {
                row.SetAt(i % distinct, i + 1);
            }

            sw.Stop();
            return new Result(Writes, notifications, sw.Elapsed.TotalMilliseconds, GcAlloc() - alloc0);
        }

        return BestOf(Body);
    }

    private static Result MeasureRamblaEntropy(int distinct)
    {
        Result Body()
        {
            var scheduler = new IntervalScheduler();
            var state = new EntropyState(distinct, scheduler);
            long notifications = 0;
            state.PropertyChanged += (_, _) => notifications++;

            long alloc0 = GcAlloc();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Writes; i++)
            {
                state.SetAt(i % distinct, i + 1);
                if (i % WritesPerFlush == 0)
                {
                    scheduler.Tick();
                }
            }

            scheduler.Tick();
            sw.Stop();
            return new Result(Writes, notifications, sw.Elapsed.TotalMilliseconds, GcAlloc() - alloc0);
        }

        return BestOf(Body);
    }

    // One warmup, then best (lowest-time) of three — counts/alloc taken from the timed run.
    private static Result BestOf(Func<Result> body)
    {
        body();
        Result best = body();
        for (int i = 0; i < 2; i++)
        {
            Result candidate = body();
            if (candidate.TimeMs < best.TimeMs)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static long GcAlloc() => GC.GetAllocatedBytesForCurrentThread();

    private static void PrintRow(string scenario, string engine, Result r)
        => Console.WriteLine(
            $"| {scenario} | {engine} | {r.Mutations,7:N0} | {r.Notifications,7:N0} | {r.CoalescingPercent,6:N2}% | {FormatMs(r.TimeMs)} | {FormatBytes(r.AllocBytes)} |");

    private static string FormatMs(double ms) => ms >= 1000
        ? (ms / 1000.0).ToString("N2", CultureInfo.InvariantCulture) + " s"
        : ms.ToString("N3", CultureInfo.InvariantCulture) + " ms";

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? (bytes / (1024.0 * 1024.0)).ToString("N2", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024.0).ToString("N1", CultureInfo.InvariantCulture) + " KB";
}
