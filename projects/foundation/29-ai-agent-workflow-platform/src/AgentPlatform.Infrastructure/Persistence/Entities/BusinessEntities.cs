namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>A customer record the tools can look up. Seed data is clearly fictional.</summary>
public sealed class Customer
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public string Tier { get; set; } = "standard";
    public decimal LifetimeValueUsd { get; set; }
    public int PriorRefundCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A support ticket the triage workflow classifies and enriches.</summary>
public sealed class Ticket
{
    public required string Id { get; set; }
    public required string CustomerId { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public string Category { get; set; } = "uncategorised";
    public string Priority { get; set; } = "normal";
    public string Status { get; set; } = "open";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A knowledge-base article the search tool returns.</summary>
public sealed class KnowledgeArticle
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string Category { get; set; } = "general";
    public string Tags { get; set; } = string.Empty;
}

/// <summary>An order backing the refund workflow's deterministic eligibility calculation.</summary>
public sealed class OrderRecord
{
    public required string Id { get; set; }
    public required string CustomerId { get; set; }
    public decimal AmountUsd { get; set; }
    public DateTimeOffset PurchasedAt { get; set; }
    public bool ItemReturned { get; set; }
    public string Reason { get; set; } = "change_of_mind";
}

/// <summary>A persisted refund request produced by the approval workflow (mutating tool output).</summary>
public sealed class RefundRequest
{
    public required string Id { get; set; }
    public required string CustomerId { get; set; }
    public string? OrderId { get; set; }
    public decimal AmountUsd { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "created";
    public string? RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An outbound email captured in a durable outbox — no real SMTP is ever contacted.</summary>
public sealed class EmailOutboxMessage
{
    public required string Id { get; set; }
    public required string To { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public string? RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A tamper-evident audit entry for approvals and mutating actions.</summary>
public sealed class AuditLogEntry
{
    public required string Id { get; set; }
    public string? RunId { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Persisted idempotency record guaranteeing at-most-once execution of a non-idempotent tool: the
/// stored result is replayed on retry instead of re-executing the side effect.
/// </summary>
public sealed class IdempotencyRecord
{
    public required string Key { get; set; }
    public required string RunId { get; set; }
    public required string ToolName { get; set; }
    public string ResultJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
