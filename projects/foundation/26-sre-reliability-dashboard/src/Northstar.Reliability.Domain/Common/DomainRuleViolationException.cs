namespace Northstar.Reliability.Domain.Common;

public sealed class DomainRuleViolationException(string message) : InvalidOperationException(message);
