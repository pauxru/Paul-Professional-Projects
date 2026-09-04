using System.Security.Claims;
using Collab.Api.Auth;
using Collab.Application.Contracts;
using Collab.Application.Services;

namespace Collab.Api.Endpoints;

/// <summary>REST surface for documents: lifecycle, content, history, versions, diff, restore, time-travel.</summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/documents").WithTags("Documents").RequireAuthorization();

        group.MapPost("/", async (CreateDocumentRequest request, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
        {
            var dto = await service.CreateAsync(user.GetUserId(), request, ct);
            return Results.Created($"/api/v1/documents/{dto.Id}", dto);
        });

        group.MapGet("/", async (Guid workspaceId, int? page, int? pageSize, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(user.GetUserId(), workspaceId, page ?? 1, pageSize ?? 50, ct)));

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
            Results.Ok(await service.GetContentAsync(user.GetUserId(), id, ct)));

        group.MapGet("/{id:guid}/history", async (Guid id, int? page, int? pageSize, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
            Results.Ok(await service.GetHistoryAsync(user.GetUserId(), id, page ?? 1, pageSize ?? 50, ct)));

        group.MapGet("/{id:guid}/at/{sequence:long}", async (Guid id, long sequence, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
            Results.Ok(await service.TimeTravelAsync(user.GetUserId(), id, sequence, ct)));

        group.MapPost("/{id:guid}/versions", async (Guid id, CreateNamedVersionRequest request, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
        {
            var dto = await service.CreateNamedVersionAsync(user.GetUserId(), id, request, ct);
            return Results.Created($"/api/v1/documents/{id}/versions/{dto.Id}", dto);
        });

        group.MapGet("/{id:guid}/versions", async (Guid id, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
            Results.Ok(await service.ListVersionsAsync(user.GetUserId(), id, ct)));

        group.MapGet("/{id:guid}/diff", async (Guid id, long from, long to, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
            Results.Ok(await service.DiffAsync(user.GetUserId(), id, from, to, ct)));

        group.MapPost("/{id:guid}/restore", async (Guid id, RestoreRequest request, ClaimsPrincipal user, DocumentService service, CancellationToken ct) =>
            Results.Ok(await service.RestoreAsync(user.GetUserId(), id, request, ct)));

        return app;
    }
}
