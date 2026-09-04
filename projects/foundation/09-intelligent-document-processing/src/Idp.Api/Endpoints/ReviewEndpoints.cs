using Idp.Api.Auth;
using Idp.Api.Contracts;
using Idp.Application.Review;

namespace Idp.Api.Endpoints;

public static class ReviewEndpoints
{
    public static RouteGroupBuilder MapReviewEndpoints(this RouteGroupBuilder group)
    {
        var review = group.MapGroup("/review").WithTags("Review");

        review.MapGet("/queue", GetQueueAsync)
            .RequireAuthorization(Permissions.ReviewProcess)
            .WithName("GetReviewQueue");

        review.MapPost("/{id:guid}/claim", ClaimAsync)
            .RequireAuthorization(Permissions.ReviewProcess)
            .WithName("ClaimReviewTask");

        review.MapPost("/{id:guid}/correct", CorrectAsync)
            .RequireAuthorization(Permissions.ReviewProcess)
            .WithName("CorrectReviewTask");

        review.MapPost("/{id:guid}/approve", ApproveAsync)
            .RequireAuthorization(Permissions.ReviewApprove)
            .WithName("ApproveReviewTask");

        review.MapPost("/{id:guid}/reject", RejectAsync)
            .RequireAuthorization(Permissions.ReviewApprove)
            .WithName("RejectReviewTask");

        return group;
    }

    private static async Task<IResult> GetQueueAsync(
        ReviewService review, int? limit, CancellationToken ct)
    {
        var items = await review.GetQueueAsync(Math.Clamp(limit ?? 50, 1, 200), ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> ClaimAsync(
        Guid id, HttpContext http, ClaimRequest? body, ReviewService review, CancellationToken ct)
    {
        var reviewer = Reviewer(http, body?.Reviewer);
        var result = await review.ClaimAsync(id, reviewer, ct);
        return Map(result);
    }

    private static async Task<IResult> CorrectAsync(
        Guid id, HttpContext http, CorrectRequest body, ReviewService review, CancellationToken ct)
    {
        if (body?.Corrections is null || body.Corrections.Count == 0)
            return EndpointHelpers.Problem("At least one correction is required.", 400,
                "Invalid correction");

        var reviewer = Reviewer(http, body.Reviewer);
        var corrections = body.Corrections
            .Select(c => new FieldCorrectionInput(c.FieldKey, c.NewValue, c.Reason ?? "correction"))
            .ToList();
        var result = await review.CorrectAsync(id, corrections, reviewer, ct);
        return Map(result);
    }

    private static async Task<IResult> ApproveAsync(
        Guid id, HttpContext http, ApproveRequest? body, ReviewService review, CancellationToken ct)
    {
        var reviewer = Reviewer(http, body?.Reviewer);
        var result = await review.ApproveAsync(id, reviewer, ct);
        return Map(result, okPayloadKey: "exportStatus");
    }

    private static async Task<IResult> RejectAsync(
        Guid id, HttpContext http, RejectRequest? body, ReviewService review, CancellationToken ct)
    {
        var reviewer = Reviewer(http, body?.Reviewer);
        var reason = string.IsNullOrWhiteSpace(body?.Reason) ? "Rejected by reviewer" : body!.Reason!;
        var result = await review.RejectAsync(id, reason, reviewer, ct);
        return Map(result);
    }

    private static string Reviewer(HttpContext http, string? explicitReviewer) =>
        string.IsNullOrWhiteSpace(explicitReviewer) ? http.GetActor() : explicitReviewer!;

    private static IResult Map(ReviewActionResult result, string? okPayloadKey = null) =>
        result.Status switch
        {
            ReviewActionStatus.Ok when okPayloadKey is not null =>
                Results.Ok(new Dictionary<string, string?> { [okPayloadKey] = result.Message }),
            ReviewActionStatus.Ok => Results.Ok(new { status = "ok", message = result.Message }),
            ReviewActionStatus.NotFound =>
                EndpointHelpers.Problem(result.Message ?? "Not found.", 404, "Not found"),
            ReviewActionStatus.Conflict =>
                EndpointHelpers.Problem(result.Message ?? "Conflict.", 409, "Claim conflict"),
            _ => EndpointHelpers.Problem(result.Message ?? "Invalid.", 400, "Invalid review action")
        };
}
