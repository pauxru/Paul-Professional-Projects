using System.Text.Json.Nodes;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Json;
using AgentPlatform.Domain.Security;
using AgentPlatform.Domain.Tools;

namespace AgentPlatform.Infrastructure.Tools;

/// <summary>
/// http_get — the only outbound-HTTP tool. It is locked down hard: an explicit host allow-list, an
/// SSRF guard that rejects any URL resolving to a private/loopback/link-local address, http/https
/// only, and redirects are never followed (an off-list redirect is refused, not chased). This is a
/// deliberately small, auditable seam — model output can never turn it into an arbitrary fetch.
/// </summary>
public sealed class HttpGetTool : ITool
{
    private readonly HttpClient _httpClient;
    private readonly UrlSecurityPolicy _policy;
    private const int MaxBodyChars = 16_000;

    public HttpGetTool(HttpClient httpClient, UrlSecurityPolicy policy)
    {
        _httpClient = httpClient;
        _policy = policy;
    }

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "http_get",
        Version = "1.0.0",
        Description = "HTTP GET a URL on the allow-list of approved hosts (SSRF-guarded, no redirects).",
        ParameterSchema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": { "url": { "type": "string", "format": "uri", "maxLength": 2048 } },
          "required": ["url"],
          "additionalProperties": false
        }
        """),
        SideEffect = ToolSideEffect.External,
        RiskLevel = ToolRiskLevel.Medium,
        RequiredScopes = new[] { "agents:run" },
        CostWeight = 0.002m,
        Timeout = TimeSpan.FromSeconds(5),
        IsNaturallyIdempotent = true,
    };

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var url = ToolJson.OptString(arguments, "url") ?? string.Empty;

        var guard = _policy.Check(url);
        if (!guard.Allowed)
            return ToolResult.Fail(ToolError.Policy($"Request blocked by SSRF policy: {guard.Reason}"));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // Redirects are never followed: an off-list hop is a classic SSRF bypass.
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                return ToolResult.Fail(ToolError.Policy("Redirect responses are not followed by http_get."));

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > MaxBodyChars) body = body[..MaxBodyChars];

            return ToolResult.Ok(ToolJson.Serialize(new
            {
                status,
                content_type = response.Content.Headers.ContentType?.ToString(),
                body,
            }));
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail(new ToolError(ToolErrorCodes.DependencyFailure, $"HTTP request failed: {ex.Message}", Transient: true));
        }
    }
}
