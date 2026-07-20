using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Rambla.Demo.MarketDashboard.Diagnostics;
using Rambla.Demo.MarketDashboard.Model;

namespace Rambla.Demo.MarketDashboard.Feed;

/// <summary>
/// A background producer that random-walks quotes for every symbol at a target
/// rate. It writes to the rows through <see cref="ISymbolRow.Apply"/> and counts
/// each field write as one incoming update — this is the "thousands of updates
/// per second" pressure the UI must survive.
/// </summary>
public sealed class SyntheticFeed
{
    private const int FieldsPerQuote = 5;

    private readonly ISymbolRow[] _rows;
    private readonly DemoMetrics _metrics;
    private readonly int _targetQuotesPerSecond;

    private CancellationTokenSource? _cts;
    private Task? _worker;

    public SyntheticFeed(ISymbolRow[] rows, DemoMetrics metrics, int targetQuotesPerSecond)
    {
        _rows = rows;
        _metrics = metrics;
        _targetQuotesPerSecond = Math.Max(1, targetQuotesPerSecond);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => Run(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null || _worker is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _worker = null;
        }
    }

    private void Run(CancellationToken token)
    {
        // Deterministic-ish PRNG so runs are comparable; not security-sensitive.
        var random = new Random(17);
        var seed = new decimal[_rows.Length];
        for (int i = 0; i < seed.Length; i++)
        {
            seed[i] = 100m + i;
        }

        const int batch = 256;
        var clock = Stopwatch.StartNew();
        long emitted = 0;

        while (!token.IsCancellationRequested)
        {
            for (int b = 0; b < batch; b++)
            {
                int index = random.Next(_rows.Length);
                decimal mid = seed[index] + ((decimal)random.NextDouble() - 0.5m);
                seed[index] = mid;

                decimal bid = decimal.Round(mid - 0.01m, 2);
                decimal ask = decimal.Round(mid + 0.01m, 2);
                decimal last = decimal.Round(mid, 2);
                decimal volume = random.Next(1, 10_000);
                decimal pnl = decimal.Round((decimal)((random.NextDouble() - 0.5) * 1000.0), 2);

                _rows[index].Apply(bid, ask, last, volume, pnl);
            }

            emitted += batch;
            _metrics.OnIncomingUpdates(batch * FieldsPerQuote);

            // Soft rate limit: throttle to the target quotes/second.
            double expectedSeconds = emitted / (double)_targetQuotesPerSecond;
            double actualSeconds = clock.Elapsed.TotalSeconds;
            double aheadMs = (expectedSeconds - actualSeconds) * 1000.0;
            if (aheadMs > 1.0)
            {
                Thread.Sleep((int)Math.Min(aheadMs, 50));
            }
        }
    }
}
