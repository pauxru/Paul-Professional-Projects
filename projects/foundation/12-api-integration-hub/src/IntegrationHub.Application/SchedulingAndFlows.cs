using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IntegrationHub.Domain;

namespace IntegrationHub.Application;

public sealed class CronExpression
{
    private readonly CronField _minute;
    private readonly CronField _hour;
    private readonly CronField _day;
    private readonly CronField _month;
    private readonly CronField _dayOfWeek;

    private CronExpression(string expression)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            throw new FormatException("Cron expressions must contain five fields.");
        }

        _minute = CronField.Parse(fields[0], 0, 59);
        _hour = CronField.Parse(fields[1], 0, 23);
        _day = CronField.Parse(fields[2], 1, 31);
        _month = CronField.Parse(fields[3], 1, 12);
        _dayOfWeek = CronField.Parse(fields[4], 0, 6);
    }

    public static CronExpression Parse(string expression) => new(expression);

    public DateTimeOffset GetNextOccurrence(DateTimeOffset after)
    {
        var candidate = new DateTimeOffset(
            after.UtcDateTime.Year,
            after.UtcDateTime.Month,
            after.UtcDateTime.Day,
            after.UtcDateTime.Hour,
            after.UtcDateTime.Minute,
            0,
            TimeSpan.Zero).AddMinutes(1);

        for (var i = 0; i < 366 * 24 * 60 * 5; i++, candidate = candidate.AddMinutes(1))
        {
            if (_minute.Matches(candidate.Minute)
                && _hour.Matches(candidate.Hour)
                && _day.Matches(candidate.Day)
                && _month.Matches(candidate.Month)
                && _dayOfWeek.Matches((int)candidate.DayOfWeek))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No cron occurrence found within five years.");
    }

    private sealed class CronField(HashSet<int> values)
    {
        public bool Matches(int value) => values.Contains(value);

        public static CronField Parse(string text, int minimum, int maximum)
        {
            var values = new HashSet<int>();
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "*")
                {
                    AddRange(values, minimum, maximum, 1);
                }
                else if (part.StartsWith("*/", StringComparison.Ordinal)
                         && int.TryParse(part[2..], out var step)
                         && step > 0)
                {
                    AddRange(values, minimum, maximum, step);
                }
                else if (part.Contains('-'))
                {
                    var range = part.Split('-', 2);
                    if (range.Length != 2
                        || !int.TryParse(range[0], out var start)
                        || !int.TryParse(range[1], out var end)
                        || start < minimum
                        || end > maximum
                        || start > end)
                    {
                        throw new FormatException($"Invalid cron range '{part}'.");
                    }
                    AddRange(values, start, end, 1);
                }
                else if (int.TryParse(part, out var exact) && exact >= minimum && exact <= maximum)
                {
                    values.Add(exact);
                }
                else
                {
                    throw new FormatException($"Invalid cron field '{part}'.");
                }
            }

            return new CronField(values);
        }

        private static void AddRange(HashSet<int> values, int start, int end, int step)
        {
            for (var value = start; value <= end; value += step)
            {
                values.Add(value);
            }
        }
    }
}

public sealed record ScheduleDecision(bool ShouldRun, DateTimeOffset NextDue, string Reason);

public sealed class FlowScheduleState
{
    public DateTimeOffset? LastScheduledAt { get; set; }
    public bool IsRunning { get; set; }
}

public sealed class FlowScheduler(IClock clock)
{
    public ScheduleDecision Evaluate(
        CronExpression cron,
        FlowScheduleState state,
        string catchUpPolicy,
        TimeSpan misfireGrace)
    {
        var baseline = state.LastScheduledAt ?? clock.UtcNow.AddMinutes(-1);
        var due = cron.GetNextOccurrence(baseline);
        if (due > clock.UtcNow)
        {
            return new ScheduleDecision(false, due, "not-due");
        }

        if (state.IsRunning)
        {
            return new ScheduleDecision(false, cron.GetNextOccurrence(clock.UtcNow), "overlap-prevented");
        }

        if (clock.UtcNow - due > misfireGrace && string.Equals(catchUpPolicy, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return new ScheduleDecision(false, cron.GetNextOccurrence(clock.UtcNow), "misfire-skipped");
        }

        if (clock.UtcNow - due > misfireGrace && string.Equals(catchUpPolicy, "latest", StringComparison.OrdinalIgnoreCase))
        {
            var next = cron.GetNextOccurrence(due);
            while (next <= clock.UtcNow)
            {
                due = next;
                next = cron.GetNextOccurrence(due);
            }
        }

        return new ScheduleDecision(true, due, clock.UtcNow > due ? "catch-up" : "on-time");
    }
}

public sealed class FlowDefinitionParser : IFlowDefinitionParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static FlowDefinitionParser()
    {
        Options.Converters.Add(new JsonStringEnumConverter());
    }

