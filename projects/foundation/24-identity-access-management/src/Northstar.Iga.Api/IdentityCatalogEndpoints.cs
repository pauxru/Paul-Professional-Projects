using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Api;

public static class IdentityCatalogEndpoints
{
    public static IEndpointRouteBuilder MapIdentityCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/v1/users").RequireRateLimiting("api").WithTags("Identities");
        users.MapGet("/", async (int? page, int? pageSize, IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetUsersAsync(page ?? 1, pageSize ?? 25, cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        users.MapGet("/{id:guid}", async (Guid id, IIgaService service, CancellationToken cancellationToken) =>
        {
            var user = await service.GetUserAsync(id, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }).RequireAuthorization(ApiPolicies.Read);
        users.MapPost("/", async (
            CreateUserCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.EmployeeNumber) ||
                string.IsNullOrWhiteSpace(command.DisplayName) ||
                string.IsNullOrWhiteSpace(command.Email))
            {
                return ValidationResults.Required(
                    ("employeeNumber", command.EmployeeNumber),
                    ("displayName", command.DisplayName),
                    ("email", command.Email));
            }
            var created = await service.CreateUserAsync(
                command,
                context.Actor(),
                context.CorrelationId(),
                cancellationToken);
            return Results.Created($"/api/v1/users/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        users.MapPost("/{id:guid}/joiner", async (
            Guid id,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ProcessJoinerAsync(
                id, context.Actor(), context.CorrelationId(), cancellationToken)))
            .RequireAuthorization(ApiPolicies.Admin);
        users.MapPost("/{id:guid}/mover", async (
            Guid id,
            MoveUserCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ProcessMoverAsync(
                id, command, context.Actor(), context.CorrelationId(), cancellationToken)))
            .RequireAuthorization(ApiPolicies.Admin);
        users.MapPost("/{id:guid}/leaver", async (
            Guid id,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ProcessLeaverAsync(
                id, context.Actor(), context.CorrelationId(), cancellationToken)))
            .RequireAuthorization(ApiPolicies.Admin);
        users.MapGet("/{id:guid}/access-profile", async (
            Guid id,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetUserAccessProfileAsync(id, cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);

        var applications = app.MapGroup("/api/v1/applications").RequireRateLimiting("api").WithTags("Applications");
        applications.MapGet("/", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetApplicationsAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        applications.MapPost("/", async (
            CreateApplicationCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Key) || string.IsNullOrWhiteSpace(command.Name))
            {
                return ValidationResults.Required(("key", command.Key), ("name", command.Name));
            }
            var created = await service.CreateApplicationAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/applications/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);

        var entitlements = app.MapGroup("/api/v1/entitlements").RequireRateLimiting("api").WithTags("Entitlements");
        entitlements.MapGet("/", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetEntitlementsAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        entitlements.MapPost("/", async (
            CreateEntitlementCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Key) ||
                string.IsNullOrWhiteSpace(command.Permission) ||
                string.IsNullOrWhiteSpace(command.BusinessDescription))
            {
                return ValidationResults.Required(
                    ("key", command.Key),
                    ("permission", command.Permission),
                    ("businessDescription", command.BusinessDescription));
            }
            var created = await service.CreateEntitlementAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/entitlements/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        entitlements.MapPost("/{entitlementId:guid}/grant/{userId:guid}", async (
            Guid entitlementId,
            Guid userId,
            DirectGrantRequest request,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            await service.GrantEntitlementAsync(
                userId,
                entitlementId,
                GrantSource.Direct,
                request.Reason,
                request.ExpiresAt,
                context.Actor(),
                context.CorrelationId(),
                cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Admin);

        var roles = app.MapGroup("/api/v1/roles").RequireRateLimiting("api").WithTags("Roles");
        roles.MapGet("/", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetRolesAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        roles.MapPost("/", async (
            CreateRoleCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Key) || string.IsNullOrWhiteSpace(command.Name))
            {
                return ValidationResults.Required(("key", command.Key), ("name", command.Name));
            }
            var created = await service.CreateRoleAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/roles/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        roles.MapPost("/{roleId:guid}/entitlements/{entitlementId:guid}", async (
            Guid roleId,
            Guid entitlementId,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            await service.AddRoleEntitlementAsync(
                roleId, entitlementId, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Admin);
        roles.MapPost("/{roleId:guid}/inherits/{inheritedRoleId:guid}", async (
            Guid roleId,
            Guid inheritedRoleId,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            await service.AddRoleInheritanceAsync(
                roleId, inheritedRoleId, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Admin);
        roles.MapPost("/{roleId:guid}/grant/{userId:guid}", async (
            Guid roleId,
            Guid userId,
            DirectGrantRequest request,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            await service.GrantRoleAsync(
                userId,
                roleId,
                GrantSource.Direct,
                request.Reason,
                request.ExpiresAt,
                context.Actor(),
                context.CorrelationId(),
                cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Admin);

        var groups = app.MapGroup("/api/v1/groups").RequireRateLimiting("api").WithTags("Groups");
        groups.MapGet("/", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetGroupsAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        groups.MapPost("/", async (
            CreateGroupCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Key) || string.IsNullOrWhiteSpace(command.Name))
            {
                return ValidationResults.Required(("key", command.Key), ("name", command.Name));
            }
            var created = await service.CreateGroupAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/groups/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        groups.MapPost("/{groupId:guid}/members/{userId:guid}", async (
            Guid groupId,
            Guid userId,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            await service.AddGroupMemberAsync(
                groupId, userId, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Admin);
        groups.MapPost("/{groupId:guid}/roles/{roleId:guid}", async (
            Guid groupId,
            Guid roleId,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            await service.AddGroupRoleAsync(
                groupId, roleId, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Admin);
        groups.MapPost("/reevaluate/{userId:guid}", async (
            Guid userId,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            await service.ReevaluateDynamicGroupsAsync(
                userId, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Admin);

        return app;
    }
}

public sealed record DirectGrantRequest(string Reason, DateTimeOffset? ExpiresAt);
