using System.Diagnostics;
using Lab.Diagnostics.Measurement;

namespace Lab.Scenarios.Incidents;

public sealed class PoisonQueueScenario : IIncidentScenario
{
    public string Id => "INC-009";

    public string Name => "Poison queue message partition blockage";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var healthyMessages = options.BoundedRequests(3, 20);
        const int fixedMaximumAttempts = 3;
        const int brokenSafetyDeliveryBudget = 12;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();
        var queue = new LinkedList<QueueMessage>();
        queue.AddLast(new QueueMessage("poison-001", isPoison: true));
        for (var index = 0; index < healthyMessages; index++)
        {
            queue.AddLast(new QueueMessage($"healthy-{index:D3}", isPoison: false));
        }

        var deliveries = 0;
        var redeliveries = 0;
        var processedHealthy = 0;
        var deadLettered = 0;
        while (queue.First is not null)
        {
            token.ThrowIfCancellationRequested();
            if (options.Mode == ScenarioMode.Broken && deliveries >= brokenSafetyDeliveryBudget)
            {
                break;
            }

            var message = queue.First.Value;
            queue.RemoveFirst();
            deliveries++;
            message.Attempts++;

            if (message.IsPoison)
            {
                if (options.Mode == ScenarioMode.Fixed && message.Attempts >= fixedMaximumAttempts)
                {
                    deadLettered++;
                }
                else
                {
                    redeliveries++;
                    queue.AddFirst(message);
                }
            }
            else
            {
                processedHealthy++;
            }

            await Task.Yield();
        }

        var outcome = session.Complete();
        return new ScenarioReport
        {
            ScenarioId = Id,
            ScenarioName = Name,
            Mode = options.Mode,
            RequestedOperations = healthyMessages,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = outcome.Elapsed.TotalMilliseconds,
            Metrics = new Dictionary<string, object?>
            {
                ["healthyMessagesProcessed"] = processedHealthy,
                ["poisonDeliveries"] = deliveries - processedHealthy,
                ["redeliveries"] = redeliveries,
                ["deadLettered"] = deadLettered,
                ["remainingPartitionMessages"] = queue.Count,
                ["healthyThroughputPerSecond"] = Math.Round(processedHealthy / Math.Max(outcome.Elapsed.TotalSeconds, 0.001), 2),
                ["brokenSafetyDeliveryBudget"] = brokenSafetyDeliveryBudget
            },
            Evidence =
            [
                "The poison item is reinserted at the head of one in-process partition, so it blocks later healthy messages.",
                "Broken mode is deliberately stopped after 12 deliveries; the production anti-pattern would redeliver indefinitely."
            ],
            Limitations =
            [
                "This is an in-process queue model. Broker lock durations, partition rebalancing, and operational dead-letter tooling vary by messaging platform."
            ]
        };
    }

    private sealed class QueueMessage(string id, bool isPoison)
    {
        public string Id { get; } = id;

        public bool IsPoison { get; } = isPoison;

        public int Attempts { get; set; }
    }
}
