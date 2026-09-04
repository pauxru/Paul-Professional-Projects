using Northstar.Reliability.Domain.Incidents;
using Northstar.Reliability.Domain.Slo;

namespace Northstar.Reliability.Domain.Reporting;

public sealed record AlertObservation(string ServiceSlug, string RuleName, AlertState State, DateTimeOffset At);

public sealed record FlappingAlert(string ServiceSlug, string RuleName, int StateTransitions, DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt);

public sealed record AlertQualityReport(
    int TotalAlerts,
    int TotalIncidents,
    decimal AlertToIncidentRatio,
    decimal FalsePositiveRate,
    IReadOnlyList<FlappingAlert> FlappingAlerts);

public static class AlertQualityAnalyzer
{
    public static AlertQualityReport Analyze(
        IEnumerable<AlertInstance> alerts,
        IEnumerable<Incident> incidents,
        IEnumerable<AlertObservation>? observations = null,
        int flappingTransitionThreshold = 3)
    {
        var alertList = alerts.ToArray();
        var incidentList = incidents.ToArray();
        var falsePositives = alertList.Count(alert => alert.State == AlertState.Resolved && !alert.LinkedIncidentId.HasValue);
        var flapping = DetectFlapping(observations ?? [], flappingTransitionThreshold);
        return new AlertQualityReport(
            alertList.Length,
            incidentList.Length,
            incidentList.Length == 0 ? 0m : (decimal)alertList.Length / incidentList.Length,
            alertList.Length == 0 ? 0m : (decimal)falsePositives / alertList.Length * 100m,
            flapping);
    }

    public static IReadOnlyList<FlappingAlert> DetectFlapping(
        IEnumerable<AlertObservation> observations,
        int transitionThreshold = 3) =>
        observations
            .GroupBy(item => (item.ServiceSlug, item.RuleName))
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.At).ToArray();
                var transitions = ordered.Zip(ordered.Skip(1), (previous, current) => previous.State != current.State ? 1 : 0).Sum();
                return new FlappingAlert(
                    group.Key.ServiceSlug,
                    group.Key.RuleName,
                    transitions,
                    ordered.First().At,
                    ordered.Last().At);
            })
            .Where(item => item.StateTransitions >= transitionThreshold)
            .OrderByDescending(item => item.StateTransitions)
            .ToArray();
}
