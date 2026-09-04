namespace Lab.SampleApp.Middleware;

public static class CorrelationIdMiddleware
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
            var correlationId = !string.IsNullOrWhiteSpace(supplied) && supplied.Length <= 128
                ? supplied
                : context.TraceIdentifier;

            context.TraceIdentifier = correlationId;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Correlation-Id"] = correlationId;
                return Task.CompletedTask;
            });

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Correlation");
            using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
            {
                await next(context);
            }
        });
}
