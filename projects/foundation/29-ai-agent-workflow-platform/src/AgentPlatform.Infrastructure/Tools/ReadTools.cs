using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Json;
using AgentPlatform.Domain.Security;
using AgentPlatform.Domain.Tools;
using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Tools;

/// <summary>Shared helpers for building tool JSON results.</summary>
internal static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    public static string? OptString(JsonObject args, string key) =>
        args.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int OptInt(JsonObject args, string key, int fallback) =>
        args.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
}

/// <summary>search_knowledge_base — read-only keyword search over seeded KB articles.</summary>
public sealed class SearchKnowledgeBaseTool : ITool
{
    private readonly AgentDbContext _db;

    public SearchKnowledgeBaseTool(AgentDbContext db) => _db = db;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "search_knowledge_base",
        Version = "1.0.0",
        Description = "Search the internal knowledge base for articles relevant to a query.",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "minLength": 2, "maxLength": 400 },
            "top_k": { "type": "integer", "minimum": 1, "maximum": 10 }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.ReadOnly,
        RiskLevel = ToolRiskLevel.Low,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.001m,
    };

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var query = ToolJson.OptString(arguments, "query") ?? string.Empty;
        var topK = ToolJson.OptInt(arguments, "top_k", 3);
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var articles = await _db.KnowledgeArticles.AsNoTracking().ToListAsync(cancellationToken);
        var ranked = articles
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.Category,
                Score = terms.Count(t => (a.Title + " " + a.Body + " " + a.Tags).ToLowerInvariant().Contains(t)),
                Snippet = a.Body.Length > 240 ? a.Body[..240] + "…" : a.Body,
            })
            .Where(a => a.Score > 0)
            .OrderByDescending(a => a.Score)
            .Take(topK)
            .ToList();

        return ToolResult.Ok(ToolJson.Serialize(new { count = ranked.Count, results = ranked }));
    }
}

/// <summary>get_ticket — read-only lookup of a support ticket by id.</summary>
public sealed class GetTicketTool : ITool
{
    private readonly AgentDbContext _db;

    public GetTicketTool(AgentDbContext db) => _db = db;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "get_ticket",
        Version = "1.0.0",
        Description = "Fetch a support ticket and its metadata by ticket id.",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": { "ticket_id": { "type": "string", "minLength": 1, "maxLength": 64 } },
          "required": ["ticket_id"],
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.ReadOnly,
        RiskLevel = ToolRiskLevel.Low,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.0005m,
    };

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var ticketId = ToolJson.OptString(arguments, "ticket_id");
        var ticket = await _db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
        if (ticket is null)
            return ToolResult.Fail(new ToolError(ToolErrorCodes.NotFound, $"Ticket '{ticketId}' was not found."));

        return ToolResult.Ok(ToolJson.Serialize(new
        {
            id = ticket.Id,
            customer_id = ticket.CustomerId,
            subject = ticket.Subject,
            body = ticket.Body,
            category = ticket.Category,
            priority = ticket.Priority,
            status = ticket.Status,
        }));
    }
}

/// <summary>lookup_customer — read-only customer lookup by id or email.</summary>
public sealed class LookupCustomerTool : ITool
{
    private readonly AgentDbContext _db;

    public LookupCustomerTool(AgentDbContext db) => _db = db;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "lookup_customer",
        Version = "1.0.0",
        Description = "Look up a customer by id or email address.",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "customer_id": { "type": "string", "maxLength": 64 },
            "email": { "type": "string", "format": "email", "maxLength": 256 }
          },
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.ReadOnly,
        RiskLevel = ToolRiskLevel.Low,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.0005m,
    };

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var id = ToolJson.OptString(arguments, "customer_id");
        var email = ToolJson.OptString(arguments, "email");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(email))
            return ToolResult.Fail(ToolError.Invalid("Provide either customer_id or email."));

        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(
            c => (id != null && c.Id == id) || (email != null && c.Email == email), cancellationToken);
        if (customer is null)
            return ToolResult.Fail(new ToolError(ToolErrorCodes.NotFound, "No matching customer."));

        return ToolResult.Ok(ToolJson.Serialize(new
        {
            id = customer.Id,
            name = customer.Name,
            email = customer.Email,
            tier = customer.Tier,
            lifetime_value_usd = customer.LifetimeValueUsd,
            prior_refund_count = customer.PriorRefundCount,
        }));
    }
}

/// <summary>summarise_document — deterministic extractive summary (no model, no network).</summary>
public sealed class SummariseDocumentTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "summarise_document",
        Version = "1.0.0",
        Description = "Produce a deterministic extractive summary of a block of text.",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "minLength": 1, "maxLength": 20000 },
            "max_sentences": { "type": "integer", "minimum": 1, "maximum": 10 }
          },
          "required": ["text"],
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.Costly,
        RiskLevel = ToolRiskLevel.Low,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.002m,
    };

    public Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var text = ToolJson.OptString(arguments, "text") ?? string.Empty;
        var maxSentences = ToolJson.OptInt(arguments, "max_sentences", 3);
        var summary = ExtractiveSummary.Summarise(text, maxSentences);
        return Task.FromResult(ToolResult.Ok(ToolJson.Serialize(new
        {
            summary,
            sentence_count = summary.Count,
            original_length = text.Length,
        })));
    }
}

/// <summary>calculate — evaluates an arithmetic expression with a safe parser (never eval).</summary>
public sealed class CalculateTool : ITool
{
    private readonly SafeExpressionEvaluator _evaluator = new();

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "calculate",
        Version = "1.0.0",
        Description = "Evaluate a numeric arithmetic expression (+ - * / % ^ and abs/min/max/round/floor/ceil/sqrt).",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": { "expression": { "type": "string", "minLength": 1, "maxLength": 512 } },
          "required": ["expression"],
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.ReadOnly,
        RiskLevel = ToolRiskLevel.Low,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.0001m,
    };

    public Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var expression = ToolJson.OptString(arguments, "expression") ?? string.Empty;
        if (_evaluator.TryEvaluate(expression, out var value, out var error))
            return Task.FromResult(ToolResult.Ok(ToolJson.Serialize(new { expression, result = value })));
        return Task.FromResult(ToolResult.Fail(ToolError.Invalid($"Invalid expression: {error}")));
    }
}
