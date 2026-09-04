using System.Security.Claims;
using Collab.Api.Auth;
using Collab.Application.Contracts;
using Collab.Application.Services;

namespace Collab.Api.Endpoints;

/// <summary>REST surface for comments and threads.</summary>
public static class CommentEndpoints
{
    public static IEndpointRouteBuilder MapCommentEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/api/v1/documents/{documentId:guid}/comments").WithTags("Comments").RequireAuthorization();

        documents.MapGet("/", async (Guid documentId, ClaimsPrincipal user, CommentService service, CancellationToken ct) =>
            Results.Ok(await service.ListByDocumentAsync(user.GetUserId(), documentId, ct)));

        documents.MapPost("/", async (Guid documentId, CreateCommentRequest request, ClaimsPrincipal user, CommentService service, CancellationToken ct) =>
        {
            var dto = await service.CreateAsync(user.GetUserId(), documentId, request, ct);
            return Results.Created($"/api/v1/documents/{documentId}/comments/{dto.Id}", dto);
        });

        var comments = app.MapGroup("/api/v1/comments").WithTags("Comments").RequireAuthorization();

        comments.MapPost("/{commentId:guid}/replies", async (Guid commentId, ReplyRequest request, ClaimsPrincipal user, CommentService service, CancellationToken ct) =>
        {
            var dto = await service.ReplyAsync(user.GetUserId(), commentId, request, ct);
            return Results.Created($"/api/v1/comments/{dto.Id}", dto);
        });

        comments.MapPost("/{commentId:guid}/resolve", async (Guid commentId, ClaimsPrincipal user, CommentService service, CancellationToken ct) =>
            Results.Ok(await service.ResolveAsync(user.GetUserId(), commentId, ct)));

        comments.MapPost("/{commentId:guid}/reopen", async (Guid commentId, ClaimsPrincipal user, CommentService service, CancellationToken ct) =>
            Results.Ok(await service.ReopenAsync(user.GetUserId(), commentId, ct)));

        return app;
    }
}
