using JobScheduler.Api.Auth;
using JobScheduler.Api.Contracts;
using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JobScheduler.Api.Endpoints;

public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/schedule").WithTags("Schedule");
        group.MapGet("/upcoming", UpcomingAsync).RequireAuthorization(AuthConstants.PolicyRead);
    }

    private static async Task<Ok<IReadOnlyList<UpcomingOccurrenceDto>>> UpcomingAsync(
        IJobDefinitionStore store, IClock clock, int? count, CancellationToken ct)
    {
        int take = Math.Clamp(count ?? 20, 1, 200);
        var now = clock.UtcNow;
        var scheduled = await store.ListEnabledScheduledAsync(ct);
        var upcoming = new List<UpcomingOccurrenceDto>();

        foreach (var def in scheduled)
        {
            if (def.TriggerType == TriggerType.Manual)
            {
                continue;
            }

            try
            {
                var schedule = def.BuildSchedule();
                var next = schedule.NextFireAfter(now);
                if (next is null)
                {
                    continue;
                }

                var tz = def.ResolveTimeZone();
                var local = TimeZoneInfo.ConvertTime(next.Value, tz);
                upcoming.Add(new UpcomingOccurrenceDto(def.Id, def.Name, def.TriggerType, def.TimeZoneId, next.Value.ToUniversalTime(), local));
            }
            catch (Exception)
            {
                // A malformed schedule should not break the whole listing.
            }
        }

        var ordered = upcoming.OrderBy(u => u.NextFireUtc).Take(take).ToList();
        return TypedResults.Ok<IReadOnlyList<UpcomingOccurrenceDto>>(ordered);
    }
}
