using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Api.Services;

public sealed class DemoSeeder(
    ISecretsRepository repository,
    SecretLifecycleService lifecycle)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await repository.AnySecretsAsync(cancellationToken))
        {
            return;
        }

        await repository.AddAccessPolicyAsync(
            new AccessPolicy(
                Guid.NewGuid(),
                "demo-admin",
                "**",
                true,
                true,
                true,
                true),
            cancellationToken);
        await repository.AddAccessPolicyAsync(
            new AccessPolicy(
                Guid.NewGuid(),
                "demo-reader",
                "synthetic-app-*/prod/*",
                false,
                true,
                false,
                false),
            cancellationToken);

        var consumers = new List<Consumer>();
        for (var index = 1; index <= 40; index++)
        {
            var consumer = new Consumer(
                Guid.NewGuid(),
                $"consumer-{index:00}",
                $"synthetic-app-{index:00}",
                $"https://demo-webhook.invalid/consumer/{index:00}",
                $"consumer-{index:00}@example.invalid");
            consumers.Add(consumer);
            await repository.AddConsumerAsync(consumer, cancellationToken);
        }

        await repository.SaveChangesAsync(cancellationToken);

        var types = Enum.GetValues<SecretType>();
        for (var index = 1; index <= 40; index++)
        {
            var type = types[(index - 1) % types.Length];
            var environment = (index % 3) switch
            {
                0 => "prod",
                1 => "dev",
                _ => "stage"
            };
            var purpose = type.ToString().ToLowerInvariant();
            await lifecycle.RegisterAsync(
                new RegisterSecretCommand(
                    $"synthetic-app-{index:00}/{environment}/{purpose}",
                    type,
                    "Northstar Platform Engineering (fictional)",
                    environment,
                    index % 5 == 0 ? SecretCriticality.Critical : SecretCriticality.High,
                    ["fictional", "synthetic", "portfolio-demo"],
                    "Runtime-generated demonstration credential; no real credential is stored in the repository.",
                    24 * (14 + index % 20),
                    24 * 90,
                    24,
                    [consumers[index - 1].Id]),
                cancellationToken);
        }
    }
}
