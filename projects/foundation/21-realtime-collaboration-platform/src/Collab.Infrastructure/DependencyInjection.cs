using Collab.Application.Abstractions;
using Collab.Domain.Abstractions;
using Collab.Infrastructure.Observability;
using Collab.Infrastructure.Persistence;
using Collab.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Collab.Infrastructure;

/// <summary>Registers persistence, presence, runtime cache and metrics adapters.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCollabInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IOperationLogRepository, OperationLogRepository>();
        services.AddScoped<ISnapshotRepository, SnapshotRepository>();
        services.AddScoped<INamedVersionRepository, NamedVersionRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();

        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IPresenceStore, InMemoryPresenceStore>();
        services.AddSingleton<IDocumentRuntimeCache, DocumentRuntimeCache>();
        services.AddSingleton<ICollabMetrics, CollabMetrics>();

        services.AddScoped<CollabDbInitializer>();
        return services;
    }
}
