using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RagAssistant.Api.Auth;
using RagAssistant.Api.Contracts;
using RagAssistant.Application.Answering;
using RagAssistant.Application.Chat;
using RagAssistant.Application.Cost;

namespace RagAssistant.Api.Endpoints;

public static class QueryEndpoints
{
    public static IEndpointRouteBuilder MapQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/query").WithTags("Query").RequireAuthorization();
        group.MapPost("/", QueryAsync).WithName("Query");
        return app;
    }

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/chat").WithTags("Chat").RequireAuthorization();
        group.MapPost("/sessions", ChatAsync).WithName("ChatTurn");
        return app;
    }

    private static async Task<Results<Ok<QueryResponse>, ValidationProblem, ProblemHttpResult>> QueryAsync(
        [FromBody] QueryRequest request,
        HttpContext http,
        AnsweringService answering,
        IBudgetGuard budget,
        IUserPrincipalAccessor accessor,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(QueryRequest.Query)] = ["Query is required."],
            });
        }

        try
        {
            await budget.EnsureAllowedAsync(request.Tenant ?? "default", DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        catch (BudgetExceededException ex)
        {
            return TypedResults.Problem(
                title: "Budget exceeded",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var user = accessor.Get(http);
        var mode = ModeParsing.Parse(request.Mode);
        var answer = await answering.AnswerAsync(
            new AnswerRequest(request.Query, user, mode, Math.Clamp(request.TopK, 1, 16), request.Tenant ?? "default"),
            ct).ConfigureAwait(false);

        return TypedResults.Ok(ToResponse(answer));
    }

    private static async Task<Results<Ok<ChatResponse>, ValidationProblem, ProblemHttpResult>> ChatAsync(
        [FromBody] ChatRequestDto request,
        HttpContext http,
        ChatOrchestrator orchestrator,
        IUserPrincipalAccessor accessor,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(ChatRequestDto.Query)] = ["Query is required."],
            });
        }

        var user = accessor.Get(http);
        try
        {
            var result = await orchestrator.HandleAsync(
                new ChatTurnRequest(request.SessionId, request.Query, user, ModeParsing.Parse(request.Mode), Math.Clamp(request.TopK, 1, 16), request.Tenant ?? "default"),
                ct).ConfigureAwait(false);
            return TypedResults.Ok(new ChatResponse(result.SessionId, result.RewrittenQuery, ToResponse(result.Answer)));
        }
        catch (BudgetExceededException ex)
        {
            return TypedResults.Problem(
                title: "Budget exceeded",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
        }
    }

    internal static QueryResponse ToResponse(AnswerResult answer) => new(
        answer.Answer,
        answer.Refused,
        answer.RefusalReason,
        answer.SupportRatio,
        answer.Citations.Select(c => new CitationResponse(c.DocumentId, c.DocumentTitle, c.ChunkId, c.Sequence, c.StartChar, c.EndChar, c.Score)).ToArray(),
        answer.Mode.ToString(),
        answer.PromptVersion,
        answer.PromptTokens,
        answer.CompletionTokens);
}
