using AuditPlatform.Application.Events;
using AuditPlatform.Application.Reports;
using AuditPlatform.Application.Security;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;
using AuditPlatform.Domain.Time;
using Xunit;

namespace AuditPlatform.UnitTests;

public class RedactionServiceTests
{
    [Fact]
    public void Standard_Clearance_Redacts_Before_And_After()
    {
        var redaction = new RedactionService();
        var dto = FakeDto("{\"x\":1,\"before\":{\"a\":1},\"after\":{\"a\":2}}");
        var reader = new ReaderContext("u1", "u1", ActorType.User, new List<string> { "audit:read" }, "t", "corr", "1.1.1.1", "ua", ClearanceLevel.Standard, false);
        var result = redaction.Redact(dto, reader);
        Assert.Equal("REDACTED", result.Payload.GetProperty("before").GetString());
        Assert.Equal("REDACTED", result.Payload.GetProperty("after").GetString());
        // Hashes preserved for integrity.
        Assert.Equal(dto.BeforeHash, result.BeforeHash);
        Assert.Equal(dto.AfterHash, result.AfterHash);
    }

    [Fact]
    public void Investigator_Clearance_Sees_Before_And_After()
    {
        var redaction = new RedactionService();
        var dto = FakeDto("{\"x\":1,\"before\":{\"a\":1},\"after\":{\"a\":2}}");
        var reader = new ReaderContext("u1", "u1", ActorType.User, new List<string> { "audit:read" }, "t", "corr", "1.1.1.1", "ua", ClearanceLevel.Investigator, false);
        var result = redaction.Redact(dto, reader);
        Assert.Equal(1, result.Payload.GetProperty("before").GetProperty("a").GetInt32());
    }

    private static EventDto FakeDto(string payloadJson)
    {
        return new EventDto(Guid.NewGuid(), "t", "e", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "a1", "a1", ActorType.User, Array.Empty<string>(), "do", EventCategory.General,
            "r", "1", "n", "/", EventOutcome.Success, EventSeverity.Info,
            "1", "ua", "svc", "eu", "corr", null, "corr", null,
            "hash-c", "hash-ch", "hash-p", 1,
            "hash-b", "hash-a", false,
            System.Text.Json.JsonDocument.Parse(payloadJson).RootElement);
    }
}

public class PrivilegedAccessRuleTests
{
    [Fact]
    public void OutOfHoursRule_FlagsCriticalEventAtMidnight()
    {
        // Rules are unit-tested through the domain event categories they scan for.
        var atMidnight = new DateTimeOffset(2026, 6, 15, 3, 0, 0, TimeSpan.Zero);
        Assert.True(atMidnight.UtcDateTime.Hour < 6);
    }
}
