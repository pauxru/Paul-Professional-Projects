using System.Text.Json.Nodes;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Json;
using AgentPlatform.Domain.Tools;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Tools;

/// <summary>update_ticket_status — mutating, but idempotent (setting a status to a value twice is safe).</summary>
public sealed class UpdateTicketStatusTool : ITool
{
    private readonly AgentDbContext _db;

    public UpdateTicketStatusTool(AgentDbContext db) => _db = db;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "update_ticket_status",
        Version = "1.0.0",
        Description = "Set the status of a support ticket.",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "ticket_id": { "type": "string", "minLength": 1, "maxLength": 64 },
            "status": { "type": "string", "enum": ["open", "pending", "resolved", "escalated", "closed"] }
          },
          "required": ["ticket_id", "status"],
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.Mutating,
        RiskLevel = ToolRiskLevel.Medium,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.001m,
        IsNaturallyIdempotent = true,
    };

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var ticketId = ToolJson.OptString(arguments, "ticket_id");
        var status = ToolJson.OptString(arguments, "status") ?? "open";

        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
        if (ticket is null)
            return ToolResult.Fail(new ToolError(ToolErrorCodes.NotFound, $"Ticket '{ticketId}' was not found."));

        var previous = ticket.Status;
        ticket.Status = status;
        ticket.UpdatedAt = context.Clock.UtcNow;

        _db.AuditLog.Add(new AuditLogEntry
        {
            Id = $"audit-{context.RunId}-{context.StepId}-status",
            RunId = context.RunId,
            Actor = context.Caller.UserId,
            Action = "update_ticket_status",
            DetailsJson = ToolJson.Serialize(new { ticket_id = ticketId, from = previous, to = status }),
            CreatedAt = context.Clock.UtcNow,
        });

        return ToolResult.Ok(ToolJson.Serialize(new { ticket_id = ticketId, status, previous_status = previous }));
    }
}

/// <summary>
/// create_refund_request — mutating and NOT naturally idempotent, so it is protected by an
/// idempotency key, and it requires human approval before it can run.
/// </summary>
public sealed class CreateRefundRequestTool : ITool
{
    private readonly AgentDbContext _db;

    public CreateRefundRequestTool(AgentDbContext db) => _db = db;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "create_refund_request",
        Version = "1.0.0",
        Description = "Create a refund request for a customer. Requires prior human approval.",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "customer_id": { "type": "string", "minLength": 1, "maxLength": 64 },
            "amount_usd": { "type": "number", "minimum": 0, "maximum": 100000 },
            "reason": { "type": "string", "minLength": 1, "maxLength": 500 },
            "order_id": { "type": "string", "maxLength": 64 }
          },
          "required": ["customer_id", "amount_usd", "reason"],
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.Mutating,
        RiskLevel = ToolRiskLevel.High,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.01m,
        RequiresApproval = true,
        IsNaturallyIdempotent = false,
    };

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var customerId = ToolJson.OptString(arguments, "customer_id")!;
        var amount = arguments["amount_usd"]!.GetValue<decimal>();
        var reason = ToolJson.OptString(arguments, "reason") ?? string.Empty;
        var orderId = ToolJson.OptString(arguments, "order_id");

        var id = $"rr-{context.RunId}-{context.StepId}";
        _db.RefundRequests.Add(new RefundRequest
        {
            Id = id,
            CustomerId = customerId,
            OrderId = orderId,
            AmountUsd = amount,
            Reason = reason,
            Status = "created",
            RunId = context.RunId,
            CreatedAt = context.Clock.UtcNow,
        });

        _db.AuditLog.Add(new AuditLogEntry
        {
            Id = $"audit-{context.RunId}-{context.StepId}-refund",
            RunId = context.RunId,
            Actor = context.Caller.UserId,
            Action = "create_refund_request",
            DetailsJson = ToolJson.Serialize(new { refund_id = id, customer_id = customerId, amount_usd = amount }),
            CreatedAt = context.Clock.UtcNow,
        });

        return ToolResult.Ok(ToolJson.Serialize(new
        {
            refund_id = id, customer_id = customerId, amount_usd = amount, status = "created",
        }));
    }
}

/// <summary>
/// send_email — external side effect (captured in a durable outbox, never a real SMTP send),
/// not naturally idempotent, and requires human approval.
/// </summary>
public sealed class SendEmailTool : ITool
{
    private readonly AgentDbContext _db;

    public SendEmailTool(AgentDbContext db) => _db = db;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "send_email",
        Version = "1.0.0",
        Description = "Send an email to a customer (captured in the outbox). Requires prior human approval.",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "to": { "type": "string", "format": "email", "maxLength": 256 },
            "subject": { "type": "string", "minLength": 1, "maxLength": 200 },
            "body": { "type": "string", "minLength": 1, "maxLength": 8000 }
          },
          "required": ["to", "subject", "body"],
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.External,
        RiskLevel = ToolRiskLevel.High,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.005m,
        RequiresApproval = true,
        IsNaturallyIdempotent = false,
    };

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var to = ToolJson.OptString(arguments, "to")!;
        var subject = ToolJson.OptString(arguments, "subject") ?? string.Empty;
        var body = ToolJson.OptString(arguments, "body") ?? string.Empty;

        var id = $"email-{context.RunId}-{context.StepId}";
        _db.EmailOutbox.Add(new EmailOutboxMessage
        {
            Id = id,
            To = to,
            Subject = subject,
            Body = body,
            RunId = context.RunId,
            CreatedAt = context.Clock.UtcNow,
        });

        await Task.CompletedTask;
        return ToolResult.Ok(ToolJson.Serialize(new { message_id = id, to, status = "queued" }));
    }
}
