using Microsoft.EntityFrameworkCore;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Webhooks;
using Contoso.Payments.Infrastructure.Persistence;

namespace Contoso.Payments.Infrastructure.Webhooks;

public sealed class EfWebhookReplayStore : IWebhookReplayStore
{
    private readonly AppDbContext _db;

    public EfWebhookReplayStore(AppDbContext db)
    {
        _db = db;
    }

    public Task<bool> SeenAsync(string signatureHash, CancellationToken ct)
        => _db.WebhookReplays.AnyAsync(r => r.SignatureHash == signatureHash, ct);

    public async Task RecordAsync(string signatureHash, DateTimeOffset receivedAtUtc, CancellationToken ct)
    {
        _db.WebhookReplays.Add(new WebhookReplayRecord
        {
            SignatureHash = signatureHash,
            ReceivedAtUtc = receivedAtUtc
        });
        // Save via the outer transaction; caller flushes.
        await Task.CompletedTask;
    }
}
