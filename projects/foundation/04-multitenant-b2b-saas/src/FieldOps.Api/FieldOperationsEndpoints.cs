using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using FieldOps.Application;
using FieldOps.Domain;
using FieldOps.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Api;

public sealed record CreateJobRequest(
    string Title,
    string? Description,
    JobPriority Priority,
    DateTimeOffset ScheduleStart,
    DateTimeOffset ScheduleEnd,
    DateTimeOffset SlaDueAt,
    Guid? AssetId);
public sealed record TransitionJobRequest(JobStatus Status);
public sealed record AssignJobRequest(Guid MembershipId);
public sealed record CreateAssetRequest(string AssetTag, string Name, string Category, string Location, DateOnly? NextMaintenanceDate);
public sealed record UploadAttachmentRequest(string FileName, string ContentType, string Base64Content);
public sealed record InspectionItemRequest(
    string Prompt,
    InspectionItemType Type,
    bool Required,
    decimal Weight,
    decimal? MinValue,
    decimal? MaxValue,
    string? ExpectedText);
public sealed record CreateInspectionTemplateRequest(string Name, decimal PassingScore, IReadOnlyList<InspectionItemRequest> Items);
public sealed record SubmitInspectionRequest(Guid JobId, IReadOnlyList<InspectionAnswerInput> Answers);
public sealed record CreateTeamRequest(string Name);
public sealed record AddTeamMemberRequest(Guid UserId);
public sealed record CreateInvitationRequest(string Email, MemberRole Role, int LifetimeHours = 72);
public sealed record AcceptInvitationRequest(string Token, Guid UserId);
public sealed record ChangeRoleRequest(MemberRole Role);
public sealed record PageResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public static class FieldOperationsEndpoints
{
    public static IEndpointRouteBuilder MapFieldOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        MapJobs(app);
        MapAssets(app);
        MapInspections(app);
        MapMembers(app);
        return app;
    }

    private static void MapJobs(IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/v1/jobs").WithTags("Jobs").RequireAuthorization();

        jobs.MapGet("/", async (
            int? page,
            int? pageSize,
            JobStatus? status,
            string? sort,
            IJobRepository repository,
            CancellationToken cancellationToken) =>
        {
            var paging = Paging.Normalize(page, pageSize);
            var total = await repository.CountAsync(status, cancellationToken);
            var items = await repository.ListAsync((paging.Page - 1) * paging.PageSize, paging.PageSize, status, sort, cancellationToken);
            return Results.Ok(new PageResponse<Job>(
                items, paging.Page, paging.PageSize, total, Paging.TotalPages(total, paging.PageSize)));
        });

        jobs.MapGet("/{id:guid}", async (Guid id, IJobRepository repository, CancellationToken cancellationToken) =>
        {
            var job = await repository.FindAsync(id, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        jobs.MapPost("/", async (
            CreateJobRequest request,
            ITenantContext tenant,
            IOrganizationRepository organizations,
            IAssetRepository assets,
            IEntitlementService entitlements,
            UsageQuotaService quota,
            JobApplicationService service,
            ClaimsPrincipal principal,
            HttpContext context,
            IAuditWriter audit,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["Title is required."]
                });
            }

            var organization = await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.");
            var plan = entitlements.GetPlan(organization.Plan);
            entitlements.EnsureFeature(organization.Plan, "jobs");
            await quota.IncrementMonthlyAsync(
                tenant.RequiredTenantId, "jobs-created", 1,
                (long)Math.Floor(plan.MaxJobsPerMonth * 0.8m), plan.MaxJobsPerMonth, false, cancellationToken);
            var asset = request.AssetId.HasValue
                ? await assets.FindAsync(request.AssetId.Value, cancellationToken)
                : null;
            if (request.AssetId.HasValue && asset is null) throw new KeyNotFoundException("Asset was not found.");
            var job = await service.CreateAsync(
                request.Title,
                request.Description ?? string.Empty,
                request.Priority,
                request.ScheduleStart,
                request.ScheduleEnd,
                request.SlaDueAt,
                asset,
                cancellationToken);
            await audit.WriteAsync(
                ActorId(principal),
                "job.created",
                $"job/{job.Id}",
                null,
                new { job.Title, job.Priority, job.Status, job.AssetId },
                context.TraceIdentifier,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.FirstOrDefault(),
                cancellationToken);
            return Results.Created($"/api/v1/jobs/{job.Id}", job);
        }).RequireAuthorization(Permissions.JobsCreate);

        jobs.MapPut("/{id:guid}/status", async (
            Guid id,
            TransitionJobRequest request,
            JobApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.TransitionAsync(id, request.Status, cancellationToken)))
            .RequireAuthorization(Permissions.JobsComplete);

        jobs.MapPut("/{id:guid}/assignee", async (
            Guid id,
            AssignJobRequest request,
            IMembershipRepository memberships,
            JobApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var membership = await memberships.FindAsync(request.MembershipId, cancellationToken)
                ?? throw new KeyNotFoundException("Membership was not found.");
            return Results.Ok(await service.AssignAsync(id, membership, cancellationToken));
        }).RequireAuthorization(Permissions.JobsAssign);

        jobs.MapPost("/{id:guid}/attachments", async (
            Guid id,
            UploadAttachmentRequest request,
            ITenantContext tenant,
            IJobRepository repository,
            IObjectStore objectStore,
            FieldOpsDbContext db,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var job = await repository.FindAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException("Job was not found.");
            TenantGuard.EnsureSameTenant(tenant.RequiredTenantId, job);
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(request.Base64Content);
            }
            catch (FormatException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["base64Content"] = ["Content must be valid base64."]
                });
            }

            await using var stream = new MemoryStream(bytes);
            var stored = await objectStore.PutAsync(
                request.FileName, request.ContentType, stream, bytes.LongLength, cancellationToken);
            var attachment = new JobAttachment(
                tenant.RequiredTenantId, job.Id, stored.ObjectKey, stored.FileName, stored.ContentType, stored.Length, clock.UtcNow);
            await db.JobAttachments.AddAsync(attachment, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/v1/jobs/{id}/attachments/{attachment.Id}", attachment);
        }).RequireAuthorization(Permissions.JobsCreate);
    }

    private static void MapAssets(IEndpointRouteBuilder app)
    {
        var assets = app.MapGroup("/api/v1/assets").WithTags("Assets").RequireAuthorization();
        assets.MapGet("/", async (
            int? page,
            int? pageSize,
            IAssetRepository repository,
            CancellationToken cancellationToken) =>
        {
            var paging = Paging.Normalize(page, pageSize);
            var total = await repository.CountAsync(cancellationToken);
            var items = await repository.ListAsync((paging.Page - 1) * paging.PageSize, paging.PageSize, cancellationToken);
            return Results.Ok(new PageResponse<Asset>(
                items, paging.Page, paging.PageSize, total, Paging.TotalPages(total, paging.PageSize)));
        });

        assets.MapPost("/", async (
            CreateAssetRequest request,
            ITenantContext tenant,
            IOrganizationRepository organizations,
            IAssetRepository repository,
            IEntitlementService entitlements,
            ClaimsPrincipal principal,
            HttpContext context,
            IAuditWriter audit,
            CancellationToken cancellationToken) =>
        {
            var organization = await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.");
            var current = await repository.CountAsync(cancellationToken);
            entitlements.EnsureLimit("assets", current, 1, entitlements.GetPlan(organization.Plan).MaxAssets);
            var asset = new Asset(
                tenant.RequiredTenantId,
                request.AssetTag,
                request.Name,
                request.Category,
                request.Location,
                request.NextMaintenanceDate);
            await repository.AddAsync(asset, cancellationToken);
            await repository.SaveAsync(cancellationToken);
            await audit.WriteAsync(
                ActorId(principal),
                "asset.created",
                $"asset/{asset.Id}",
                null,
                new { asset.AssetTag, asset.Name, asset.Category, asset.Location },
                context.TraceIdentifier,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.FirstOrDefault(),
                cancellationToken);
            return Results.Created($"/api/v1/assets/{asset.Id}", asset);
        }).RequireAuthorization(Permissions.AssetsManage);
    }

    private static void MapInspections(IEndpointRouteBuilder app)
    {
        var inspections = app.MapGroup("/api/v1/inspections").WithTags("Inspections").RequireAuthorization();
        inspections.MapPost("/templates", async (
            CreateInspectionTemplateRequest request,
            ITenantContext tenant,
            IOrganizationRepository organizations,
            IEntitlementService entitlements,
            IInspectionRepository repository,
            CancellationToken cancellationToken) =>
        {
            var organization = await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.");
            entitlements.EnsureFeature(organization.Plan, "inspections");
            var template = new InspectionTemplate(tenant.RequiredTenantId, request.Name, request.PassingScore);
            foreach (var item in request.Items)
            {
                template.AddItem(item.Prompt, item.Type, item.Required, item.Weight, item.MinValue, item.MaxValue, item.ExpectedText);
            }

            await repository.AddTemplateAsync(template, cancellationToken);
            await repository.SaveAsync(cancellationToken);
            return Results.Created($"/api/v1/inspections/templates/{template.Id}", template);
        }).RequireAuthorization(Permissions.AssetsManage);

        inspections.MapPost("/templates/{templateId:guid}/submissions", async (
            Guid templateId,
            SubmitInspectionRequest request,
            ClaimsPrincipal principal,
            IInspectionRepository repository,
            InspectionService service,
            CancellationToken cancellationToken) =>
        {
            var template = await repository.FindTemplateAsync(templateId, cancellationToken)
                ?? throw new KeyNotFoundException("Inspection template was not found.");
            var userId = Guid.Parse(
                principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
            var submission = await service.SubmitAsync(template, request.JobId, userId, request.Answers, cancellationToken);
            return Results.Created($"/api/v1/inspections/submissions/{submission.Id}", submission);
        }).RequireAuthorization(Permissions.InspectionsSubmit);
    }

    private static void MapMembers(IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/v1/teams").WithTags("Teams").RequireAuthorization(Permissions.MembersManage);
        teams.MapPost("/", async (
            CreateTeamRequest request,
            ITenantContext tenant,
            FieldOpsDbContext db,
            CancellationToken cancellationToken) =>
        {
            var team = new Team(tenant.RequiredTenantId, request.Name);
            await db.Teams.AddAsync(team, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/v1/teams/{team.Id}", team);
        });

        teams.MapPost("/{teamId:guid}/members", async (
            Guid teamId,
            AddTeamMemberRequest request,
            ITenantContext tenant,
            FieldOpsDbContext db,
            CancellationToken cancellationToken) =>
        {
            var team = await db.Teams.SingleOrDefaultAsync(x => x.Id == teamId, cancellationToken)
                ?? throw new KeyNotFoundException("Team was not found.");
            var membership = await db.Memberships.SingleOrDefaultAsync(
                x => x.UserId == request.UserId && x.IsActive,
                cancellationToken) ?? throw new KeyNotFoundException("Tenant membership was not found.");
            TenantGuard.EnsureSameTenant(tenant.RequiredTenantId, team, membership);
            var teamMembership = new TeamMembership(tenant.RequiredTenantId, team.Id, request.UserId);
            await db.TeamMemberships.AddAsync(teamMembership, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/v1/teams/{teamId}/members/{teamMembership.Id}", teamMembership);
        });

        var invitations = app.MapGroup("/api/v1/invitations").WithTags("Invitations");
        invitations.MapPost("/", async (
            CreateInvitationRequest request,
            InvitationService service,
            ITenantContext tenant,
            IOrganizationRepository organizations,
            IMembershipRepository memberships,
            IEntitlementService entitlements,
            ClaimsPrincipal principal,
            HttpContext context,
            IAuditWriter audit,
            CancellationToken cancellationToken) =>
        {
            var organization = await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.");
            entitlements.EnsureLimit(
                "users",
                await memberships.CountActiveAsync(cancellationToken),
                1,
                entitlements.GetPlan(organization.Plan).MaxUsers);
            var created = await service.CreateAsync(
                request.Email, request.Role, TimeSpan.FromHours(Math.Clamp(request.LifetimeHours, 1, 168)), cancellationToken);
            await audit.WriteAsync(
                ActorId(principal),
                "invitation.created",
                $"invitation/{created.Invitation.Id}",
                null,
                new { created.Invitation.Email, created.Invitation.Role, created.Invitation.ExpiresAt },
                context.TraceIdentifier,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.FirstOrDefault(),
                cancellationToken);
            return Results.Created($"/api/v1/invitations/{created.Invitation.Id}", new
            {
                created.Invitation.Id,
                created.Invitation.Email,
                created.Invitation.Role,
                created.Invitation.ExpiresAt,
                token = created.Token
            });
        }).RequireAuthorization(Permissions.MembersManage);

        invitations.MapPost("/accept", async (
            AcceptInvitationRequest request,
            InvitationService service,
            ITenantContext tenant,
            IOrganizationRepository organizations,
            IMembershipRepository memberships,
            IEntitlementService entitlements,
            CancellationToken cancellationToken) =>
        {
            var organization = await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.");
            entitlements.EnsureLimit(
                "users",
                await memberships.CountActiveAsync(cancellationToken),
                1,
                entitlements.GetPlan(organization.Plan).MaxUsers);
            return Results.Ok(await service.AcceptAsync(request.Token, request.UserId, cancellationToken));
        })
            .AllowAnonymous();

        app.MapPut("/api/v1/memberships/{id:guid}/role", async (
            Guid id,
            ChangeRoleRequest request,
            IPermissionService service,
            ClaimsPrincipal principal,
            HttpContext context,
            IAuditWriter audit,
            CancellationToken cancellationToken) =>
        {
            await service.ChangeRoleAsync(id, request.Role, cancellationToken);
            await audit.WriteAsync(
                ActorId(principal),
                "membership.role-changed",
                $"membership/{id}",
                null,
                new { request.Role },
                context.TraceIdentifier,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.FirstOrDefault(),
                cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.MembersManage).WithTags("Members");
    }

    private static string ActorId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? "unknown";
}

public static class Paging
{
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, 100));

    public static int TotalPages(int total, int pageSize) =>
        total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
}
