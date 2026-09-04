using ExampleBank.Ledger.Api.Auth;
using ExampleBank.Ledger.Api.Contracts;
using ExampleBank.Ledger.Application.Accounts;
using ExampleBank.Ledger.Application.Entries;
using ExampleBank.Ledger.Application.Fx;
using ExampleBank.Ledger.Application.Holds;
using ExampleBank.Ledger.Application.Integrity;
using ExampleBank.Ledger.Application.Reports;
using ExampleBank.Ledger.Application.Reversals;
using ExampleBank.Ledger.Application.Statements;
using ExampleBank.Ledger.Application.Transfers;
using Microsoft.AspNetCore.Mvc;

namespace ExampleBank.Ledger.Api.Endpoints;

/// <summary>Maps every versioned ledger endpoint with its authorization policy.</summary>
public static class LedgerEndpoints
{
    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        MapAccounts(app);
        MapEntries(app);
        MapTransfers(app);
        MapHolds(app);
        MapReversals(app);
        MapFx(app);
        MapStatements(app);
        MapReports(app);
        MapAdmin(app);
        return app;
    }

    private static void MapAccounts(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/accounts").WithTags("Accounts");

        group.MapPost("/", async (CreateAccountRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var created = await accounts.CreateAsync(request, ct);
            return Results.Created($"/api/v1/accounts/{created.Id}", created);
        }).RequireAuthorization(LedgerPolicies.Admin);

        group.MapGet("/", async (AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.ListAsync(ct))).RequireAuthorization(LedgerPolicies.Read);

        group.MapGet("/{id:guid}", async (Guid id, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.GetAsync(id, ct))).RequireAuthorization(LedgerPolicies.Read);

        group.MapGet("/{id:guid}/balance", async (Guid id, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.GetBalanceAsync(id, ct))).RequireAuthorization(LedgerPolicies.Read);

        group.MapPost("/{id:guid}/freeze", async (Guid id, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.FreezeAsync(id, ct))).RequireAuthorization(LedgerPolicies.Adjust);

        group.MapPost("/{id:guid}/activate", async (Guid id, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.ActivateAsync(id, ct))).RequireAuthorization(LedgerPolicies.Adjust);

        group.MapPost("/{id:guid}/close", async (Guid id, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.CloseAsync(id, ct))).RequireAuthorization(LedgerPolicies.Admin);
    }

    private static void MapEntries(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/entries").WithTags("Entries");

        group.MapPost("/", async (PostEntryRequest request, HttpContext ctx, EntryService entries, CancellationToken ct) =>
        {
            var result = await entries.PostAsync(
                request with { IdempotencyKey = ctx.IdempotencyKey(), CorrelationId = ctx.CorrelationId() }, ct);
            return Results.Created($"/api/v1/entries/{result.Id}", result);
        }).RequireAuthorization(LedgerPolicies.Post);

        group.MapGet("/{id:guid}", async (Guid id, EntryService entries, CancellationToken ct) =>
            Results.Ok(await entries.GetAsync(id, ct))).RequireAuthorization(LedgerPolicies.Read);

        group.MapGet("/", async (HttpContext ctx, EntryService entries, CancellationToken ct) =>
            Results.Ok(await entries.ListAsync(ctx.Paging(), ct))).RequireAuthorization(LedgerPolicies.Read);
    }

    private static void MapTransfers(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/transfers", async (TransferRequest request, HttpContext ctx, TransferService transfers, CancellationToken ct) =>
        {
            var result = await transfers.TransferAsync(
                request with { IdempotencyKey = ctx.IdempotencyKey(), CorrelationId = ctx.CorrelationId() }, ct);
            return Results.Created($"/api/v1/entries/{result.Id}", result);
        }).WithTags("Transfers").RequireAuthorization(LedgerPolicies.Post);
    }

    private static void MapHolds(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/holds").WithTags("Holds");

        group.MapPost("/", async (PlaceHoldRequest request, HttpContext ctx, HoldService holds, CancellationToken ct) =>
        {
            var result = await holds.PlaceAsync(request with { IdempotencyKey = ctx.IdempotencyKey() }, ct);
            return Results.Created($"/api/v1/holds/{result.Id}", result);
        }).RequireAuthorization(LedgerPolicies.Post);

        group.MapGet("/{id:guid}", async (Guid id, HoldService holds, CancellationToken ct) =>
            Results.Ok(await holds.GetAsync(id, ct))).RequireAuthorization(LedgerPolicies.Read);

        group.MapPost("/{id:guid}/capture", async (Guid id, CaptureHoldBody body, HttpContext ctx, HoldService holds, CancellationToken ct) =>
        {
            var request = new CaptureHoldRequest(id, body.DestinationAccountId, body.CaptureMinor, body.Reference, ctx.IdempotencyKey());
            return Results.Ok(await holds.CaptureAsync(request, ct));
        }).RequireAuthorization(LedgerPolicies.Post);

        group.MapPost("/{id:guid}/release", async (Guid id, HttpContext ctx, HoldService holds, CancellationToken ct) =>
            Results.Ok(await holds.ReleaseAsync(id, ctx.IdempotencyKey(), ct))).RequireAuthorization(LedgerPolicies.Post);
    }

    private static void MapReversals(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/reversals", async (ReversalRequest request, HttpContext ctx, ReversalService reversals, CancellationToken ct) =>
        {
            var result = await reversals.ReverseAsync(
                request with { IdempotencyKey = ctx.IdempotencyKey(), CorrelationId = ctx.CorrelationId() }, ct);
            return Results.Created($"/api/v1/entries/{result.Id}", result);
        }).WithTags("Reversals").RequireAuthorization(LedgerPolicies.Adjust);
    }

    private static void MapFx(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/fx").WithTags("FX");

        group.MapPost("/convert", async (FxConvertRequest request, HttpContext ctx, FxService fx, CancellationToken ct) =>
        {
            var result = await fx.ConvertAsync(
                request with { IdempotencyKey = ctx.IdempotencyKey(), CorrelationId = ctx.CorrelationId() }, ct);
            return Results.Created($"/api/v1/entries/{result.Id}", result);
        }).RequireAuthorization(LedgerPolicies.Post);

        group.MapPost("/quote", async (FxConvertRequest request, FxService fx, CancellationToken ct) =>
            Results.Ok(await fx.PreviewAsync(request, ct))).RequireAuthorization(LedgerPolicies.Read);
    }

    private static void MapStatements(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/statements/{accountId:guid}", async (
            Guid accountId,
            DateOnly? from,
            DateOnly? to,
            string? format,
            HttpContext ctx,
            StatementService statements,
            CancellationToken ct) =>
        {
            var paging = ctx.Paging();
            var request = new StatementRequest(
                accountId,
                from ?? DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-1),
                to ?? DateOnly.FromDateTime(DateTime.UtcNow),
                paging.Page,
                paging.PageSize);

            var statement = await statements.GetAsync(request, ct);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Text(StatementService.ToCsv(statement), "text/csv");
            }

            return Results.Ok(statement);
        }).WithTags("Statements").RequireAuthorization(LedgerPolicies.Read);
    }

    private static void MapReports(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/reports/trial-balance", async (TrialBalanceService trialBalance, CancellationToken ct) =>
            Results.Ok(await trialBalance.GetAsync(ct)))
            .WithTags("Reports").RequireAuthorization(LedgerPolicies.Read);
    }

    private static void MapAdmin(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/integrity/verify", async (IntegrityService integrity, CancellationToken ct) =>
            Results.Ok(await integrity.VerifyAsync(ct)))
            .WithTags("Admin").RequireAuthorization(LedgerPolicies.Admin);
    }
}
