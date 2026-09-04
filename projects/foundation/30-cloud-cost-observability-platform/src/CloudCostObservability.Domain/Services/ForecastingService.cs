using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Domain.Services;

public sealed record DailyCostPoint(DateOnly Date, decimal Amount);

public sealed class ForecastingService
{
    public ForecastResult Forecast(
        DateOnly monthStart,
        IEnumerable<DailyCostPoint> history,
        IEnumerable<DailyCostPoint> observedToDate,
        ForecastMethod method,
        decimal measuredMape = 0m)
    {
        var observed = observedToDate.Where(x => x.Date.Year == monthStart.Year && x.Date.Month == monthStart.Month).OrderBy(x => x.Date).ToList();
        if (observed.Count == 0) throw new ArgumentException("At least one current-month observation is required.", nameof(observedToDate));
        var allHistory = history.Where(x => x.Date < monthStart).OrderBy(x => x.Date).ToList();
        var days = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var projected = method switch
        {
            ForecastMethod.RunRate => RunRate(observed, days),
            ForecastMethod.SeasonalAware => SeasonalAware(allHistory, observed, monthStart, days),
            ForecastMethod.LinearRegression => LinearRegression(allHistory, observed, monthStart, days),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
        var errorRatio = ConfidenceRatio(allHistory, method);
        return new ForecastResult(method, Round(projected), Round(projected * (1m - errorRatio)), Round(projected * (1m + errorRatio)), measuredMape, false);
    }

    public IReadOnlyList<ForecastBacktest> Backtest(IEnumerable<DailyCostPoint> series, int observedDays = 14, int minimumHistoryMonths = 2)
    {
        if (observedDays < 1) throw new ArgumentOutOfRangeException(nameof(observedDays));
        var all = series.OrderBy(x => x.Date).ToList();
        var months = all.GroupBy(x => new DateOnly(x.Date.Year, x.Date.Month, 1))
            .Where(group => group.Count() == DateTime.DaysInMonth(group.Key.Year, group.Key.Month))
            .OrderBy(group => group.Key)
            .ToList();
        var results = new List<ForecastBacktest>();
        foreach (var method in Enum.GetValues<ForecastMethod>())
        {
            var errors = new List<decimal>();
            for (var index = minimumHistoryMonths; index < months.Count; index++)
            {
                var month = months[index];
                var observed = month.OrderBy(x => x.Date).Take(observedDays).ToList();
                if (observed.Count == 0) continue;
                var actual = month.Sum(x => x.Amount);
                if (actual == 0m) continue;
                var history = months.Take(index).SelectMany(x => x);
                var prediction = Forecast(month.Key, history, observed, method).ProjectedMonthEnd;
                errors.Add(Math.Abs(prediction - actual) / actual * 100m);
            }
            results.Add(new ForecastBacktest(method, errors.Count == 0 ? 0m : Round(errors.Average()), errors.Count));
        }
        return results;
    }

    public IReadOnlyList<ForecastResult> ForecastAll(DateOnly monthStart, IEnumerable<DailyCostPoint> history, IEnumerable<DailyCostPoint> observedToDate)
    {
        var historical = history.ToList();
        var observed = observedToDate.ToList();
        var accuracy = Backtest(historical.Concat(observed));
        var defaultMethod = accuracy.OrderBy(x => x.MapePercent).ThenBy(x => x.Method).First().Method;
        return Enum.GetValues<ForecastMethod>()
            .Select(method =>
            {
                var mape = accuracy.Single(x => x.Method == method).MapePercent;
                var result = Forecast(monthStart, historical, observed, method, mape);
                return result with { IsDefault = method == defaultMethod };
            })
            .ToList();
    }

    private static decimal RunRate(IReadOnlyList<DailyCostPoint> observed, int days) =>
        observed.Sum(x => x.Amount) / observed.Count * days;

    private static decimal SeasonalAware(IReadOnlyList<DailyCostPoint> history, IReadOnlyList<DailyCostPoint> observed, DateOnly monthStart, int days)
    {
        if (history.Count < 14) return RunRate(observed, days);
        var weekdayMeans = history.GroupBy(x => (int)x.Date.DayOfWeek).ToDictionary(x => x.Key, x => x.Average(y => y.Amount));
        var global = history.Average(x => x.Amount);
        if (global == 0m || weekdayMeans.Values.Any(x => x == 0m)) return RunRate(observed, days);

        var inferredBaseline = observed.Average(x => x.Amount / (weekdayMeans.GetValueOrDefault((int)x.Date.DayOfWeek, global) / global));
        var total = 0m;
        for (var day = 1; day <= days; day++)
        {
            var date = new DateOnly(monthStart.Year, monthStart.Month, day);
            var known = observed.FirstOrDefault(x => x.Date == date);
            total += known is null
                ? inferredBaseline * weekdayMeans.GetValueOrDefault((int)date.DayOfWeek, global) / global
                : known.Amount;
        }
        return total;
    }

    private static decimal LinearRegression(IReadOnlyList<DailyCostPoint> history, IReadOnlyList<DailyCostPoint> observed, DateOnly monthStart, int days)
    {
        var training = history.Concat(observed).OrderBy(x => x.Date).ToList();
        if (training.Count < 2) return RunRate(observed, days);
        var n = training.Count;
        var xMean = (n - 1) / 2m;
        var yMean = training.Average(x => x.Amount);
        var numerator = training.Select((point, index) => (index - xMean) * (point.Amount - yMean)).Sum();
        var denominator = training.Select((_, index) => (index - xMean) * (index - xMean)).Sum();
        if (denominator == 0m) return RunRate(observed, days);
        var slope = numerator / denominator;
        var intercept = yMean - slope * xMean;
        var total = 0m;
        for (var day = 1; day <= days; day++)
        {
            var date = new DateOnly(monthStart.Year, monthStart.Month, day);
            var known = observed.FirstOrDefault(x => x.Date == date);
            if (known is not null) total += known.Amount;
            else
            {
                var index = training.Count - 1 + (date.DayNumber - observed.Last().Date.DayNumber);
                total += Math.Max(0m, intercept + slope * index);
            }
        }
        return total;
    }

    private static decimal ConfidenceRatio(IReadOnlyList<DailyCostPoint> history, ForecastMethod method)
    {
        if (history.Count < 2) return .20m;
        var mean = history.Average(x => x.Amount);
        if (mean == 0m) return .20m;
        var mad = history.Select(x => Math.Abs(x.Amount - mean)).OrderBy(x => x).ElementAt(history.Count / 2);
        var baseRatio = Math.Min(.35m, Math.Max(.03m, mad / mean * 1.96m));
        return method == ForecastMethod.SeasonalAware ? baseRatio * .75m : method == ForecastMethod.LinearRegression ? baseRatio * .9m : baseRatio;
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
