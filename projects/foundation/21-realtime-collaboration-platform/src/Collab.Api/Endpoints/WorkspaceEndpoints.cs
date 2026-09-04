using System.Security.Claims;
using Collab.Api.Auth;
using Collab.Application.Contracts;
using Collab.Application.Services;

namespace Collab.Api.Endpoints;

/// <summary>REST surface for workspaces, membership and roles.</summary>
public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workspaces").WithTags("Workspaces").RequireAuthorization();

        group.MapPost("/", async (CreateWorkspaceRequest request, ClaimsPrincipal user, WorkspaceService service, CancellationToken ct) =>
        {
            var dto = await service.CreateAsync(user.GetUserId(), request, ct);
            return Results.Created($"/api/v1/workspaces/{dto.Id}", dto);
        });

        group.MapGet("/", async (ClaimsPrincipal user, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.ListForUserAsync(user.GetUserId(), ct)));

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(user.GetUserId(), id, ct)));

        group.MapGet("/{id:guid}/members", async (Guid id, ClaimsPrincipal user, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.ListMembersAsync(user.GetUserId(), id, ct)));

        group.MapPost("/{id:guid}/members", async (Guid id, AddMemberRequest request, ClaimsPrincipal user, WorkspaceService service, CancellationToken ct) =>
        {
            var dto = await service.AddMemberAsync(user.GetUserId(), id, request, ct);
            return Results.Created($"/api/v1/workspaces/{id}/members/{dto.UserId}", dto);
        });

        group.MapPut("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, ChangeRoleRequest request, ClaimsPrincipal user, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.ChangeRoleAsync(user.GetUserId(), id, userId, request, ct)));

        return app;
    }
}