    public FlowDefinition Parse(string format, string definition)
    {
        FlowDefinition parsed;
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            parsed = JsonSerializer.Deserialize<FlowDefinition>(definition, Options)
                     ?? throw new DomainValidationException("Flow definition is empty.");
        }
        else if (string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase))
        {
            parsed = ParseYaml(definition);
        }
        else
        {
            throw new DomainValidationException("Flow format must be json or yaml.");
        }

        parsed.Validate();
        return parsed;
    }

    public string Serialize(string format, FlowDefinition definition)
    {
        definition.Validate();
        return string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase)
            ? SerializeYaml(definition)
            : JsonSerializer.Serialize(definition, Options);
    }

    private static FlowDefinition ParseYaml(string text)
    {
        string? name = null;
        var triggerKind = FlowTriggerKind.Manual;
        string? cron = null;
        var catchUp = "latest";
        string? webhookPath = null;
        var steps = new List<FlowStepDefinition>();
        Dictionary<string, string>? currentSettings = null;
        string? currentId = null;
        var currentKind = FlowStepKind.Trigger;
        var timeout = 30;
        var retries = 3;
        var inSettings = false;

        void Flush()
        {
            if (currentId is null)
            {
                return;
            }
            steps.Add(new FlowStepDefinition(currentId, currentKind, currentSettings ?? new Dictionary<string, string>(), timeout, retries));
            currentId = null;
            currentSettings = null;
            timeout = 30;
            retries = 3;
            inSettings = false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf(':');
            if (separator < 0)
            {
                throw new DomainValidationException($"Unsupported YAML line: {trimmed}");
            }

            var key = trimmed[..separator].TrimStart('-', ' ');
            var value = Unquote(trimmed[(separator + 1)..].Trim());
            var indent = line.Length - line.TrimStart().Length;

            if (indent == 0 && key == "name")
            {
                name = value;
            }
            else if (indent <= 2 && key == "kind" && currentId is null)
            {
                triggerKind = Enum.Parse<FlowTriggerKind>(value, true);
            }
            else if (currentId is null && key == "cron")
            {
                cron = value;
            }
            else if (currentId is null && key == "catchUpPolicy")
            {
                catchUp = value;
            }
            else if (currentId is null && key == "webhookPath")
            {
                webhookPath = value;
            }
            else if (trimmed.StartsWith("- id:", StringComparison.Ordinal))
            {
                Flush();
                currentId = value;
                currentSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else if (currentId is not null && key == "kind")
            {
                currentKind = Enum.Parse<FlowStepKind>(value, true);
            }
            else if (currentId is not null && key == "timeoutSeconds")
            {
                timeout = int.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (currentId is not null && key == "retryCount")
            {
                retries = int.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (currentId is not null && key == "settings")
            {
                inSettings = true;
            }
            else if (currentId is not null && inSettings)
            {
                currentSettings![key] = value;
            }
        }

        Flush();
        return new FlowDefinition(
            name ?? throw new DomainValidationException("YAML flow name is required."),
            new TriggerDefinition(triggerKind, cron, catchUp, webhookPath),
            steps);
    }

    private static string SerializeYaml(FlowDefinition definition)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"name: {definition.Name}");
        builder.AppendLine("trigger:");
        builder.AppendLine($"  kind: {definition.Trigger.Kind}");
        if (definition.Trigger.Cron is not null)
        {
            builder.AppendLine($"  cron: \"{definition.Trigger.Cron}\"");
        }
        builder.AppendLine($"  catchUpPolicy: {definition.Trigger.CatchUpPolicy}");
        if (definition.Trigger.WebhookPath is not null)
        {
            builder.AppendLine($"  webhookPath: {definition.Trigger.WebhookPath}");
        }
        builder.AppendLine("steps:");
        foreach (var step in definition.Steps)
        {
            builder.AppendLine($"  - id: {step.Id}");
            builder.AppendLine($"    kind: {step.Kind}");
            builder.AppendLine($"    timeoutSeconds: {step.TimeoutSeconds}");
            builder.AppendLine($"    retryCount: {step.RetryCount}");
            builder.AppendLine("    settings:");
            foreach (var setting in step.Settings)
            {
                builder.AppendLine($"      {setting.Key}: \"{setting.Value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
            }
        }
        return builder.ToString();
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
            : value;
}
