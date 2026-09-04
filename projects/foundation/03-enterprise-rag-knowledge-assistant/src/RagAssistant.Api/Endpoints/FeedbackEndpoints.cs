using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RagAssistant.Api.Auth;
using RagAssistant.Api.Contracts;
using RagAssistant.Application.Feedback;
using RagAssistant.Domain.Feedback;

namespace RagAssistant.Api.Endpoints;

public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/feedback").WithTags("Feedback").RequireAuthorization();
        group.MapPost("/", SubmitAsync).WithName("SubmitFeedback");
        group.MapGet("/", ListAsync).WithName("ListFeedback");
        return app;
    }

    private static async Task<Results<Created<FeedbackRecord>, ValidationProblem>> SubmitAsync(
        [FromBody] FeedbackRequestDto request,
        HttpContext http,
        FeedbackService service,
        IUserPrincipalAccessor accessor,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query) || string.IsNullOrWhiteSpace(request.Answer))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["Query and answer are required."],
            });
        }

        var user = accessor.Get(http);
        var rating = request.Rating switch
        {
            > 0 => FeedbackRating.ThumbsUp,
            < 0 => FeedbackRating.ThumbsDown,
            _ => FeedbackRating.Neutral,
        };

        var record = await service.SubmitAsync(
            new FeedbackSubmission(user.UserId, request.Query, request.Answer, rating, request.Reason ?? string.Empty, request.PromptVersion, request.CitedChunkIds ?? []),
            ct).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/feedback/{record.Id}", record);
    }

    private static async Task<Ok<IReadOnlyList<FeedbackRecord>>> ListAsync(
        FeedbackService service,
        int? limit,
        CancellationToken ct)
    {
        var records = await service.ListRecentAsync(limit ?? 50, ct).ConfigureAwait(false);
        return TypedResults.Ok(records);
    }
}
