using System.Diagnostics;
using FraudPipeline.Api.Contracts;
using FraudPipeline.Application.Abstractions;
using FraudPipeline.Application.Cases;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.ValueObjects;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FraudPipeline.Api.Endpoints;

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/transactions").WithTags("Transactions");

        group.MapPost("/score", ScoreAsync)
             .RequireAuthorization("risk:score")
             .WithName("ScoreTransaction");

        group.MapGet("/", ListAsync)
             .RequireAuthorization("risk:investigate")
             .WithName("ListTransactions");

        group.MapGet("/{txnRef}", GetByRefAsync)
             .RequireAuthorization("risk:investigate")
             .WithName("GetTransaction");

        return app;
    }

    private static async Task<Results<Ok<ScoreResponse>, BadRequest<ProblemDetails>>> ScoreAsync(
        [FromBody] ScoreRequest request,
        ScoringService scoring,
        FeatureStoreService features,
        ITransactionRepository txns,
        CaseManagementService cases,
        IIdGenerator ids,
        IClock clock,
        CancellationToken ct)
    {
        if (!Enum.TryParse<TransactionType>(request.Type, out var type))
            return TypedResults.BadRequest(new ProblemDetails { Title = "Invalid transaction type", Status = 400 });

        Money money;
        GeoLocation location;
        Transaction txn;
        try
        {
            money = Money.Of(request.Amount, request.Currency);
            location = GeoLocation.Of(request.Latitude, request.Longitude, request.Country);
            var occurredAt = request.OccurredAt ?? clock.UtcNow;
            var receivedAt = clock.UtcNow;
            if (receivedAt < occurredAt) receivedAt = occurredAt;
            txn = new Transaction(
                id: ids.NewGuid(),
                transactionRef: request.TransactionRef,
                cardId: request.CardId,
                customerId: request.CustomerId,
                deviceId: request.DeviceId,
                ipAddress: request.IpAddress,
                merchantId: request.MerchantId,
                mcc: request.Mcc,
                amount: money,
                type: type,
                location: location,
                occurredAt: occurredAt,
                receivedAt: receivedAt);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new ProblemDetails { Title = "Invalid input", Detail = ex.Message, Status = 400 });
        }

        var existing = await txns.GetByRefAsync(request.TransactionRef, ct);
        if (existing is null)
        {
            await txns.AddAsync(txn, ct);
            await txns.SaveAsync(ct);
        }
        else
        {
            txn = existing;
        }
        features.Observe(txn);
        var result = await scoring.ScoreAsync(txn, ct);
        await cases.HandleAsync(txn, result, ct);

        return TypedResults.Ok(new ScoreResponse(
            TransactionRef: result.TransactionRef,
            Score: result.Score,
            Decision: result.Decision.ToString(),
            RulesetVersion: result.RulesetVersion,
            RulesFired: result.RulesFired.Select(f => new FiringSummary(f.RuleId, f.Kind.ToString(), f.Contribution, f.Reason)).ToList(),
            LatencyMs: result.LatencyMs,
            BudgetExceeded: result.BudgetExceeded,
            Reasons: result.Reasons));
    }

    private static async Task<Ok<PagedResponse<TransactionListItem>>> ListAsync(
        ITransactionRepository txns,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 200) pageSize = 25;
        var items = await txns.ListAsync(page, pageSize, ct);
        var total = await txns.CountAsync(ct);
        return TypedResults.Ok(new PagedResponse<TransactionListItem>(
            items.Select(t => new TransactionListItem(t.TransactionRef, t.CardId, t.Amount.Amount, t.Amount.Currency, t.MerchantId, t.OccurredAt)).ToList(),
            page, pageSize, total, (int)Math.Ceiling((double)total / pageSize)));
    }

    private static async Task<Results<Ok<TransactionDetail>, NotFound>> GetByRefAsync(
        string txnRef,
        ITransactionRepository txns,
        CancellationToken ct)
    {
        var t = await txns.GetByRefAsync(txnRef, ct);
        if (t is null) return TypedResults.NotFound();
        return TypedResults.Ok(new TransactionDetail(
            t.TransactionRef, t.CardId, t.CustomerId, t.MerchantId, t.MerchantCategoryCode,
            t.Amount.Amount, t.Amount.Currency, t.Type.ToString(), t.Outcome.ToString(),
            t.Location.LatitudeDeg, t.Location.LongitudeDeg, t.Location.CountryIso2,
            t.OccurredAt, t.ReceivedAt));
    }
}

public sealed record TransactionListItem(string TransactionRef, string CardId, decimal Amount, string Currency, string MerchantId, DateTimeOffset OccurredAt);
public sealed record TransactionDetail(string TransactionRef, string CardId, string CustomerId, string MerchantId, string Mcc, decimal Amount, string Currency, string Type, string Outcome, double Latitude, double Longitude, string Country, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt);
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
