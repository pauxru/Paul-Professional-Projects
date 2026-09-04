using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Application.Ingestion;

namespace ReconEngine.Api.Endpoints;

public static class ImportsEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/imports").WithTags("Imports").RequireAuthorization();

        group.MapPost("/", async (HttpRequest request, ImportService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data with a 'file' and a 'profile'." });

            var form = await request.ReadFormAsync(ct);
            var profileName = form["profile"].ToString();
            if (string.IsNullOrWhiteSpace(profileName))
                return Results.BadRequest(new { error = "The 'profile' field is required." });

            var profile = BuiltInProfiles.ByName(profileName);
            if (profile is null)
                return Results.BadRequest(new { error = $"Unknown profile '{profileName}'. Known: {string.Join(", ", BuiltInProfiles.All.Select(p => p.Name))}." });

            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "A non-empty 'file' is required." });

            await using var stream = file.OpenReadStream();
            var result = await svc.ImportAsync(stream, profile, file.FileName, ct);
            return Results.Ok(result);
        })
        .DisableAntiforgery()
        .WithName("ImportFile")
        .WithSummary("Import an internal or external file using a named format profile.");

        group.MapGet("/{id:guid}", async (Guid id, IImportStore store, CancellationToken ct) =>
        {
            var batch = await store.GetBatchAsync(id, ct);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        })
        .WithName("GetImport");

        group.MapGet("/{id:guid}/rejections", async (Guid id, IImportStore store, CancellationToken ct) =>
        {
            var rejections = await store.GetRejectionsAsync(id, ct);
            return Results.Ok(rejections);
        })
        .WithName("GetImportRejections")
        .WithSummary("The rejected-rows report for an import, with line numbers and reasons.");

        group.MapGet("/", async (int? page, int? pageSize, IImportStore store, CancellationToken ct) =>
        {
            var (p, size) = EndpointHelpers.NormalizePaging(page, pageSize);
            var items = await store.ListBatchesAsync((p - 1) * size, size, ct);
            var total = await store.CountBatchesAsync(ct);
            return Results.Ok(new PagedResult<object>(items.Cast<object>().ToList(), p, size, total));
        })
        .WithName("ListImports");

        return app;
    }
}
