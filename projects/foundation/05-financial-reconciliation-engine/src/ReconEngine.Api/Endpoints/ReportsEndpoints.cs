using System.Globalization;
using ReconEngine.Application.Reporting;
using ReconEngine.Infrastructure.Export;

namespace ReconEngine.Api.Endpoints;

public static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/reports").WithTags("Reports").RequireAuthorization();

        group.MapGet("/runs/{runId:guid}/summary", async (Guid runId, ReportService reports, CancellationToken ct) =>
            Results.Ok(await reports.RunSummaryAsync(runId, ct)))
            .WithName("ReportRunSummary");

        group.MapGet("/aging", async (string? format, ReportService reports, CancellationToken ct) =>
        {
            var rows = await reports.AgingAsync(ct);
            if (IsCsv(format))
            {
                var csv = CsvWriter.Write(
                    new[] { "Currency", "Bucket", "Count", "AbsValueMinor" },
                    rows.Select(r => new[] { r.Currency, r.Bucket, r.Count.ToString(CultureInfo.InvariantCulture), r.AbsValueMinor.ToString(CultureInfo.InvariantCulture) }));
                return CsvResult(csv, "aging.csv");
            }
            return Results.Ok(rows);
        })
        .WithName("ReportAging")
        .WithSummary("Aging buckets of open exceptions (supports ?format=csv).");

        group.MapGet("/runs/{runId:guid}/value-by-currency", async (Guid runId, string? format, ReportService reports, CancellationToken ct) =>
        {
            var rows = await reports.ValueByCurrencyAsync(runId, ct);
            if (IsCsv(format))
            {
                var csv = CsvWriter.Write(
                    new[] { "Currency", "InternalMinor", "MatchedInternalMinor", "UnmatchedInternalMinor", "ExternalMinor", "MatchedExternalMinor", "UnmatchedExternalMinor" },
                    rows.Select(r => new[]
                    {
                        r.Currency,
                        r.InternalMinor.ToString(CultureInfo.InvariantCulture),
                        r.MatchedInternalMinor.ToString(CultureInfo.InvariantCulture),
                        r.UnmatchedInternalMinor.ToString(CultureInfo.InvariantCulture),
                        r.ExternalMinor.ToString(CultureInfo.InvariantCulture),
                        r.MatchedExternalMinor.ToString(CultureInfo.InvariantCulture),
                        r.UnmatchedExternalMinor.ToString(CultureInfo.InvariantCulture),
                    }));
                return CsvResult(csv, $"value-by-currency-{runId}.csv");
            }
            return Results.Ok(rows);
        })
        .WithName("ReportValueByCurrency");

        group.MapGet("/runs/{runId:guid}/value-by-day", async (Guid runId, string? format, ReportService reports, CancellationToken ct) =>
        {
            var rows = await reports.ValueByDayAsync(runId, ct);
            if (IsCsv(format))
            {
                var csv = CsvWriter.Write(
                    new[] { "Date", "Currency", "MatchedCount", "MatchedMinor", "UnmatchedCount", "UnmatchedMinor" },
                    rows.Select(r => new[]
                    {
                        r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        r.Currency,
                        r.MatchedCount.ToString(CultureInfo.InvariantCulture),
                        r.MatchedMinor.ToString(CultureInfo.InvariantCulture),
                        r.UnmatchedCount.ToString(CultureInfo.InvariantCulture),
                        r.UnmatchedMinor.ToString(CultureInfo.InvariantCulture),
                    }));
                return CsvResult(csv, $"value-by-day-{runId}.csv");
            }
            return Results.Ok(rows);
        })
        .WithName("ReportValueByDay");

        group.MapGet("/runs/{runId:guid}/fees", async (Guid runId, string? format, ReportService reports, CancellationToken ct) =>
        {
            var rows = await reports.FeeReconciliationAsync(runId, ct);
            if (IsCsv(format))
            {
                var csv = CsvWriter.Write(
                    new[] { "Currency", "FeeAdjustedMatches", "ExpectedFeeMinor", "FeeVarianceMinor", "VarianceCount" },
                    rows.Select(r => new[]
                    {
                        r.Currency,
                        r.FeeAdjustedMatches.ToString(CultureInfo.InvariantCulture),
                        r.ExpectedFeeMinor.ToString(CultureInfo.InvariantCulture),
                        r.FeeVarianceMinor.ToString(CultureInfo.InvariantCulture),
                        r.VarianceCount.ToString(CultureInfo.InvariantCulture),
                    }));
                return CsvResult(csv, $"fees-{runId}.csv");
            }
            return Results.Ok(rows);
        })
        .WithName("ReportFees");

        group.MapGet("/runs/{runId:guid}/balance", async (Guid runId, ReportService reports, CancellationToken ct) =>
            Results.Ok(await reports.BalanceAsync(runId, ct)))
            .WithName("ReportBalance")
            .WithSummary("Per-currency balance assertion (internal = matched + unmatched).");

        return app;
    }

    private static bool IsCsv(string? format) => string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);

    private static IResult CsvResult(string csv, string fileName)
        => Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
}
