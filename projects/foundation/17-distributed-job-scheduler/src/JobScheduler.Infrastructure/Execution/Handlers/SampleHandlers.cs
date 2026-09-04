using System.Text.Json;
using JobScheduler.Application.Abstractions;

namespace JobScheduler.Infrastructure.Execution.Handlers;

/// <summary>Generates a fictional operational report and returns a summary. Idempotent.</summary>
public sealed class ReportGeneratorHandler : IJobHandler
{
    public string HandlerType => "report-generator";

    public async Task<HandlerOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        context.Log($"Generating report for idempotency key {context.IdempotencyKey}.");
        var report = ParsePayload(context.PayloadJson);

        int rows = report.GetValueOrDefault("rows", 250);
        double total = 0;
        for (int i = 0; i < rows; i++)
        {
            ct.ThrowIfCancellationRequested();
            total += (i % 7) * 1.5;
            if (i % 50 == 0)
            {
                await Task.Delay(1, ct); // simulate incremental work; yields to cancellation
            }
        }

        var output = JsonSerializer.Serialize(new { rows, total, generatedFor = "Northstar Platform Team (fictional)" });
        context.Log($"Report complete: {rows} rows, total {total:0.##}.");
        return HandlerOutcome.Ok(output);
    }

    private static Dictionary<string, int> ParsePayload(string json)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
            var result = new Dictionary<string, int>();
            foreach (var (k, v) in dict)
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
                {
                    result[k] = n;
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>ETL-ish CSV transform: reads inline rows, uppercases names and sums amounts.</summary>
public sealed class CsvTransformHandler : IJobHandler
{
    public string HandlerType => "csv-transform";

    private sealed record Row(string Name, decimal Amount);

    public async Task<HandlerOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        await Task.Yield();
        List<Row> rows;
        try
        {
            using var doc = JsonDocument.Parse(context.PayloadJson);
            rows = [];
            if (doc.RootElement.TryGetProperty("rows", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    decimal amount = el.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : 0m;
                    rows.Add(new Row(name, amount));
                }
            }
        }
        catch (JsonException ex)
        {
            return HandlerOutcome.Fail($"Malformed payload: {ex.Message}", retriable: false);
        }

        if (rows.Count == 0)
        {
            // Synthesize a small dataset so the demo produces visible output.
            rows = [new Row("acme", 120.50m), new Row("northstar", 340.00m), new Row("contoso", 75.25m)];
        }

        ct.ThrowIfCancellationRequested();
        decimal total = rows.Sum(r => r.Amount);
        var transformed = rows.Select(r => new { name = r.Name.ToUpperInvariant(), r.Amount }).ToList();
        context.Log($"Transformed {rows.Count} row(s); total amount {total}.");

        var output = JsonSerializer.Serialize(new { count = rows.Count, total, transformed });
        return HandlerOutcome.Ok(output);
    }
}

/// <summary>Simulates a retention/cleanup sweep. Uses the idempotency key to stay safe on re-run.</summary>
public sealed class CleanupHandler : IJobHandler
{
    public string HandlerType => "cleanup";

    public async Task<HandlerOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        await Task.Yield();
        int olderThanDays = 30;
        try
        {
            using var doc = JsonDocument.Parse(context.PayloadJson);
            if (doc.RootElement.TryGetProperty("olderThanDays", out var d) && d.TryGetInt32(out int days))
            {
                olderThanDays = days;
            }
        }
        catch (JsonException) { /* use default */ }

        ct.ThrowIfCancellationRequested();
        int removed = 17 + (Math.Abs(context.IdempotencyKey.GetHashCode()) % 40);
        context.Log($"Cleanup removed {removed} artifact(s) older than {olderThanDays} days.");
        return HandlerOutcome.Ok(JsonSerializer.Serialize(new { removed, olderThanDays }));
    }
}

/// <summary>
/// Deliberately flaky handler for demonstrating retries. Fails while the current attempt number is
/// within the configured <c>failTimes</c>, then succeeds — deterministic for tests.
/// </summary>
public sealed class FlakyHandler : IJobHandler
{
    public string HandlerType => "flaky";

    public async Task<HandlerOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        await Task.Yield();
        int failTimes = 1;
        try
        {
            using var doc = JsonDocument.Parse(context.PayloadJson);
            if (doc.RootElement.TryGetProperty("failTimes", out var f) && f.TryGetInt32(out int ft))
            {
                failTimes = ft;
            }
        }
        catch (JsonException) { /* use default */ }

        if (context.Attempt <= failTimes)
        {
            context.Log("Warning", $"Flaky handler failing on attempt {context.Attempt} (failTimes={failTimes}).");
            return HandlerOutcome.Fail($"Transient failure on attempt {context.Attempt}.", retriable: true);
        }

        context.Log($"Flaky handler succeeded on attempt {context.Attempt}.");
        return HandlerOutcome.Ok(JsonSerializer.Serialize(new { succeededOnAttempt = context.Attempt }));
    }
}

/// <summary>Deliberately slow handler for demonstrating hard timeouts and cancellation.</summary>
public sealed class SlowHandler : IJobHandler
{
    public string HandlerType => "slow";

    public async Task<HandlerOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        int seconds = 30;
        try
        {
            using var doc = JsonDocument.Parse(context.PayloadJson);
            if (doc.RootElement.TryGetProperty("durationSeconds", out var d) && d.TryGetInt32(out int s))
            {
                seconds = s;
            }
        }
        catch (JsonException) { /* use default */ }

        context.Log($"Slow handler sleeping up to {seconds}s (honours cancellation).");
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct); // throws OperationCanceledException on timeout/cancel
        return HandlerOutcome.Ok(JsonSerializer.Serialize(new { sleptSeconds = seconds }));
    }
}
