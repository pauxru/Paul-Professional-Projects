using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iiot.Application;
using Iiot.Domain;
using Iiot.EdgeGateway;

namespace Iiot.Api;

public sealed record RegisterDeviceRequest(
    [property: Required] string DeviceId,
    [property: Required] string DeviceType,
    [property: Required] string Plant,
    [property: Required] string Line,
    [property: Required] string Asset,
    [property: Required] string FirmwareVersion,
    [property: Required, MinLength(12)] string DeviceKey,
    [property: Required] string EnrollmentToken,
    string? CertificateThumbprint);

public sealed record EnrollDeviceRequest([property: Required] string EnrollmentToken);
public sealed record TwinPatchRequest(int ExpectedVersion, Dictionary<string, string?> Properties);
public sealed record RuleConditionRequest(string Metric, string Comparison, decimal Threshold);
public sealed record CreateRuleRequest(
    [property: Required] string RuleId,
    [property: Required] string DeviceId,
    [property: Required] string Kind,
    [property: Required] string Metric,
    [property: Required] string Comparison,
    decimal Threshold,
    decimal Hysteresis,
    int DwellSeconds,
    int WindowSeconds,
    List<RuleConditionRequest>? Conditions,
    string? CompositeOperator,
    int? SuppressionSeconds,
    bool Enabled = true,
    bool IsMaintenanceSilenced = false);

public sealed record CreateCommandRequest(
    [property: Required] string DeviceId,
    [property: Required] string CommandType,
    Dictionary<string, string>? Parameters,
    int TimeoutSeconds = 30);

public sealed record FirmwareRequest([property: Required] string Version);
public sealed record NetworkToggleRequest(bool Online);

internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

internal static class ApiProblems
{
    public static IResult Domain(DomainRuleViolation exception) =>
        Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Domain rule rejected the request");

