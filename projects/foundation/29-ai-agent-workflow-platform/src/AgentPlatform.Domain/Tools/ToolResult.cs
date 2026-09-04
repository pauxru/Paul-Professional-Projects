namespace AgentPlatform.Domain.Tools;

/// <summary>
/// The outcome of a tool invocation. Exactly one of <see cref="Output"/> (success) or
/// <see cref="Error"/> (failure) is meaningful, indicated by <see cref="Succeeded"/>.
/// </summary>
public sealed record ToolResult
{
    public required bool Succeeded { get; init; }

    /// <summary>JSON string result on success.</summary>
    public string? Output { get; init; }

    public ToolError? Error { get; init; }

    public decimal Cost { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>True when the result was replayed from an idempotency record, not re-executed.</summary>
    public bool FromIdempotencyCache { get; init; }

    public static ToolResult Ok(string output, decimal cost = 0m) =>
        new() { Succeeded = true, Output = output, Cost = cost };

    public static ToolResult Fail(ToolError error) =>
        new() { Succeeded = false, Error = error };

    /// <summary>Render the result as the string a tool message carries back to the model.</summary>
    public string ToModelContent() => Succeeded
        ? Output ?? "{}"
        : $"{{\"error\":{{\"code\":\"{Error!.Code}\",\"message\":{System.Text.Json.JsonSerializer.Serialize(Error.Message)}}}}}";
}
