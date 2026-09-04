namespace NotificationPlatform.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Analytics;
using NotificationPlatform.Application.Dlq;
using NotificationPlatform.Application.Fairness;
using NotificationPlatform.Application.Localization;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Application.Preferences;
using NotificationPlatform.Application.Receipts;
using NotificationPlatform.Application.Suppressions;
using NotificationPlatform.Application.Templates;
using NotificationPlatform.Application.Unsubscribe;
using NotificationPlatform.Infrastructure.Delivery;
using NotificationPlatform.Infrastructure.Metrics;
using NotificationPlatform.Infrastructure.Persistence;
using NotificationPlatform.Infrastructure.Providers.Common;
using NotificationPlatform.Infrastructure.Providers.Email;
using NotificationPlatform.Infrastructure.Providers.Push;
using NotificationPlatform.Infrastructure.Providers.Sms;
using NotificationPlatform.Infrastructure.Providers.Webhook;
using NotificationPlatform.Infrastructure.Security;
using NotificationPlatform.Infrastructure.Templates;
using NotificationPlatform.Application.Providers;

public static class ServiceRegistration
{
    public static IServiceCollection AddNotificationPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton<INotificationMetrics, NotificationMetrics>();
        services.AddSingleton<ITemplateEngine, TemplateEngine>();
        services.AddSingleton<ILocaleResolver, LocaleResolver>();
        services.AddSingleton<IQuietHoursCalculator, QuietHoursCalculator>();
        services.AddSingleton<ITenantFairnessScheduler, TenantFairnessScheduler>();
        services.AddSingleton<IBackoffPolicy>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new ExponentialBackoffPolicy(opts.BackoffBaseMilliseconds, opts.BackoffMaxMilliseconds, opts.DeterministicMode ? opts.Seed : null);
        });
        services.AddSingleton<IUnsubscribeTokenService, UnsubscribeTokenService>();
        services.AddSingleton<IWebhookSignatureService, WebhookSignatureService>();

        services.AddSingleton<IProviderClock, ProviderClock>();
        services.AddSingleton<IChannelProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new SmtpSimulator(opts.Seed + 1, sp.GetRequiredService<IProviderClock>());
        });
        services.AddSingleton<IChannelProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new SendGridSimulator(opts.Seed + 2, sp.GetRequiredService<IProviderClock>());
        });
        services.AddSingleton<IChannelProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new TwilioSimulator(opts.Seed + 3, sp.GetRequiredService<IProviderClock>());
        });
        services.AddSingleton<IChannelProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new AfricasTalkingSimulator(opts.Seed + 4, sp.GetRequiredService<IProviderClock>());
        });
        services.AddSingleton<IChannelProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new FcmSimulator(opts.Seed + 5, sp.GetRequiredService<IProviderClock>());
        });
        services.AddSingleton<IChannelProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new ApnsSimulator(opts.Seed + 6, sp.GetRequiredService<IProviderClock>());
        });
        services.AddSingleton<IChannelProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new HttpWebhookSimulator(opts.Seed + 7, sp.GetRequiredService<IProviderClock>());
        });
        services.AddSingleton<IChannelProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value;
            return new ServiceBusWebhookSimulator(opts.Seed + 8, sp.GetRequiredService<IProviderClock>());
        });
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<IProviderRateLimiter, InMemoryProviderRateLimiter>();
        services.AddSingleton<IProviderHealthTracker, ProviderHealthTracker>();

        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<TemplateService>();
        services.AddScoped<ITemplateService>(sp => sp.GetRequiredService<TemplateService>());
        services.AddScoped<IDeliveryPipeline, DeliveryPipeline>();
        services.AddScoped<IReceiptIngestor, ReceiptIngestor>();
        services.AddScoped<IPreferenceService, PreferenceService>();
        services.AddScoped<ISuppressionService, SuppressionService>();
        services.AddScoped<IUnsubscribeService, UnsubscribeService>();
        services.AddScoped<IDlqService, DlqService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        return services;
    }

    public static IServiceCollection AddDeliveryHostedWorker(this IServiceCollection services)
    {
        services.AddHostedService<DeliveryWorker>();
        return services;
    }
}
