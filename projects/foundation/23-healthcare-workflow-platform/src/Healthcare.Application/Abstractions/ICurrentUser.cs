namespace Healthcare.Application.Abstractions;

/// <summary>Represents the acting principal within a single request. Populated by an API middleware.</summary>
public interface ICurrentUser
{
    string UserId { get; }
    string DisplayName { get; }
    IReadOnlyCollection<string> Roles { get; }
    /// <summary>For clinician users, the clinician entity id, if resolved.</summary>
    Guid? ClinicianId { get; }
    /// <summary>Correlation id, populated by middleware.</summary>
    string CorrelationId { get; }
    string? SourceIp { get; }
    string? UserAgent { get; }
    /// <summary>True if the caller has activated a break-glass access header for this request.</summary>
    bool BreakGlassActivated { get; }
    string? BreakGlassJustification { get; }
    bool HasRole(string role);
    bool IsAuthenticated { get; }
}
