using Northstar.Secrets.Api.Auth;
using Northstar.Secrets.Api.Middleware;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Api.Endpoints;

public static class ConsumerEndpoints
{
    public sealed record RegisterConsumerRequest(
        string Name,
        string Application,
        string? WebhookUrl,
        string? Email);

    public static IEndpointRouteBuilder MapConsumerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/consumers").WithTags("Consumers");
        group.MapPost("/", RegisterAsync).RequireAuthorization(ScopePolicies.ManageSecrets);
        group.MapGet("/", ListAsync).RequireAuthorization(ScopePolicies.ManageSecrets);
        group.MapPost("/{id:guid}/subscriptions/{secretName}", LinkAsync)
            .RequireAuthorization(ScopePolicies.ManageSecrets);
        group.MapGet("/{id:guid}/pending", PendingAsync)
            .RequireAuthorization(ScopePolicies.ConsumerAcknowledge);
        group.MapPost("/{id:guid}/rotations/{rotationId:guid}/acknowledge", AcknowledgeAsync)
            .RequireAuthorization(ScopePolicies.ConsumerAcknowledge);
        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterConsumerRequest request,
        ISecretsRepository repository,
        CancellationToken cancellationToken)
    {
        var consumer = new Consumer(
            Guid.NewGuid(),
            request.Name,
            request.Application,
            request.WebhookUrl,
            request.Email);
        await repository.AddConsumerAsync(consumer, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/consumers/{consumer.Id}", new
        {
            consumer.Id,
            consumer.Name,
            consumer.Application,
            consumer.WebhookUrl,
            consumer.Email
        });
    }

    private static async Task<IResult> ListAsync(
        ISecretsRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok((await repository.ListConsumersAsync(cancellationToken)).Select(x => new
        {
            x.Id,
            x.Name,
            x.Application,
            x.WebhookUrl,
            x.Email,
            linkedSecrets = x.SecretLinks.Count
        }));

    private static async Task<IResult> LinkAsync(
        Guid id,
        string secretName,
        HttpContext httpContext,
        PathAuthorizationService pathAuthorization,
        SecretLifecycleService lifecycle,
        CancellationToken cancellationToken)
    {
        await pathAuthorization.DemandAsync(
            httpContext.User.FindFirst("sub")?.Value ?? "unknown",
            SecretNameCodec.Decode(secretName),
            SecretPermission.ManageMetadata,
            cancellationToken);
        await lifecycle.LinkConsumerAsync(
            SecretNameCodec.Decode(secretName), id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> PendingAsync(
        Guid id,
        ISecretsRepository repository,
        CancellationToken cancellationToken)
    {
        if (await repository.GetConsumerAsync(id, cancellationToken) is null)
        {
            throw new ResourceNotFoundException($"Consumer '{id}' was not found.");
        }

        var pending = await repository.ListPendingRotationsForConsumerAsync(id, cancellationToken);
        var response = new List<object>();
        foreach (var rotation in pending)
        {
            var secret = await repository.GetSecretByIdAsync(rotation.SecretId, cancellationToken);
            response.Add(new
            {
                rotation = RotationEndpoints.ToResponse(rotation),
                secretName = secret?.Name,
                secretReference = secret is not null && rotation.NewVersionNumber is not null
                    ? SecretReference.Create(secret.Name, rotation.NewVersionNumber.Value).ToString()
                    : null
            });
        }

        return Results.Ok(response);
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid id,
        Guid rotationId,
        RotationEngine engine,
        CancellationToken cancellationToken)
    {
        var rotation = await engine.AcknowledgeAsync(rotationId, id, cancellationToken);
        rotation = await engine.RunToPauseOrTerminalAsync(rotation.Id, cancellationToken);
        return Results.Ok(RotationEndpoints.ToResponse(rotation));
    }
}
