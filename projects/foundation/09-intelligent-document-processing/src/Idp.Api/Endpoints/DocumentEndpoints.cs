using System.Diagnostics;
using Idp.Api.Auth;
using Idp.Api.Contracts;
using Idp.Api.Observability;
using Idp.Application.Documents;
using Idp.Domain.Documents;

namespace Idp.Api.Endpoints;

public static class DocumentEndpoints
{
    public static RouteGroupBuilder MapDocumentEndpoints(this RouteGroupBuilder group)
    {
        var docs = group.MapGroup("/documents").WithTags("Documents");

        docs.MapPost("/", UploadAsync)
            .RequireAuthorization(Permissions.DocumentsSubmit)
            .DisableAntiforgery()
            .WithName("UploadDocument");

        docs.MapGet("/", ListAsync)
            .RequireAuthorization(Permissions.DocumentsSubmit)
            .WithName("ListDocuments");

        docs.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Permissions.DocumentsSubmit)
            .WithName("GetDocument");

        docs.MapGet("/{id:guid}/fields", GetFieldsAsync)
            .RequireAuthorization(Permissions.DocumentsSubmit)
            .WithName("GetDocumentFields");

        docs.MapPost("/{id:guid}/reprocess", ReprocessAsync)
            .RequireAuthorization(Permissions.DocumentsSubmit)
            .WithName("ReprocessDocument");

        return group;
    }

    private static async Task<IResult> UploadAsync(
        HttpContext http, IFormFile? file, DocumentIntakeService intake, IdpMetrics metrics,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return EndpointHelpers.Problem("A non-empty 'file' form field is required.", 400,
                "Invalid upload");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;

        var stopwatch = Stopwatch.StartNew();
        var result = await intake.IngestAsync(
            file.FileName, contentType, bytes, http.GetCorrelationId(), http.GetActor(), ct);
        stopwatch.Stop();

        switch (result.Status)
        {
            case IntakeStatus.Invalid:
                return EndpointHelpers.Problem(result.Message ?? "Invalid document.", 400,
                    "Upload rejected");
            case IntakeStatus.Duplicate:
                return Results.Conflict(new
                {
                    message = result.Message,
                    documentId = result.Document?.Id
                });
            default:
                var doc = result.Document!;
                metrics.PipelineDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
                metrics.ExtractionConfidence.Record(doc.DocumentConfidence);
                metrics.DocumentsProcessed.Add(1);
                if (doc.Routing == RoutingDecision.AutoApprove)
                    metrics.DocumentsAutoApproved.Add(1);
                return Results.Created($"/api/v1/documents/{doc.Id}", DtoMapper.ToDetail(doc));
        }
    }

    private static async Task<IResult> ListAsync(
        IDocumentRepository documents, DocumentType? type, PipelineState? state, int? page,
        int? pageSize, CancellationToken ct)
    {
        var query = new DocumentQuery(
            type, state, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, 200));
        var result = await documents.ListAsync(query, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(DtoMapper.ToSummary).ToList(),
            page = result.Page,
            pageSize = result.PageSize,
            totalCount = result.TotalCount,
            totalPages = result.TotalPages
        });
    }

    private static async Task<IResult> GetAsync(
        Guid id, IDocumentRepository documents, CancellationToken ct)
    {
        var doc = await documents.GetAsync(id, ct);
        return doc is null
            ? EndpointHelpers.Problem($"Document {id} not found.", 404, "Not found")
            : Results.Ok(DtoMapper.ToDetail(doc));
    }

    private static async Task<IResult> GetFieldsAsync(
        Guid id, IDocumentRepository documents, CancellationToken ct)
    {
        var doc = await documents.GetAsync(id, ct);
        if (doc is null)
            return EndpointHelpers.Problem($"Document {id} not found.", 404, "Not found");
        return Results.Ok(doc.Fields.OrderBy(f => f.FieldKey).Select(DtoMapper.ToField).ToList());
    }

    private static async Task<IResult> ReprocessAsync(
        Guid id, HttpContext http, DocumentIntakeService intake, CancellationToken ct)
    {
        var result = await intake.ReprocessAsync(id, http.GetActor(), ct);
        if (result is null)
            return EndpointHelpers.Problem($"Document {id} not found.", 404, "Not found");
        if (result.Status == IntakeStatus.Invalid)
            return EndpointHelpers.Problem(result.Message ?? "Cannot reprocess.", 409,
                "Reprocess rejected");
        return Results.Ok(DtoMapper.ToDetail(result.Document!));
    }
}
