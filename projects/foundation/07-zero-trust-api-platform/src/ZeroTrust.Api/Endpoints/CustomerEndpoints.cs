using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ZeroTrust.Api.Authorization;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.Api.Endpoints;

public sealed record AccountDto(Guid Id, string AccountNumber, string Nickname, string Currency, decimal BalanceMinorUnits);

public sealed record StatementDto(Guid Id, Guid AccountId, int Year, int Month, decimal OpeningBalanceMinorUnits, decimal ClosingBalanceMinorUnits, string Currency);

public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/customer")
            .RequireAuthorization("customer.read");

        g.MapGet("/accounts", async (
            HttpContext ctx, ZeroTrustDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var subject = ctx.User.Subject() ?? "?";
            var accounts = await db.Accounts.AsNoTracking()
                .Where(a => a.OwnerSubject == subject)
                .Select(a => new AccountDto(a.Id, a.AccountNumber, a.Nickname, a.Currency, a.BalanceMinorUnits))
                .ToListAsync(ct);
            await audit.AppendAsync(AuditKind.AuthorizationAllow, subject, "list_accounts", "customer:accounts",
                Cid(ctx), Ip(ctx), Ua(ctx), $"count={accounts.Count}", true, ct);
            return Results.Ok(accounts);
        }).WithName("ListMyAccounts");

        g.MapGet("/accounts/{id:guid}", async (
            Guid id, HttpContext ctx, IAuthorizationService authz, ZeroTrustDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var subject = ctx.User.Subject() ?? "?";
            var ownership = await authz.AuthorizeAsync(ctx.User, id, new AccountOwnerRequirement());
            if (!ownership.Succeeded)
            {
                await audit.AppendAsync(AuditKind.AuthorizationDeny, subject, "read_account", $"account:{id}",
                    Cid(ctx), Ip(ctx), Ua(ctx), "ownership_denied", false, ct);
                return Results.Problem(title: "forbidden", detail: "account is not owned by principal", statusCode: 403);
            }
            var acct = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (acct is null) return Results.NotFound();
            await audit.AppendAsync(AuditKind.AuthorizationAllow, subject, "read_account", $"account:{id}",
                Cid(ctx), Ip(ctx), Ua(ctx), "ok", true, ct);
            return Results.Ok(new AccountDto(acct.Id, acct.AccountNumber, acct.Nickname, acct.Currency, acct.BalanceMinorUnits));
        }).WithName("GetMyAccount");

        g.MapGet("/accounts/{id:guid}/statements", async (
            Guid id, HttpContext ctx, IAuthorizationService authz, ZeroTrustDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var ownership = await authz.AuthorizeAsync(ctx.User, id, new AccountOwnerRequirement());
            if (!ownership.Succeeded)
            {
                await audit.AppendAsync(AuditKind.AuthorizationDeny, ctx.User.Subject() ?? "?", "list_statements",
                    $"account:{id}", Cid(ctx), Ip(ctx), Ua(ctx), "ownership_denied", false, ct);
                return Results.Problem(title: "forbidden", detail: "account is not owned by principal", statusCode: 403);
            }
            var statements = await db.Statements.AsNoTracking().Where(s => s.AccountId == id)
                .Select(s => new StatementDto(s.Id, s.AccountId, s.Year, s.Month,
                    s.OpeningBalanceMinorUnits, s.ClosingBalanceMinorUnits, s.Currency))
                .ToListAsync(ct);
            return Results.Ok(statements);
        }).WithName("ListStatements");

        g.MapGet("/me", (HttpContext ctx) =>
        {
            return Results.Ok(new
            {
                subject = ctx.User.Subject(),
                scopes = ctx.User.Scopes().ToArray(),
                roles = ctx.User.FindAll("roles").Select(c => c.Value).ToArray(),
                type = ctx.User.PrincipalType(),
            });
        }).WithName("GetMe");

        return app;
    }

    private static string Cid(HttpContext ctx) => ctx.Items[Middleware.CorrelationIdMiddleware.HeaderName]?.ToString() ?? "-";
    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "-";
    private static string Ua(HttpContext ctx) => ctx.Request.Headers.UserAgent.ToString();
}
