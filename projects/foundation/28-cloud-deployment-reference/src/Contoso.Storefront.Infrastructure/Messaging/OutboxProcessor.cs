using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Domain;
using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contoso.Storefront.Infrastructure.Messaging;

public sealed class OutboxProcessor(
    StorefrontDbContext dbContext,
    IOutboxTransport transport,
    IClock clock)
{
    public const string WorkerName = "storefront-outbox-v1";

    public async Task<int> ProcessOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        var messages = await dbContext.OutboxMessages
            .Where(message => message.ProcessedAt == null)
            .OrderBy(message => message.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await transport.PublishAsync(
                    message.Id,
                    message.Type,
                    message.Payload,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                message.MarkAttempt(exception.Message);
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            message.MarkAttempt(null);
            message.MarkProcessed(clock.UtcNow);

            var checkpoint = await dbContext.WorkerCheckpoints
                .SingleOrDefaultAsync(
                    item => item.WorkerName == WorkerName,
                    cancellationToken);

            if (checkpoint is null)
            {
                dbContext.WorkerCheckpoints.Add(
                    new WorkerCheckpoint(WorkerName, message.Id, clock.UtcNow));
            }
            else
            {
                checkpoint.Advance(message.Id, clock.UtcNow);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            processed++;
        }

        return processed;
    }
}
