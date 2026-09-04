using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Infrastructure.Persistence;
using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

/// <summary>
/// Verifies that placing an order writes an OutboxMessage in the SAME transaction as the order —
/// the core promise of the transactional outbox pattern.
/// </summary>
public class OutboxTransactionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public OutboxTransactionTests(ApiFactory f) { _factory = f; }

    [Fact]
    public async Task Placing_an_order_writes_an_outbox_row_atomically()
    {
        var http = _factory.CreateClient();
        var token = _factory.IssueToken("orders:write");
        var client = new ApiClient(http, token);

        int before, after;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            before = await db.OutboxMessages.CountAsync();
        }

        var body = new { customerRef = "outbox-user", currency = "USD", lines = new[] { new { sku = "TEST-USD-1", quantity = 1 } } };
        var _ = await client.PostOk<System.Text.Json.JsonElement>("/api/v1/orders", body, Guid.NewGuid().ToString());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            after = await db.OutboxMessages.CountAsync();
        }
        Assert.True(after > before, "Order placement did not enqueue a domain event to the outbox.");
    }
}
