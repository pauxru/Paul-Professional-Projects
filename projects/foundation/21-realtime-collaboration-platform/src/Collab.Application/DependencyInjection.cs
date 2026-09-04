using Collab.Application.Abstractions;
using Collab.Application.Options;
using Collab.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Collab.Application;

/// <summary>Registers Application-layer services and options. Adapters (persistence, presence,
/// metrics, client push) are supplied by the composition root in the API/Infrastructure layers.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCollabApplication(this IServiceCollection services)
    {
        // Defaults live in CollaborationOptions; the API composition root binds configuration over them.
        services.AddOptions<CollaborationOptions>();

        services.AddScoped<IAccessControl, AccessControlService>();
        services.AddScoped<IAuditLog, AuditService>();

        services.AddScoped<WorkspaceService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<CommentService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<CollaborationService>();

        return services;
    }
}
