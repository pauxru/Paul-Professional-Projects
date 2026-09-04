using System.Security.Cryptography;
using System.Text;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Application;

public sealed class RotationScheduleCalculator
{
    public DateTimeOffset CalculateNextRotation(SecretRecord secret)
    {
        var current = secret.CurrentVersion
            ?? throw new DomainRuleException("A secret without a current version cannot be scheduled.");
        var anchor = current.ActivatedAt ?? current.CreatedAt;
        var nominal = anchor.Add(secret.RotationInterval);
        var jitterRange = TimeSpan.FromTicks(Math.Max(1, secret.RotationInterval.Ticks / 10));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret.Name));
        var sample = BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue;
        var offsetTicks = (long)((sample * 2d - 1d) * jitterRange.Ticks);
        var jittered = nominal.AddTicks(offsetTicks);
        var hardDeadline = current.ExpiresAt.Subtract(secret.GracePeriod);
        return jittered <= hardDeadline ? jittered : hardDeadline;
    }

    public IReadOnlyList<DateTimeOffset> BuildDistribution(IEnumerable<SecretRecord> secrets) =>
        secrets.Select(CalculateNextRotation).OrderBy(x => x).ToArray();
}

public sealed class AutomaticRotationScheduler(
    ISecretsRepository repository,
    RotationScheduleCalculator scheduleCalculator,
    RotationEngine rotationEngine,
    IClock clock)
{
    public async Task<IReadOnlyList<Guid>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var secrets = await repository.ListSecretsAsync(cancellationToken);
        var rotations = await repository.ListRotationsAsync(cancellationToken);
        var requested = new List<Guid>();

        foreach (var secret in secrets.Where(x => x.CurrentVersion is not null))
        {
            if (scheduleCalculator.CalculateNextRotation(secret) > now ||
                rotations.Any(x => x.SecretId == secret.Id && !x.IsTerminal))
            {
                continue;
            }

            var operation = await rotationEngine.RequestAsync(
                new RequestRotationCommand(
                    secret.Name,
                    RotationStrategyKind.DualWrite,
                    $"schedule:{secret.Id}:{now:yyyyMMddHH}",
                    "system:scheduler",
                    Guid.NewGuid().ToString("N")),
                cancellationToken);
            requested.Add(operation.Id);
            await rotationEngine.RunToPauseOrTerminalAsync(operation.Id, cancellationToken);
        }

        return requested;
    }
}
