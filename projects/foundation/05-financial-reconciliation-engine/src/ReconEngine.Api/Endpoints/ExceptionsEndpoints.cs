using System.Security.Claims;
using ReconEngine.Api.Auth;
using ReconEngine.Api.Contracts;
using ReconEngine.Application.Common;
using ReconEngine.Application.Exceptions;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Api.Endpoints;

public static class ExceptionsEndpoints
{
    public static IEndpointRouteBuilder MapExceptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/exceptions").WithTags("Exceptions").RequireAuthorization();

        group.MapGet("/", async (
            ExceptionStatus? status, ExceptionType? type, ExceptionSeverity? severity, string? currency,
            string? assignedTo, string? sortBy, bool? desc, int? page, int? pageSize,
            ExceptionWorkflowService svc, CancellationToken ct) =>
        {
            var (p, size) = EndpointHelpers.NormalizePaging(page, pageSize);
            var query = new ExceptionQuery(status, type, severity, currency, assignedTo,
                sortBy ?? "createdAt", desc ?? true, (p - 1) * size, size);
            var result = await svc.QueryAsync(query, ct);
            var mapped = new PagedResult<ExceptionResponse>(
                result.Items.Select(e => e.ToResponse()).ToList(), result.Page, result.PageSize, result.TotalCount);
            return Results.Ok(mapped);
        })
        .WithName("ListExceptions")
        .WithSummary("List/filter/sort/paginate reconciliation exceptions.");

        group.MapGet("/{id:guid}", async (Guid id, ExceptionWorkflowService svc, CancellationToken ct) =>
        {
            var ex = await svc.GetAsync(id, ct);
            return ex is null ? Results.NotFound() : Results.Ok(ex.ToResponse());
        })
        .WithName("GetException");

        group.MapPost("/{id:guid}/assign", async (Guid id, AssignRequest request, ClaimsPrincipal user, ExceptionWorkflowService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Assignee))
                return Results.BadRequest(new { error = "Assignee is required." });
            var ex = await svc.AssignAsync(id, request.Assignee, user.CurrentUser(), ct);
            return Results.Ok(ex.ToResponse());
        })
        .RequireAuthorization(ReconScopes.Resolve)
        .WithName("AssignException");

        group.MapPost("/{id:guid}/comment", async (Guid id, CommentRequest request, ClaimsPrincipal user, ExceptionWorkflowService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest(new { error = "Comment text is required." });
            var ex = await svc.CommentAsync(id, user.CurrentUser(), request.Text, ct);
            return Results.Ok(ex.ToResponse());
        })
        .RequireAuthorization(ReconScopes.Resolve)
        .WithName("CommentException");

        group.MapPost("/{id:guid}/resolve", async (Guid id, ResolveRequest request, ClaimsPrincipal user, ExceptionWorkflowService svc, CancellationToken ct) =>
        {
            var ex = await svc.ResolveAsync(id, request.Reason, user.CurrentUser(), request.Note, ct);
            return Results.Ok(ex.ToResponse());
        })
        .RequireAuthorization(ReconScopes.Resolve)
        .WithName("ResolveException")
        .WithSummary("Resolve an exception; write-offs above the threshold enter four-eyes approval.");

        group.MapPost("/{id:guid}/approve", async (Guid id, ClaimsPrincipal user, ExceptionWorkflowService svc, CancellationToken ct) =>
        {
            var ex = await svc.ApproveAsync(id, user.CurrentUser(), ct);
            return Results.Ok(ex.ToResponse());
        })
        .RequireAuthorization(ReconScopes.Approve)
        .WithName("ApproveException")
        .WithSummary("Second-reviewer approval of a pending write-off (four-eyes).");

        group.MapPost("/{id:guid}/reject", async (Guid id, NoteRequest request, ClaimsPrincipal user, ExceptionWorkflowService svc, CancellationToken ct) =>
        {
            var ex = await svc.RejectApprovalAsync(id, user.CurrentUser(), request.Note, ct);
            return Results.Ok(ex.ToResponse());
        })
        .RequireAuthorization(ReconScopes.Approve)
        .WithName("RejectException");

        group.MapPost("/{id:guid}/reopen", async (Guid id, NoteRequest request, ClaimsPrincipal user, ExceptionWorkflowService svc, CancellationToken ct) =>
        {
            var ex = await svc.ReopenAsync(id, user.CurrentUser(), request.Note, ct);
            return Results.Ok(ex.ToResponse());
        })
        .RequireAuthorization(ReconScopes.Resolve)
        .WithName("ReopenException");

        return app;
    }
}
