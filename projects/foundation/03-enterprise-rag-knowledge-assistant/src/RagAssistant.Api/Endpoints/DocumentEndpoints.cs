using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RagAssistant.Api.Auth;
using RagAssistant.Api.Contracts;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Chunking;
using RagAssistant.Application.Ingestion;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Api.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/documents").WithTags("Documents").RequireAuthorization();

        group.MapPost("/", IngestAsync).WithName("IngestDocument");
        group.MapGet("/", ListAsync).WithName("ListDocuments");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetDocument");
        group.MapPost("/{id:guid}/reindex", ReindexAsync).WithName("ReindexDocument");
        group.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteDocument").RequireAuthorization("KnowledgeAdmin");

        return app;
    }

    private static async Task<Results<Created<DocumentResponse>, Ok<DocumentResponse>, ValidationProblem>> IngestAsync(
        [FromBody] IngestDocumentRequest request,
        HttpContext httpContext,
        IngestionService ingestion,
        IDocumentRepository repository,
        IUserPrincipalAccessor accessor,
        CancellationToken ct)
    {
        var validation = Validate(request);
        if (validation is not null)
        {
            return TypedResults.ValidationProblem(validation);
        }

        var user = accessor.Get(httpContext);
        if (!Enum.TryParse<Classification>(request.Classification, ignoreCase: true, out var classification))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["classification"] = ["Unknown classification."],
            });
        }

        if (classification > user.MaxClassification)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["classification"] = ["Cannot ingest above your own classification level."],
            });
        }

        var acl = new AccessControlList(request.Roles ?? [], request.Departments ?? [], classification);
        var strategy = ParseStrategy(request.Strategy);
        var result = await ingestion.IngestAsync(
            new IngestionRequest(request.Title, request.Source, request.Content, acl, strategy),
            ct).ConfigureAwait(false);

        var document = await repository.GetByIdAsync(result.DocumentId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Ingested document missing.");

        var response = ToResponse(document);
        return result.ReusedExisting
            ? TypedResults.Ok(response)
            : TypedResults.Created($"/api/v1/documents/{document.Id}", response);
    }

    private static async Task<Ok<IReadOnlyList<DocumentResponse>>> ListAsync(
        HttpContext httpContext,
        IDocumentRepository repository,
        IUserPrincipalAccessor accessor,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var user = accessor.Get(httpContext);
        var skip = ((page ?? 1) - 1) * (pageSize ?? 25);
        var docs = await repository.ListForUserAsync(user, Math.Max(0, skip), Math.Min(100, pageSize ?? 25), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DocumentResponse>>(docs.Select(ToResponse).ToArray());
    }

    private static async Task<Results<Ok<DocumentResponse>, NotFound, ForbidHttpResult>> GetAsync(
        Guid id,
        HttpContext httpContext,
        IDocumentRepository repository,
        IUserPrincipalAccessor accessor,
        CancellationToken ct)
    {
        var document = await repository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (document is null)
        {
            return TypedResults.NotFound();
        }

        var user = accessor.Get(httpContext);
        if (!document.Acl.Allows(user))
        {
            return TypedResults.Forbid();
        }

        return TypedResults.Ok(ToResponse(document));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> ReindexAsync(
        Guid id,
        [FromQuery] string? strategy,
        HttpContext httpContext,
        IngestionService ingestion,
        IDocumentRepository repository,
        IUserPrincipalAccessor accessor,
        CancellationToken ct)
    {
        var document = await repository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (document is null)
        {
            return TypedResults.NotFound();
        }

        var user = accessor.Get(httpContext);
        if (!document.Acl.Allows(user))
        {
            return TypedResults.Forbid();
        }

        await ingestion.ReindexAsync(id, ParseStrategy(strategy), ct).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id,
        IngestionService ingestion,
        IDocumentRepository repository,
        CancellationToken ct)
    {
        var document = await repository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (document is null)
        {
            return TypedResults.NotFound();
        }

        await ingestion.DeleteAsync(id, ct).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static Dictionary<string, string[]>? Validate(IngestDocumentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["request"] = ["Request body is required."];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors[nameof(request.Title)] = ["Title is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Source))
        {
            errors[nameof(request.Source)] = ["Source is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            errors[nameof(request.Content)] = ["Content is required."];
        }

        return errors.Count == 0 ? null : errors;
    }

    private static ChunkingStrategy ParseStrategy(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy))
        {
            return ChunkingStrategy.SentenceAware;
        }

        return strategy.Trim().ToLowerInvariant() switch
        {
            "fixed" or "fixedsize" => ChunkingStrategy.FixedSize,
            "sentence" or "sentenceaware" => ChunkingStrategy.SentenceAware,
            _ => throw new ArgumentException($"Unknown chunking strategy '{strategy}'."),
        };
    }

    private static DocumentResponse ToResponse(Document document) => new(
        document.Id,
        document.Title,
        document.Source,
        document.Acl.Classification.ToString(),
        document.Acl.Roles.ToArray(),
        document.Acl.Departments.ToArray(),
        document.Chunks.Count,
        document.Version,
        document.UpdatedAt);
}
