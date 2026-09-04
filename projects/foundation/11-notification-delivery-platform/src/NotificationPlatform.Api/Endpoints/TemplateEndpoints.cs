namespace NotificationPlatform.Api.Endpoints;

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Application.Templates;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Persistence;

public static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/templates").WithTags("Templates").RequireAuthorization(Policies.ManageTemplates);

        group.MapPost("/", async ([FromBody] CreateTemplateRequest request, ClaimsPrincipal user, ITemplateService service, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            try
            {
                var dto = await service.CreateAsync(tenantId.Value, request, ct);
                return Results.Created($"/api/v1/templates/{dto.Id}", dto);
            }
            catch (DomainException ex)
            {
                return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        group.MapGet("/", async (ClaimsPrincipal user, ITemplateService service, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            var list = await service.ListAsync(tenantId.Value, ct);
            return Results.Ok(new { items = list });
        });

        group.MapPost("/preview", async ([FromBody] TemplatePreviewRequest request, ClaimsPrincipal user, ITemplateService service, IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null) return Results.NotFound();
            try
            {
                var preview = await service.PreviewAsync(tenantId.Value, tenant.DefaultLocale, request, ct);
                return Results.Ok(preview);
            }
            catch (DomainException ex)
            {
                return Results.Problem(title: ex.Message, statusCode: 422);
            }
        });

        return app;
    }
}
