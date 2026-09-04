namespace Idp.Application.Exporting;

/// <summary>A request to book a document into the (simulated) downstream ERP.</summary>
public sealed record ErpExportRequest(
    Guid DocumentId,
    string IdempotencyKey,
    string Format,
    string Payload);

/// <summary>The ERP response. <see cref="Transient"/> failures are retryable; others are not.</summary>
public sealed record ErpExportResult(bool Success, string? Reference, string? Error, bool Transient)
{
    public static ErpExportResult Ok(string reference) => new(true, reference, null, false);
    public static ErpExportResult TransientFailure(string error) => new(false, null, error, true);
    public static ErpExportResult PermanentFailure(string error) => new(false, null, error, false);
}

/// <summary>
/// Client for the simulated ERP HTTP endpoint. Honours the idempotency key so a retried request that
/// the ERP already accepted returns the original reference instead of double-booking.
/// </summary>
public interface IErpExportClient
{
    Task<ErpExportResult> SendAsync(ErpExportRequest request, CancellationToken ct = default);
}
