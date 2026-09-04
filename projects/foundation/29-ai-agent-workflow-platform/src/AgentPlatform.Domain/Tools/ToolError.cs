namespace AgentPlatform.Domain.Tools;

/// <summary>Stable machine-readable error codes returned to the model instead of exceptions.</summary>
public static class ToolErrorCodes
{
    public const string UnknownTool = "unknown_tool";
    public const string Unauthorized = "unauthorized_tool";
    public const string InvalidArguments = "invalid_arguments";
    public const string RateLimited = "rate_limited";
    public const string Timeout = "timeout";
    public const string PolicyViolation = "policy_violation";
    public const string ApprovalRequired = "approval_required";
    public const string OutputTooLarge = "output_too_large";
    public const string DependencyFailure = "dependency_failure";
    public const string NotFound = "not_found";
    public const string Internal = "internal_error";
}

/// <summary>
/// A structured error surfaced to the model. Crucially, tool failures are <b>data</b> returned to
/// the caller, never thrown exceptions that could unwind the engine or leak internals.
/// </summary>
public sealed record ToolError(string Code, string Message, bool Transient = false)
{
    public static ToolError Unauthorized(string tool, IEnumerable<string> missing) =>
        new(ToolErrorCodes.Unauthorized,
            $"Caller is not authorised to invoke '{tool}'. Missing scope(s): {string.Join(", ", missing)}.");

    public static ToolError Unknown(string tool) =>
        new(ToolErrorCodes.UnknownTool, $"No tool named '{tool}' is registered.");

    public static ToolError Invalid(string details) =>
        new(ToolErrorCodes.InvalidArguments, details);

    public static ToolError Policy(string details) =>
        new(ToolErrorCodes.PolicyViolation, details);
}
