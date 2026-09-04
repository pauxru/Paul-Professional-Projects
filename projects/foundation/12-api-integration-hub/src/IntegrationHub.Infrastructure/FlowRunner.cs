using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntegrationHub.Application;
using IntegrationHub.Domain;

namespace IntegrationHub.Infrastructure;

public sealed class FlowRunner(
    IFlowStore flows,
    IFlowDefinitionParser parser,
    IConnectorRegistry connectors,
    IExecutionStore executions,
    IDeadLetterStore deadLetters,
    ICheckpointStore checkpoints,
    IIdempotencyStore idempotencyStore,
    IContractDriftStore driftStore,
    ISecretRedactor redactor,
    SafeExpressionEvaluator evaluator,
    PayloadContractValidator contractValidator,
    SchemaDriftDetector driftDetector,
    IClock clock,
    IIdGenerator ids) : IFlowRunner
{
    public const string MeterName = "IntegrationHub.Execution";
    public const string ActivitySourceName = "IntegrationHub.FlowExecution";
    private static readonly ActivitySource Activity = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> RunCounter = Meter.CreateCounter<long>("integrationhub.runs");
    private static readonly Counter<long> RecordCounter = Meter.CreateCounter<long>("integrationhub.records.processed");
    private static readonly Counter<long> DeadLetterCounter = Meter.CreateCounter<long>("integrationhub.dlq.added");
    private static readonly UpDownCounter<long> DeadLetterDepth = Meter.CreateUpDownCounter<long>("integrationhub.dlq.depth");
    private static readonly Counter<long> RetryCounter = Meter.CreateCounter<long>("integrationhub.step.retries");
    private static readonly Histogram<double> StepDuration = Meter.CreateHistogram<double>("integrationhub.step.duration.ms");

    public async Task<RunSnapshot> RunAsync(
        Guid flowId,
        JsonNode? triggerPayload,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var flow = await flows.GetAsync(flowId, cancellationToken)
                   ?? throw new KeyNotFoundException("Flow not found.");
        var active = flow.ActiveVersion is int versionNumber
            ? flow.Versions.Single(x => x.Version == versionNumber)
            : throw new InvalidOperationException("Flow has no active version.");
        var definition = parser.Parse(active.Format, active.Definition);
        var run = new RunSnapshot(
            ids.NewId(), flowId, active.Version, RunStatus.Running, correlationId, clock.UtcNow);
        await executions.CreateRunAsync(run, cancellationToken);
        using var runActivity = Activity.StartActivity("flow.run", ActivityKind.Internal);
        runActivity?.SetTag("flow.id", flowId);
        runActivity?.SetTag("flow.version", active.Version);
        runActivity?.SetTag("run.id", run.Id);

        var records = NormalizeRecords(triggerPayload);
        var quarantined = 0;
        try
        {
            foreach (var step in definition.Steps)
            {
                var started = clock.UtcNow;
                var timer = Stopwatch.StartNew();
                var input = records.DeepClone().AsArray();
                string? error = null;
                using var activity = Activity.StartActivity($"flow.step.{step.Kind}", ActivityKind.Internal);
                activity?.SetTag("step.id", step.Id);
                activity?.SetTag("records.in", records.Count);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

                try
                {
                    var stepRetry = new RetryExecutor();
                    var result = await stepRetry.ExecuteAsync(
                        async (_, token) =>
                        {
                            try
                            {
                                return new RetryOutcome<StepResult>(
                                    true,
                                    await ExecuteStepAsync(flowId, run.Id, step, records, correlationId, token));
                            }
                            catch (Exception ex)
                            {
                                var transient = ex is TimeoutException or HttpRequestException
                                                || ex is ConnectorException { StatusCode: 429 or >= 500 };
                                return new RetryOutcome<StepResult>(
                                    false,
                                    IsTransient: transient,
                                    Error: ex);
                            }
                        },
                        step.RetryCount + 1,
                        TimeSpan.FromMilliseconds(250),
                        (_, _) => RetryCounter.Add(1,
                            new KeyValuePair<string, object?>("step.kind", step.Kind.ToString())),
                        timeout.Token);
                    records = result.Records;
                    quarantined += result.Quarantined;
                }
                catch (Exception ex)
                {
                    error = redactor.Redact(ex.ToString());
                    await executions.AppendStepAsync(new StepSnapshot(
                        ids.NewId(), run.Id, step.Id, step.Kind, started, clock.UtcNow,
                        Snapshot(input), Snapshot(records), input.Count, records.Count, error), cancellationToken);
                    await executions.CompleteRunAsync(run.Id, RunStatus.Failed, records.Count, error, cancellationToken);
                    RunCounter.Add(1, new KeyValuePair<string, object?>("status", "failed"));
                    return run with { Status = RunStatus.Failed, CompletedAt = clock.UtcNow, RecordsProcessed = records.Count, Error = error };
                }
                finally
                {
                    timer.Stop();
                    StepDuration.Record(timer.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("step.kind", step.Kind.ToString()));
                }

                await executions.AppendStepAsync(new StepSnapshot(
                    ids.NewId(), run.Id, step.Id, step.Kind, started, clock.UtcNow,
                    Snapshot(input), Snapshot(records), input.Count, records.Count, error), cancellationToken);
            }

            var status = quarantined > 0 ? RunStatus.PartiallySucceeded : RunStatus.Succeeded;
            await executions.CompleteRunAsync(run.Id, status, records.Count, null, cancellationToken);
            RunCounter.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
            RecordCounter.Add(records.Count);
            return run with { Status = status, CompletedAt = clock.UtcNow, RecordsProcessed = records.Count };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await executions.CompleteRunAsync(run.Id, RunStatus.Cancelled, records.Count, "Run cancelled.", CancellationToken.None);
            throw;
        }
    }

    public async Task<RunSnapshot> ReplayAsync(
        Guid? itemId,
        Guid? runId,
        string? batchKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var items = await deadLetters.GetForReplayAsync(itemId, runId, batchKey, cancellationToken);
        if (items.Count == 0)
        {
            throw new KeyNotFoundException("No pending dead-letter records matched the replay request.");
        }

        var original = await executions.GetRunAsync(items[0].RunId, cancellationToken)
                       ?? throw new KeyNotFoundException("Original run not found.");
        var replay = new RunSnapshot(
            ids.NewId(),
            original.Run.FlowId,
            original.Run.FlowVersion,
            RunStatus.Running,
            correlationId,
            clock.UtcNow);
        await executions.CreateRunAsync(replay, cancellationToken);
        var idempotent = new IdempotentExecutor(idempotencyStore, clock);
        var failed = 0;

        foreach (var item in items)
        {
            try
            {
                var envelope = JsonNode.Parse(item.Payload)?.AsObject()
                               ?? throw new ConnectorException("Dead-letter envelope is invalid.");
                var connectorId = envelope["connectorId"]?.GetValue<string>()
                                  ?? throw new ConnectorException("Dead-letter connector is missing.");
                var operation = envelope["operation"]?.GetValue<string>()
                                ?? throw new ConnectorException("Dead-letter operation is missing.");
                var payload = envelope["payload"];
                await idempotent.ExecuteAsync(
                    $"replay:{connectorId}:{operation}",
                    item.RecordKey,
                    async () =>
                    {
                        var response = await connectors.Get(connectorId).ExecuteAsync(
                            operation,
                            payload,
                            new ConnectorExecutionContext(correlationId, item.RecordKey),
                            cancellationToken);
                        return new IdempotencyResult(
                            response.StatusCode,
                            response.Payload?.ToJsonString() ?? "null",
                            clock.UtcNow);
                    },
                    cancellationToken);
                await deadLetters.MarkReplayedAsync(item.Id, replay.Id, cancellationToken);
                DeadLetterDepth.Add(-1);
            }
            catch
            {
                failed++;
            }
        }

        var status = failed == 0 ? RunStatus.Succeeded : RunStatus.PartiallySucceeded;
        await executions.CompleteRunAsync(replay.Id, status, items.Count - failed, failed == 0 ? null : $"{failed} replay item(s) failed.", cancellationToken);
        return replay with
        {
            Status = status,
            CompletedAt = clock.UtcNow,
            RecordsProcessed = items.Count - failed,
            Error = failed == 0 ? null : $"{failed} replay item(s) failed."
        };
    }

    private async Task<StepResult> ExecuteStepAsync(
        Guid flowId,
        Guid runId,
        FlowStepDefinition step,
        JsonArray records,
        string correlationId,
        CancellationToken cancellationToken)
    {
        return step.Kind switch
        {
            FlowStepKind.Trigger or FlowStepKind.Respond => new StepResult(records, 0),
            FlowStepKind.Fetch => new StepResult(await FetchAsync(step, correlationId, cancellationToken), 0),
            FlowStepKind.Transform => Transform(step, records),
            FlowStepKind.Filter => Filter(step, records),
            FlowStepKind.Enrich => new StepResult(await EnrichAsync(step, records, correlationId, cancellationToken), 0),
            FlowStepKind.Route => Route(step, records),
            FlowStepKind.Load => await LoadAsync(flowId, runId, step, records, correlationId, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported flow step {step.Kind}.")
        };
    }

    private async Task<JsonArray> FetchAsync(
        FlowStepDefinition step,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var connector = connectors.Get(GetRequired(step, "connectorId"), GetOptional(step, "connectorVersion"));
        var operation = GetRequired(step, "operation");
        var input = GetOptional(step, "input") is { } inputJson ? JsonNode.Parse(inputJson) : null;
        var result = await connector.ExecuteAsync(
            operation,
            input,
            new ConnectorExecutionContext(correlationId, PageSize: GetInt(step, "pageSize", 100)),
            cancellationToken);
        var records = NormalizeRecords(result.Payload);

        var contract = connector.Descriptor.Operations.Single(x => x.Name == operation).OutputContract;
        if (contract is not null)
        {
            foreach (var record in records)
            {
                var report = driftDetector.Detect(record, contract);
                foreach (var change in report.Changes)
                {
                    await driftStore.AddAsync(new ContractDriftAlert(
                        ids.NewId(), connector.Descriptor.Id, operation, change.Path, change.Change, clock.UtcNow), cancellationToken);
                }
                var violations = contractValidator.Validate(record, contract);
                if (violations.Count > 0)
                {
                    throw new ConnectorException($"Connector contract validation failed: {string.Join("; ", violations.Select(x => x.Path))}");
                }
            }
        }
        return records;
    }

    private StepResult Transform(FlowStepDefinition step, JsonArray records)
    {
        var mappings = JsonSerializer.Deserialize<List<FieldMapping>>(GetRequired(step, "mappings"),
                           new JsonSerializerOptions(JsonSerializerDefaults.Web))
                       ?? throw new SafeExpressionException("Mappings are invalid.");
        var engine = new MappingEngine(evaluator);
        var output = new JsonArray();
        var quarantined = 0;
        foreach (var record in records)
        {
            var result = engine.Map(record, mappings);
            if (result.Trace.Any(x => !x.Success))
            {
                quarantined++;
                continue;
            }
            output.Add(result.Output);
        }
        return new StepResult(output, quarantined);
    }

    private StepResult Filter(FlowStepDefinition step, JsonArray records)
    {
        var expression = GetRequired(step, "expression");
        var output = new JsonArray(records
            .Where(record => evaluator.Evaluate(expression, record) is JsonValue value
                             && value.TryGetValue<bool>(out var include)
                             && include)
            .Select(x => x?.DeepClone())
            .ToArray());
        return new StepResult(output, 0);
    }

    private async Task<JsonArray> EnrichAsync(
        FlowStepDefinition step,
        JsonArray records,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var connector = connectors.Get(GetRequired(step, "connectorId"), GetOptional(step, "connectorVersion"));
        var operation = GetRequired(step, "operation");
        var targetPath = GetRequired(step, "targetPath");
        var output = new JsonArray();
        foreach (var record in records.OfType<JsonObject>())
        {
            var clone = record.DeepClone().AsObject();
            var result = await connector.ExecuteAsync(
                operation,
                clone,
                new ConnectorExecutionContext(correlationId),
                cancellationToken);
            JsonPath.Set(clone, targetPath, result.Payload?.DeepClone());
            output.Add(clone);
        }
        return output;
    }

    private StepResult Route(FlowStepDefinition step, JsonArray records)
    {
        var expression = GetRequired(step, "expression");
        var trueRoute = GetOptional(step, "trueRoute") ?? "matched";
        var falseRoute = GetOptional(step, "falseRoute") ?? "default";
        var output = new JsonArray();
        foreach (var record in records.OfType<JsonObject>())
        {
            var clone = record.DeepClone().AsObject();
            var matched = evaluator.Evaluate(expression, clone) is JsonValue value
                          && value.TryGetValue<bool>(out var include)
                          && include;
            clone["_route"] = matched ? trueRoute : falseRoute;
            output.Add(clone);
        }
        return new StepResult(output, 0);
    }

    private async Task<StepResult> LoadAsync(
        Guid flowId,
        Guid runId,
        FlowStepDefinition step,
        JsonArray records,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var connectorId = GetRequired(step, "connectorId");
        var connector = connectors.Get(connectorId, GetOptional(step, "connectorVersion"));
        var operation = GetRequired(step, "operation");
        var batchKey = GetOptional(step, "batchKey") ?? "default";
        var processor = new CheckpointedBatchProcessor(checkpoints, deadLetters, ids, clock);
        var result = await processor.ProcessAsync(
            runId,
            step.Id,
            batchKey,
            records.ToList(),
            async (record, index, token) =>
            {
                var recordKey = GetRecordKey(flowId, step.Id, record, index);
                await new IdempotentExecutor(idempotencyStore, clock).ExecuteAsync(
                    $"load:{connectorId}:{operation}",
                    recordKey,
                    async () =>
                    {
                        var response = await connector.ExecuteAsync(
                            operation,
                            record,
                            new ConnectorExecutionContext(correlationId, recordKey),
                            token);
                        return new IdempotencyResult(
                            response.StatusCode,
                            response.Payload?.ToJsonString() ?? "null",
                            clock.UtcNow);
                    },
                    token);
            },
            (record, index) => GetRecordKey(flowId, step.Id, record, index),
            record => new JsonObject
            {
                ["connectorId"] = connectorId,
                ["operation"] = operation,
                ["payload"] = record?.DeepClone()
            }.ToJsonString(),
            ex => ex is ConnectorException connectorError && connectorError.StatusCode is >= 400 and < 500,
            cancellationToken);

        if (result.Quarantined > 0)
        {
            DeadLetterCounter.Add(result.Quarantined);
            DeadLetterDepth.Add(result.Quarantined);
        }
        return new StepResult(records, result.Quarantined);
    }

    private string Snapshot(JsonNode? value) =>
        redactor.Redact(value)?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";

    private static JsonArray NormalizeRecords(JsonNode? value) =>
        value switch
        {
            JsonArray array => new JsonArray(array.Select(x => x?.DeepClone()).ToArray()),
            null => [],
            _ => [value.DeepClone()]
        };

    private static string GetRequired(FlowStepDefinition step, string key) =>
        step.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Step '{step.Id}' requires setting '{key}'.");

    private static string? GetOptional(FlowStepDefinition step, string key) =>
        step.Settings.TryGetValue(key, out var value) ? value : null;

    private static int GetInt(FlowStepDefinition step, string key, int fallback) =>
        step.Settings.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static string GetRecordKey(Guid flowId, string stepId, JsonNode? record, int index)
    {
        var sourceId = JsonPath.Get(record, "$.id")?.ToString()
                       ?? JsonPath.Get(record, "$.externalId")?.ToString()
                       ?? index.ToString();
        return $"{flowId:N}:{stepId}:{sourceId}";
    }

    private sealed record StepResult(JsonArray Records, int Quarantined);
}
