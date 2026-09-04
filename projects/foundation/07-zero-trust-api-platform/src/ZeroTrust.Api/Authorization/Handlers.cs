using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.Api.Authorization;

public sealed class ScopeHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        if (context.User.HasScope(requirement.Scope)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class RoleHandler : AuthorizationHandler<RoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        if (context.User.HasRole(requirement.Role)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class StepUpHandler : AuthorizationHandler<StepUpRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StepUpRequirement requirement)
    {
        var amr = context.User.Amr();
        var acr = context.User.Acr();
        if (string.Equals(amr, requirement.RequiredAmr, StringComparison.Ordinal) &&
            string.Equals(acr, requirement.RequiredAcr, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

public sealed class ClientTypeHandler : AuthorizationHandler<ClientTypeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ClientTypeRequirement requirement)
    {
        if (string.Equals(context.User.PrincipalType(), requirement.RequiredType, StringComparison.Ordinal))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class AccountOwnerHandler : AuthorizationHandler<AccountOwnerRequirement, Guid>
{
    private readonly ZeroTrustDbContext _db;
    public AccountOwnerHandler(ZeroTrustDbContext db) => _db = db;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        AccountOwnerRequirement requirement, Guid accountId)
    {
        var subject = context.User.Subject();
        if (string.IsNullOrEmpty(subject)) return;
        var account = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is not null && account.IsOwnedBy(subject))
            context.Succeed(requirement);
    }
}
