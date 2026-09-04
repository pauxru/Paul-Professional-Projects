using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Infrastructure.Outbox;
using Contoso.Payments.Infrastructure.Persistence;
using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

public class OutboxDispatcherTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public OutboxDispatcherTests(ApiFactory f) { _factory = f; }

    private async Task EnqueueAsync(string topic, string payload)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Topic = topic,
            PayloadJson = payload,
            OccurredAtUtc = clock.UtcNow,
            NextAttemptAtUtc = clock.UtcNow.AddMilliseconds(-1),
            Attempts = 0,
            Dispatched = false,
            CorrelationId = "test-corr"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Dispatcher_delivers_pending_messages()
    {
        await EnqueueAsync("test.topic.ok", "{\"ok\":true}");
        var dispatcher = _factory.Services.GetRequiredService<OutboxDispatcher>();
        var delivered = await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.True(delivered >= 1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.OutboxMessages.CountAsync(m => !m.Dispatched);
        Assert.Equal(0, pending);
    }

    [Fact]
    public async Task Failing_message_is_dead_lettered_after_MaxAttempts()
    {
        // The default in-memory bus never throws, so we swap the bus for one that always throws
        // by publishing a message with an invalid payload the failing test bus will reject.
        // Simpler: register a failing IEventBus at the composition level.  Here we instead
        // simulate failure by directly incrementing Attempts to MaxAttempts-1 and letting the
        // dispatcher push it over the edge with an injected failure.  We use a scoped bus
        // replacement via service provider swap.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Topic = "test.topic.fail",
                PayloadJson = "{}",
                OccurredAtUtc = clock.UtcNow,
                NextAttemptAtUtc = clock.UtcNow.AddMilliseconds(-1),
                Attempts = 2, // already tried twice; MaxAttempts is 3 in test config
                Dispatched = false,
                CorrelationId = "test-corr"
            });
            await db.SaveChangesAsync();
        }

        // Use a bespoke dispatcher wired with a throwing bus for this test so we can force DLQ.
        using var ctScope = _factory.Services.CreateScope();
        var options = ctScope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Contoso.Payments.Application.Common.OutboxOptions>>();
        var log = ctScope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboxDispatcher>>();
        var metrics = ctScope.ServiceProvider.GetRequiredService<Contoso.Payments.Infrastructure.Observability.PaymentMetrics>();
        var scopeFactory = ctScope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();

        var failingDispatcher = new OutboxDispatcher(
            new SwappedScopeFactory(scopeFactory, sf => sf.CreateScope(), replaceBus: true),
            options, log, metrics);
        await failingDispatcher.DispatchOnceAsync(CancellationToken.None);

        using var check = _factory.Services.CreateScope();
        var db2 = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var dead = await db2.OutboxDeadLetters.AnyAsync();
        Assert.True(dead, "Message should have been dead-lettered after exceeding MaxAttempts");
    }

    /// <summary>
    /// A scope factory that replaces the IEventBus with a bus whose PublishAsync always throws,
    /// so we can drive the dead-letter path deterministically.
    /// </summary>
    private sealed class SwappedScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly Func<IServiceScopeFactory, IServiceScope> _create;
        private readonly bool _replaceBus;
        public SwappedScopeFactory(IServiceScopeFactory inner, Func<IServiceScopeFactory, IServiceScope> create, bool replaceBus)
        {
            _inner = inner;
            _create = create;
            _replaceBus = replaceBus;
        }
        public IServiceScope CreateScope()
        {
            var s = _inner.CreateScope();
            return new WrappedScope(s, _replaceBus);
        }
    }

    private sealed class WrappedScope : IServiceScope
    {
        private readonly IServiceScope _inner;
        private readonly IServiceProvider _sp;
        public WrappedScope(IServiceScope inner, bool replaceBus)
        {
            _inner = inner;
            _sp = new WrappedProvider(inner.ServiceProvider, replaceBus);
        }
        public IServiceProvider ServiceProvider => _sp;
        public void Dispose() => _inner.Dispose();
    }

    private sealed class WrappedProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly bool _replaceBus;
        public WrappedProvider(IServiceProvider inner, bool replaceBus)
        {
            _inner = inner;
            _replaceBus = replaceBus;
        }
        public object? GetService(Type serviceType)
        {
            if (_replaceBus && serviceType == typeof(IEventBus))
                return new ThrowingBus();
            return _inner.GetService(serviceType);
        }
    }

    private sealed class ThrowingBus : IEventBus
    {
        public ValueTask PublishAsync(string topic, string payloadJson, CancellationToken ct)
            => ValueTask.FromException(new InvalidOperationException("simulated bus failure"));
    }
}
