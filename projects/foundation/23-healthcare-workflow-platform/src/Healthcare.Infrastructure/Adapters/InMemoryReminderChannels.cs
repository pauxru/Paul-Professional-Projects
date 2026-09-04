using Healthcare.Application.Abstractions;

namespace Healthcare.Infrastructure.Adapters;

public sealed class InMemorySmsReminderChannel : IReminderChannel
{
    public string Name => "sms";
    private readonly List<ReminderMessage> _log = new();
    public IReadOnlyList<ReminderMessage> Log => _log.AsReadOnly();
    public bool ForceFail { get; set; }

    public Task<ReminderDeliveryResult> SendAsync(ReminderMessage message, CancellationToken ct)
    {
        if (ForceFail) return Task.FromResult(new ReminderDeliveryResult(false, "simulated_failure"));
        _log.Add(message);
        return Task.FromResult(new ReminderDeliveryResult(true, null));
    }
}

public sealed class InMemoryEmailReminderChannel : IReminderChannel
{
    public string Name => "email";
    private readonly List<ReminderMessage> _log = new();
    public IReadOnlyList<ReminderMessage> Log => _log.AsReadOnly();
    public bool ForceFail { get; set; }

    public Task<ReminderDeliveryResult> SendAsync(ReminderMessage message, CancellationToken ct)
    {
        if (ForceFail) return Task.FromResult(new ReminderDeliveryResult(false, "simulated_failure"));
        _log.Add(message);
        return Task.FromResult(new ReminderDeliveryResult(true, null));
    }
}