    public static IResult Validation(string field, string error) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });
}

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/devices");
        group.MapGet("", async (IDeviceRegistryStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListDevicesAsync(cancellationToken)))
            .RequireAuthorization(Policies.Operator);

        group.MapGet("/{deviceId}", async (string deviceId, IDeviceRegistryStore store, CancellationToken cancellationToken) =>
        {
            var device = await store.FindDeviceAsync(deviceId, cancellationToken);
            return device is null ? Results.NotFound() : Results.Ok(device);
        }).RequireAuthorization(Policies.Operator);

        group.MapPost("", async (RegisterDeviceRequest request, DeviceRegistryService registry, CancellationToken cancellationToken) =>
        {
            if (!Validate(request, out var error))
            {
                return ApiProblems.Validation("device", error);
            }

            try
            {
                var result = await registry.ProvisionAsync(
                    new DeviceRegistration(
                        request.DeviceId,
                        request.DeviceType,
                        request.Plant,
                        request.Line,
                        request.Asset,
                        request.FirmwareVersion,
                        request.EnrollmentToken,
                        request.CertificateThumbprint),
                    request.DeviceKey,
                    cancellationToken);
                return Results.Created($"/api/v1/devices/{request.DeviceId}", result);
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Admin);

        group.MapPost("/{deviceId}/enroll", async (string deviceId, EnrollDeviceRequest request, DeviceRegistryService registry, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await registry.EnrollAsync(deviceId, request.EnrollmentToken, cancellationToken));
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).AllowAnonymous();

        group.MapPost("/{deviceId}/credentials/revoke", async (string deviceId, DeviceRegistryService registry, CancellationToken cancellationToken) =>
        {
            try
            {
                await registry.RevokeAsync(deviceId, cancellationToken);
                return Results.NoContent();
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Admin);

        group.MapGet("/{deviceId}/credentials", async (string deviceId, IDeviceRegistryStore store, CancellationToken cancellationToken) =>
        {
            var credential = await store.FindCredentialAsync(deviceId, cancellationToken);
            return credential is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    credential.DeviceId,
                    hasDeviceKey = !string.IsNullOrWhiteSpace(credential.KeyHash),
                    credential.CertificateThumbprint,
                    credential.IsRevoked
                });
        }).RequireAuthorization(Policies.Admin);

        group.MapGet("/{deviceId}/twin", async (string deviceId, IDeviceRegistryStore store, CancellationToken cancellationToken) =>
        {
            var twin = await store.GetTwinAsync(deviceId, cancellationToken);
            return twin is null ? Results.NotFound() : Results.Ok(twin);
        }).RequireAuthorization(Policies.Operator);

        group.MapPatch("/{deviceId}/twin/desired", async (string deviceId, TwinPatchRequest request, DeviceRegistryService registry, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await registry.PatchTwinAsync(deviceId, request.Properties, request.ExpectedVersion, false, cancellationToken));
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Admin);

        group.MapPatch("/{deviceId}/twin/reported", async (string deviceId, TwinPatchRequest request, DeviceRegistryService registry, HttpRequest http, CancellationToken cancellationToken) =>
        {
            if (!await AuthenticateDeviceAsync(http, registry, deviceId, cancellationToken))
            {
                return Results.Problem("Device key authentication failed.", statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await registry.PatchTwinAsync(deviceId, request.Properties, request.ExpectedVersion, true, cancellationToken));
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).AllowAnonymous();
    }

    private static bool Validate(RegisterDeviceRequest request, out string error)
    {
        if (new[]
            {
                request.DeviceId, request.DeviceType, request.Plant, request.Line, request.Asset, request.FirmwareVersion, request.EnrollmentToken
            }.Any(string.IsNullOrWhiteSpace))
        {
            error = "Device identity, hierarchy, firmware and enrollment token are required.";
            return false;
        }

        if (request.DeviceKey.Length < 12)
        {
            error = "Device key must have at least 12 characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static async Task<bool> AuthenticateDeviceAsync(HttpRequest request, DeviceRegistryService registry, string expectedDeviceId, CancellationToken cancellationToken)
    {
        var deviceId = request.Headers["X-Device-Id"].ToString();
        var deviceKey = request.Headers["X-Device-Key"].ToString();
        return string.Equals(deviceId, expectedDeviceId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(deviceKey) &&
            await registry.AuthenticateDeviceAsync(deviceId, deviceKey, cancellationToken);
    }
}

public static class TelemetryEndpoints
{
    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/telemetry");
        group.MapPost("", IngestAsync).AllowAnonymous().RequireRateLimiting("device");
        group.MapGet("/{deviceId}", GetTelemetryAsync).RequireAuthorization(Policies.Operator);
        group.MapGet("/{deviceId}/rollups", GetRollupsAsync).RequireAuthorization(Policies.Operator);
    }

    private static async Task<IResult> IngestAsync(
        HttpRequest request,
        DeviceRegistryService registry,
        TelemetryIngestionService ingestion,
        AlertRuleOrchestrator alerts,
        IDeviceRegistryStore devices,
        CancellationToken cancellationToken)
    {
        var deviceId = request.Headers["X-Device-Id"].ToString();
        if (!await DeviceEndpoints.AuthenticateDeviceAsync(request, registry, deviceId, cancellationToken))
        {
            return Results.Problem("Device key authentication failed.", statusCode: StatusCodes.Status401Unauthorized);
        }

        Stream source = request.Body;
        GZipStream? gzip = null;
        try
        {
            if (request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                gzip = new GZipStream(request.Body, CompressionMode.Decompress, leaveOpen: true);
                source = gzip;
            }

            var readings = await JsonSerializer.DeserializeAsync<List<TelemetryReading>>(source, ApiJson.Options, cancellationToken);
            if (readings is null || readings.Count == 0)
            {
                return ApiProblems.Validation("telemetry", "At least one telemetry reading is required.");
            }

            if (readings.Any(reading => !string.Equals(reading.DeviceId, deviceId, StringComparison.Ordinal)))
            {
                return Results.Problem("A device credential may only ingest its own telemetry.", statusCode: StatusCodes.Status403Forbidden);
            }

            var result = await ingestion.IngestAsync(readings, cancellationToken);
            foreach (var reading in readings)
            {
                await alerts.EvaluateAsync(reading, cancellationToken);
            }
            foreach (var readingDeviceId in readings.Select(reading => reading.DeviceId).Distinct(StringComparer.Ordinal))
            {
                await devices.UpdateDeviceStatusAsync(readingDeviceId, DeviceStatus.Online, cancellationToken);
            }

            foreach (var perDevice in readings.GroupBy(reading => reading.DeviceId))
            {
                await ingestion.RecalculateRollupsAsync(
                    perDevice.Key,
                    perDevice.Min(reading => reading.DeviceTimestamp) - TimeSpan.FromMinutes(1),
                    perDevice.Max(reading => reading.DeviceTimestamp) + TimeSpan.FromMinutes(1),
                    cancellationToken);
            }

            IiotMetrics.IngestedReadings.Add(result.Accepted);
            return Results.Ok(new CloudIngestionReceipt(result.Accepted, result.Duplicates, result.Rejected));
        }
        catch (JsonException)
        {
            return ApiProblems.Validation("telemetry", "Telemetry JSON is malformed.");
        }
        finally
        {
            gzip?.Dispose();
        }
    }

    private static async Task<IResult> GetTelemetryAsync(
        string deviceId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        ITelemetryStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var until = to ?? clock.UtcNow;
        var since = from ?? until - TimeSpan.FromHours(1);
        if (since > until)
        {
            return ApiProblems.Validation("from", "from must be earlier than to.");
        }

        return Results.Ok(await store.QueryTelemetryAsync(deviceId, since, until, cancellationToken));
    }

    private static async Task<IResult> GetRollupsAsync(
        string deviceId,
        string metric,
        string resolution,
        DateTimeOffset? from,
        DateTimeOffset? to,
        ITelemetryStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SensorMetric>(metric, true, out var parsedMetric))
        {
            return ApiProblems.Validation("metric", "Unknown metric.");
        }

        var parsedResolution = resolution.Equals("hour", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromHours(1)
            : resolution.Equals("minute", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromMinutes(1)
                : TimeSpan.Zero;
        if (parsedResolution == TimeSpan.Zero)
        {
            return ApiProblems.Validation("resolution", "resolution must be minute or hour.");
        }

        var until = to ?? clock.UtcNow;
        var since = from ?? until - TimeSpan.FromHours(24);
        return Results.Ok(await store.QueryRollupsAsync(deviceId, parsedMetric, parsedResolution, since, until, cancellationToken));
    }
}

public static class RuleEndpoints
{
    public static void MapRuleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/rules");
        group.MapGet("", async (string? deviceId, IRuleStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListRulesAsync(deviceId, cancellationToken)))
            .RequireAuthorization(Policies.Operator);
        group.MapPost("", async (CreateRuleRequest request, IRuleStore store, CancellationToken cancellationToken) =>
        {
            if (!TryCreate(request, out var rule, out var error))
            {
                return ApiProblems.Validation("rule", error);
            }

            await store.SaveRuleAsync(rule!, cancellationToken);
            return Results.Created($"/api/v1/rules/{rule!.RuleId}", rule);
        }).RequireAuthorization(Policies.Admin);
        group.MapDelete("/{ruleId}", async (string ruleId, IRuleStore store, CancellationToken cancellationToken) =>
            await store.DeleteRuleAsync(ruleId, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(Policies.Admin);
    }

    private static bool TryCreate(CreateRuleRequest request, out RuleDefinition? rule, out string error)
    {
        rule = null;
        if (!Enum.TryParse<RuleKind>(request.Kind, true, out var kind) ||
            !Enum.TryParse<SensorMetric>(request.Metric, true, out var metric) ||
            !Enum.TryParse<RuleComparison>(request.Comparison, true, out var comparison) ||
            (request.CompositeOperator is not null && !Enum.TryParse<CompositeOperator>(request.CompositeOperator, true, out var composite)))
        {
            error = "Rule kind, metric, comparison or composite operator is invalid.";
            return false;
        }

        var effectiveComposite = request.CompositeOperator is null
            ? CompositeOperator.And
            : Enum.Parse<CompositeOperator>(request.CompositeOperator, true);
        var conditions = request.Conditions?.Select(item =>
        {
            if (!Enum.TryParse<SensorMetric>(item.Metric, true, out var conditionMetric) ||
                !Enum.TryParse<RuleComparison>(item.Comparison, true, out var conditionComparison))
            {
                throw new ArgumentException("Composite condition contains an invalid metric or comparison.");
            }

            return new RuleCondition(conditionMetric, conditionComparison, item.Threshold);
        }).ToArray();
        try
        {
            if (string.IsNullOrWhiteSpace(request.RuleId) || string.IsNullOrWhiteSpace(request.DeviceId) ||
                request.DwellSeconds < 0 || request.WindowSeconds < 0 || request.SuppressionSeconds < 0 ||
                (kind == RuleKind.Composite && (conditions is null || conditions.Length == 0)))
            {
                throw new ArgumentException("Rule identifiers, non-negative durations, and composite conditions are required.");
            }

            rule = new RuleDefinition(
                request.RuleId,
                request.DeviceId,
                kind,
                metric,
                comparison,
                request.Threshold,
                request.Hysteresis,
                TimeSpan.FromSeconds(request.DwellSeconds),
                TimeSpan.FromSeconds(request.WindowSeconds),
                conditions,
                effectiveComposite,
                request.SuppressionSeconds is null ? null : TimeSpan.FromSeconds(request.SuppressionSeconds.Value),
                request.Enabled,
                request.IsMaintenanceSilenced);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }
}

public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/alerts");
        group.MapGet("", async (bool activeOnly, IAlertStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAlertsAsync(activeOnly, cancellationToken)))
            .RequireAuthorization(Policies.Operator);
        group.MapPost("/{alertId}/acknowledge", async (string alertId, AlertRuleOrchestrator orchestrator, CancellationToken cancellationToken) =>
        {
            try
            {
                var alert = await orchestrator.AcknowledgeAsync(alertId, cancellationToken);
                IiotMetrics.AlertEvents.Add(1);
                return Results.Ok(alert);
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Operator);
        group.MapGet("/escalations", async (int? afterSeconds, string? target, AlertEscalationService escalation, CancellationToken cancellationToken) =>
        {
            try
            {
                var due = await escalation.GetDueAsync(
                    new EscalationPolicy(TimeSpan.FromSeconds(afterSeconds ?? 300), target ?? "on-call-operator"),
                    cancellationToken);
                return Results.Ok(due);
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Operator);
    }
}

public static class CommandEndpoints
{
    public static void MapCommandEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/commands");
        group.MapPost("", async (CreateCommandRequest request, CommandService commands, ClaimsPrincipal user, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (request.TimeoutSeconds is < 1 or > 3_600 || string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.CommandType))
            {
                return ApiProblems.Validation("command", "Device, command type and a timeout between 1 and 3600 seconds are required.");
            }

            try
            {
                var command = await commands.QueueAsync(
                    new CommandRequest(
                        request.DeviceId,
                        request.CommandType,
                        request.Parameters ?? new Dictionary<string, string>(),
                        user.FindFirstValue("sub") ?? "operator",
                        context.Response.Headers["X-Correlation-Id"].ToString(),
                        TimeSpan.FromSeconds(request.TimeoutSeconds)),
                    cancellationToken);
                return Results.Created($"/api/v1/commands/{command.CommandId}", command);
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Operator);
        group.MapGet("", async (string? deviceId, ICommandStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListCommandsAsync(deviceId, cancellationToken)))
            .RequireAuthorization(Policies.Operator);
        group.MapGet("/{commandId}", async (string commandId, ICommandStore store, CancellationToken cancellationToken) =>
        {
            var command = await store.FindCommandAsync(commandId, cancellationToken);
            return command is null ? Results.NotFound() : Results.Ok(command);
        }).RequireAuthorization(Policies.Operator);
        group.MapGet("/{commandId}/audit", async (string commandId, ICommandStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAuditAsync(commandId, cancellationToken)))
            .RequireAuthorization(Policies.Operator);
        group.MapPost("/{commandId}/status/{status}", async (string commandId, string status, CommandService commands, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<CommandStatus>(status, true, out var parsed))
            {
                return ApiProblems.Validation("status", "Unknown command status.");
            }

            try
            {
                var updated = await commands.TransitionAsync(commandId, parsed, user.FindFirstValue("sub") ?? "operator", cancellationToken: cancellationToken);
                if (updated.CompletedAt is { } completed && updated.SentAt is { } sent)
                {
                    IiotMetrics.CommandLatencyMs.Record((completed - sent).TotalMilliseconds);
                }

                return Results.Ok(updated);
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Operator);
        group.MapPost("/{commandId}/retry", async (string commandId, CommandService commands, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await commands.RetryAsync(commandId, user.FindFirstValue("sub") ?? "operator", cancellationToken));
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Operator);
    }
}

public static class FirmwareEndpoints
{
    public static void MapFirmwareEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/firmware");
        group.MapPost("/{deviceId}", async (string deviceId, FirmwareRequest request, IDeviceRegistryStore store, DeviceRegistryService registry, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Version))
            {
                return ApiProblems.Validation("version", "Firmware version is required.");
            }

            var twin = await store.GetTwinAsync(deviceId, cancellationToken);
            if (twin is null)
            {
                return Results.NotFound();
            }

            try
            {
                var updated = await registry.PatchTwinAsync(
                    deviceId,
                    new Dictionary<string, string?> { ["firmwareVersion"] = request.Version, ["otaStage"] = "downloadRequested" },
                    twin.Version,
                    false,
                    cancellationToken);
                return Results.Accepted($"/api/v1/devices/{deviceId}/twin", updated);
            }
            catch (DomainRuleViolation exception)
            {
                return ApiProblems.Domain(exception);
            }
        }).RequireAuthorization(Policies.Admin);
    }
}
