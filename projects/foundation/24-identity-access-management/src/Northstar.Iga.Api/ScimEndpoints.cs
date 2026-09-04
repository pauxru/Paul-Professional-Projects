using System.Text.Json.Serialization;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Api;

public static class ScimEndpoints
{
    public static IEndpointRouteBuilder MapScimEndpoints(this IEndpointRouteBuilder app)
    {
        var scim = app.MapGroup("/scim/v2").RequireRateLimiting("api").WithTags("SCIM 2.0");
        scim.MapGet("/Users", async (
            int? startIndex,
            int? count,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            var pageSize = Math.Clamp(count ?? 50, 1, 100);
            var page = Math.Max(1, (Math.Max(1, startIndex ?? 1) - 1) / pageSize + 1);
            var users = await service.GetUsersAsync(page, pageSize, cancellationToken);
            return Results.Ok(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                totalResults = users.TotalCount,
                startIndex = (page - 1) * pageSize + 1,
                itemsPerPage = users.Items.Count,
                Resources = users.Items.Select(ToScimUser)
            });
        }).RequireAuthorization(ApiPolicies.Read);
        scim.MapGet("/Users/{id:guid}", async (
            Guid id,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            var user = await service.GetUserAsync(id, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(ToScimUser(user));
        }).RequireAuthorization(ApiPolicies.Read);
        scim.MapPost("/Users", async (
            ScimUserRequest request,
            HttpContext context,
            IClock clock,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["userName"] = ["userName and displayName are required."]
                });
            }
            var created = await service.CreateUserAsync(new CreateUserCommand(
                request.EmployeeNumber ?? $"SCIM-{Guid.NewGuid():N}"[..13],
                request.DisplayName,
                request.UserName,
                request.Department ?? "Unassigned",
                request.Title ?? "Unassigned",
                request.ManagerId,
                request.Location ?? "Unassigned",
                request.CostCentre ?? "Unassigned",
                request.EmploymentType,
                request.Clearance,
                request.StartDate ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)),
                context.Actor(),
                context.CorrelationId(),
                cancellationToken);
            if (request.Active)
            {
                await service.ProcessJoinerAsync(
                    created.Id, context.Actor(), context.CorrelationId(), cancellationToken);
            }
            return Results.Created($"/scim/v2/Users/{created.Id}", ToScimUser(created));
        }).RequireAuthorization(ApiPolicies.Admin);
        scim.MapDelete("/Users/{id:guid}", async (
            Guid id,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            await service.ProcessLeaverAsync(id, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Admin);

        scim.MapGet("/Groups", async (IIgaService service, CancellationToken cancellationToken) =>
        {
            var groups = await service.GetGroupsAsync(cancellationToken);
            return Results.Ok(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                totalResults = groups.Count,
                startIndex = 1,
                itemsPerPage = groups.Count,
                Resources = groups.Select(group => new
                {
                    schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
                    id = group.Id,
                    displayName = group.Name,
                    externalId = group.Key
                })
            });
        }).RequireAuthorization(ApiPolicies.Read);
        scim.MapPost("/Groups", async (
            ScimGroupRequest request,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateGroupAsync(
                new CreateGroupCommand(
                    request.ExternalId ?? request.DisplayName.ToLowerInvariant().Replace(' ', '-'),
                    request.DisplayName,
                    request.Description ?? "SCIM-created static group.",
                    GroupType.Static,
                    null),
                context.Actor(),
                context.CorrelationId(),
                cancellationToken);
            return Results.Created($"/scim/v2/Groups/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        return app;
    }

    private static object ToScimUser(UserIdentity user) => new
    {
        schemas = new[]
        {
            "urn:ietf:params:scim:schemas:core:2.0:User",
            "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"
        },
        id = user.Id,
        userName = user.Email,
        displayName = user.DisplayName,
        active = user.Status == IdentityStatus.Active,
        title = user.JobTitle,
        enterprise = new
        {
            employeeNumber = user.EmployeeNumber,
            department = user.Department,
            manager = user.ManagerId,
            costCenter = user.CostCentre
        }
    };
}

public sealed record ScimUserRequest(
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("displayName")] string DisplayName,
    bool Active = true,
    string? EmployeeNumber = null,
    string? Department = null,
    string? Title = null,
    Guid? ManagerId = null,
    string? Location = null,
    string? CostCentre = null,
    EmploymentType EmploymentType = EmploymentType.Employee,
    int Clearance = 1,
    DateOnly? StartDate = null);

public sealed record ScimGroupRequest(string DisplayName, string? ExternalId, string? Description);
