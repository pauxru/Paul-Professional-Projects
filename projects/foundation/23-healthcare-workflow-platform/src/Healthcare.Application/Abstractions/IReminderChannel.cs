namespace Healthcare.Application.Abstractions;

public interface IReminderChannel
{
    string Name { get; }
    Task<ReminderDeliveryResult> SendAsync(ReminderMessage message, CancellationToken ct);
}

public sealed record ReminderMessage(
    Guid ReminderId,
    Guid AppointmentId,
    Guid PatientId,
    string RecipientContact,
    string Body,
    string CorrelationId);

public sealed record ReminderDeliveryResult(bool Success, string? Error);
