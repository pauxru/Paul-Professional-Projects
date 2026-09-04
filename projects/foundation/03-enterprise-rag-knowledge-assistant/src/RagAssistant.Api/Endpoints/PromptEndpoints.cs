using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RagAssistant.Api.Contracts;
using RagAssistant.Application.Prompts;
using RagAssistant.Domain.Prompts;

namespace RagAssistant.Api.Endpoints;

public static class PromptEndpoints
{
    public static IEndpointRouteBuilder MapPromptEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/prompts").WithTags("Prompts").RequireAuthorization("KnowledgeAdmin");
        group.MapPost("/", RegisterAsync).WithName("RegisterPrompt");
        group.MapGet("/", ListAsync).WithName("ListPrompts");
        return app;
    }

    private static async Task<Results<Created<PromptResponse>, ValidationProblem, Conflict<string>>> RegisterAsync(
        [FromBody] PromptRequestDto request,
        PromptService prompts,
        CancellationToken ct)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Version)
            || string.IsNullOrWhiteSpace(request.Body))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["Name, Version and Body are required."],
            });
        }

        try
        {
            var template = await prompts.RegisterAsync(
                new RegisterPromptRequest(request.Name, request.Version, request.Body),
                ct).ConfigureAwait(false);
            return TypedResults.Created($"/api/v1/admin/prompts/{template.Id}", ToResponse(template));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }
    }

    private static async Task<Ok<IReadOnlyList<PromptResponse>>> ListAsync(
        PromptService prompts,
        CancellationToken ct)
    {
        var records = await prompts.ListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<PromptResponse>>(records.Select(ToResponse).ToArray());
    }

    private static PromptResponse ToResponse(PromptTemplate template) =>
        new(template.Id, template.Name, template.Version, template.Hash, template.IsActive, template.CreatedAt);
}
