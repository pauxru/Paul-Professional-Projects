using FraudPipeline.Domain.Abstractions;

namespace FraudPipeline.Infrastructure.Time;

public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;
    public FakeClock(DateTimeOffset initial) => _now = initial;
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    public void SetTo(DateTimeOffset value) => _now = value;
}
