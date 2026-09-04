using Lakehouse.Api.Auth;
using Lakehouse.Application.Serving;

namespace Lakehouse.Api.Endpoints;

/// <summary>
/// Guarded ad-hoc SQL over the gold serving store. Every statement passes <see cref="SqlGuard"/> (single
/// read-only SELECT/WITH, no DDL/DML/PRAGMA/ATTACH/multi-statement/comments) before the engine runs it
/// read-only with a row cap and timeout. Rejections return 400 with the reason.
/// </summary>
public static class ServingEndpoints
{
    public sealed record SqlRequest(string Sql, int? MaxRows);

    public static IEndpointRouteBuilder MapServingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sql").WithTags("Serving").RequireAuthorization(AuthPolicies.Reader);

        group.MapGet("/tables", (ISqlQueryEngine engine) => Results.Ok(engine.Tables()));

        group.MapPost("/", (SqlRequest body, ISqlQueryEngine engine) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Sql))
                return Results.BadRequest(new { error = "'sql' is required." });

            try
            {
                var safe = SqlGuard.Validate(body.Sql);
                var maxRows = body.MaxRows is > 0 and <= 5000 ? body.MaxRows.Value : 1000;
                var result = engine.Query(safe, maxRows);
                return Results.Ok(result);
            }
            catch (SqlGuardException ex) { return Results.BadRequest(new { error = ex.Message, rejected = true }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }
}
