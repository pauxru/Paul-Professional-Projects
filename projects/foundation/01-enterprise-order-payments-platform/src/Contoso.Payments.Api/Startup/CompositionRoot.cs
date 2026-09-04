using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using System.Threading.RateLimiting;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Catalog;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Application.Orders;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Application.Payments;
using Contoso.Payments.Application.Reconciliation;
using Contoso.Payments.Application.Refunds;
using Contoso.Payments.Application.Webhooks;
using Contoso.Payments.Infrastructure.Ids;
using Contoso.Payments.Infrastructure.Messaging;
using Contoso.Payments.Infrastructure.Outbox;
using Contoso.Payments.Infrastructure.Payments;
using Contoso.Payments.Infrastructure.Persistence;
using Contoso.Payments.Infrastructure.Time;
using Contoso.Payments.Infrastructure.Webhooks;

namespace Contoso.Payments.Api.Startup;

public static class CompositionRoot
{
    public static WebApplicationBuilder AddPaymentsPlatform(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<DatabaseOptions>()
            .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<MessagingOptions>()
            .Bind(builder.Configuration.GetSection(MessagingOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<PaymentProviderOptions>()
            .Bind(builder.Configuration.GetSection(PaymentProviderOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<OutboxOptions>()
            .Bind(builder.Configuration.GetSection(OutboxOptions.SectionName));

        var dbOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? new DatabaseOptions();

        builder.Services.AddDbContext<AppDbContext>(o =>
        {
            switch (dbOptions.Provider)
            {
                case "Postgres":
                    // Documented stub only — Postgres would be wired via Npgsql adapter here.
                    throw new NotSupportedException(
                        "Postgres provider is a documented adapter switch.  Use Sqlite for local runs.");
                default:
                    o.UseSqlite(dbOptions.ConnectionString);
                    break;
            }
        });
        builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        builder.Services.AddScoped<OutboxWriter>();

        builder.Services.AddSingleton<DeterministicPaymentProviderSimulator>();
        builder.Services.AddSingleton<IPaymentProvider>(sp =>
        {
            var inner = sp.GetRequiredService<DeterministicPaymentProviderSimulator>();
            var log = sp.GetRequiredService<ILogger<ResilientPaymentProvider>>();
            return new ResilientPaymentProvider(inner, log);
        });

        builder.Services.AddSingleton<IWebhookSignatureVerifier, HmacSha256WebhookSignatureVerifier>();
        builder.Services.AddScoped<IWebhookReplayStore, EfWebhookReplayStore>();

        var messagingProvider = builder.Configuration["Messaging:Provider"] ?? "InMemory";
        switch (messagingProvider)
        {
            case "RabbitMq":
                builder.Services.AddSingleton<IEventBus, RabbitMqEventBusStub>();
                break;
            case "AzureServiceBus":
                builder.Services.AddSingleton<IEventBus, AzureServiceBusEventBusStub>();
                break;
            default:
                builder.Services.AddSingleton<ChannelEventBus>();
                builder.Services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<ChannelEventBus>());
                builder.Services.AddHostedService<ChannelEventBusPump>();
                break;
        }

        builder.Services.AddScoped<CatalogService>();
        builder.Services.AddScoped<OrderService>();
        builder.Services.AddScoped<PaymentService>();
        builder.Services.AddScoped<RefundService>();
        builder.Services.AddScoped<WebhookService>();
        builder.Services.AddScoped<ReconciliationService>();

        builder.Services.AddSingleton<OutboxDispatcher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());

        // Rate limiting (built into the framework, no package).
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 200,
                        QueueLimit = 0,
                        Window = TimeSpan.FromSeconds(10)
                    }));
        });

        builder.Services.AddProblemDetails(o =>
        {
            o.CustomizeProblemDetails = ctx =>
            {
                ctx.ProblemDetails.Extensions["traceId"] =
                    (ctx.HttpContext.Items[Contoso.Payments.Api.Middleware.CorrelationIdMiddleware.ItemKey] as string)
                    ?? ctx.HttpContext.TraceIdentifier;
            };
        });

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("db");

        return builder;
    }
}
