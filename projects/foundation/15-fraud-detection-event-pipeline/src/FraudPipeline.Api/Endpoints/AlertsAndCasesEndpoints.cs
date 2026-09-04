using FraudPipeline.Api.Contracts;
using FraudPipeline.Application.Abstractions;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FraudPipeline.Api.Endpoints;

public static class AlertsAndCasesEndpoints
{
    public static IEndpointRouteBuilder MapAlertsAndCasesEndpoints(this IEndpointRouteBuilder app)
    {
        var alerts = app.MapGroup("/api/v1/alerts").WithTags("Alerts");
        alerts.MapGet("/", ListAlertsAsync).RequireAuthorization("risk:investigate");

        var cases = app.MapGroup("/api/v1/cases").WithTags("Cases");
        cases.MapGet("/", ListCasesAsync).RequireAuthorization("risk:investigate");
        cases.MapGet("/{id:guid}", GetCaseAsync).RequireAuthorization("risk:investigate");
        cases.MapPost("/{id:guid}/assign", AssignAsync).RequireAuthorization("risk:investigate");
        cases.MapPost("/{id:guid}/notes", AddNoteAsync).RequireAuthorization("risk:investigate");
        cases.MapPost("/{id:guid}/disposition", ProposeDispositionAsync).RequireAuthorization("risk:investigate");
        cases.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization("risk:approve");

        return app;
    }

    private static async Task<Ok<PagedResponse<AlertView>>> ListAlertsAsync(
        IAlertRepository repo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 200) pageSize = 25;
        var items = await repo.ListAsync(page, pageSize, ct);
        var total = await repo.CountAsync(ct);
        return TypedResults.Ok(new PagedResponse<AlertView>(
            items.Select(a => new AlertView(a.Id, a.TransactionId, a.CaseId, a.PrimaryEntityKey, a.Score, a.ReasonSummary, a.Status.ToString(), a.CreatedAt)).ToList(),
            page, pageSize, total, (int)Math.Ceiling((double)total / pageSize)));
    }

    private static async Task<Ok<PagedResponse<CaseView>>> ListCasesAsync(
        ICaseRepository repo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 200) pageSize = 25;
        var items = await repo.ListAsync(page, pageSize, ct);
        var total = await repo.CountAsync(ct);
        return TypedResults.Ok(new PagedResponse<CaseView>(
            items.Select(ToView).ToList(),
            page, pageSize, total, (int)Math.Ceiling((double)total / pageSize)));
    }

    private static async Task<Results<Ok<CaseView>, NotFound>> GetCaseAsync(Guid id, ICaseRepository repo, CancellationToken ct)
    {
        var c = await repo.GetByIdAsync(id, ct);
        if (c is null) return TypedResults.NotFound();
        return TypedResults.Ok(ToView(c));
    }

    private static async Task<Results<Ok, NotFound, BadRequest<ProblemDetails>>> AssignAsync(
        Guid id,
        [FromBody] CaseAssignRequest request,
        ICaseRepository repo,
        IClock clock,
        CancellationToken ct)
    {
        var c = await repo.GetByIdAsync(id, ct);
        if (c is null) return TypedResults.NotFound();
        try
        {
            c.Assign(request.Investigator, clock.UtcNow);
            await repo.SaveAsync(ct);
            return TypedResults.Ok();
        }
        catch (Exception ex) { return TypedResults.BadRequest(new ProblemDetails { Title = "Assign failed", Detail = ex.Message, Status = 400 }); }
    }

    private static async Task<Results<Ok, NotFound, BadRequest<ProblemDetails>>> AddNoteAsync(
        Guid id,
        [FromBody] CaseNoteRequest request,
        ICaseRepository repo,
        IIdGenerator ids,
        IClock clock,
        CancellationToken ct)
    {
        var c = await repo.GetByIdAsync(id, ct);
        if (c is null) return TypedResults.NotFound();
        try
        {
            c.AddNote(ids.NewGuid(), request.Author, request.Text, clock.UtcNow);
            await repo.SaveAsync(ct);
            return TypedResults.Ok();
        }
        catch (Exception ex) { return TypedResults.BadRequest(new ProblemDetails { Title = "Note failed", Detail = ex.Message, Status = 400 }); }
    }

    private static async Task<Results<Ok<CaseView>, NotFound, BadRequest<ProblemDetails>>> ProposeDispositionAsync(
        Guid id,
        [FromBody] CaseDispositionRequest request,
        ICaseRepository repo,
        IClock clock,
        CancellationToken ct)
    {
        var c = await repo.GetByIdAsync(id, ct);
        if (c is null) return TypedResults.NotFound();
        if (!Enum.TryParse<CaseDisposition>(request.Disposition, out var disposition))
            return TypedResults.BadRequest(new ProblemDetails { Title = "Invalid disposition" });
        try
        {
            c.ProposeDisposition(disposition, request.Reason, request.By, clock.UtcNow);
            await repo.SaveAsync(ct);
            return TypedResults.Ok(ToView(c));
        }
        catch (Exception ex) { return TypedResults.BadRequest(new ProblemDetails { Title = "Disposition failed", Detail = ex.Message, Status = 400 }); }
    }

    private static async Task<Results<Ok<CaseView>, NotFound, BadRequest<ProblemDetails>>> ApproveAsync(
        Guid id,
        [FromBody] CaseApprovalRequest request,
        ICaseRepository repo,
        IClock clock,
        CancellationToken ct)
    {
        var c = await repo.GetByIdAsync(id, ct);
        if (c is null) return TypedResults.NotFound();
        try
        {
            c.Approve(request.Approver, clock.UtcNow);
            await repo.SaveAsync(ct);
            return TypedResults.Ok(ToView(c));
        }
        catch (Exception ex) { return TypedResults.BadRequest(new ProblemDetails { Title = "Approve failed", Detail = ex.Message, Status = 400 }); }
    }

    private static CaseView ToView(Case c) => new(
        c.Id, c.PrimaryEntityKey, c.Status.ToString(), c.Disposition.ToString(),
        c.ExposureAmount, c.ExposureCurrency, c.PriorityScore,
        c.AssignedTo, c.DispositionBy, c.ApprovedBy, c.DispositionReason,
        c.CreatedAt, c.DisposedAt, c.LastActivityAt);
}

public sealed record AlertView(Guid Id, Guid TransactionId, Guid? CaseId, string PrimaryEntityKey, int Score, string ReasonSummary, string Status, DateTimeOffset CreatedAt);
public sealed record CaseView(Guid Id, string PrimaryEntityKey, string Status, string Disposition, decimal ExposureAmount, string ExposureCurrency, int PriorityScore, string? AssignedTo, string? DispositionBy, string? ApprovedBy, string? DispositionReason, DateTimeOffset CreatedAt, DateTimeOffset? DisposedAt, DateTimeOffset LastActivityAt);
