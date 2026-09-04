using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Application;
using SubscriptionBilling.Infrastructure;

namespace SubscriptionBilling.Api;

public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        MapAuth(app);
        MapSubscriptions(app);
        MapUsage(app);
        MapInvoices(app);
        MapWebhooks(app);
        MapReports(app);
        return app;
    }

    private static void MapAuth(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/token", (
            TokenRequest request,
            IWebHostEnvironment environment,
            ITokenIssuer issuer) =>
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                return Results.NotFound();
            }

            CatalogueEndpoints.ValidateRequired(("subject", request.Subject));
            var allowed = new HashSet<string>(
                ["billing.read", "billing.write", "billing.admin"],
                StringComparer.Ordinal);
            var scopes = request.Scopes
                .Where(allowed.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (scopes.Length == 0)
            {
                throw new RequestValidationException(
                    "At least one supported scope is required.",
                    new Dictionary<string, string[]>
                    {
                        ["scopes"] = ["Use billing.read, billing.write, or billing.admin."]
                    });
            }

            return Results.Ok(new
            {
                accessToken = issuer.Issue(request.Subject, scopes, TimeSpan.FromHours(1)),
                tokenType = "Bearer",
                expiresInSeconds = 3600,
                scopes
            });
        }).WithTags("Authentication").AllowAnonymous();
    }

    private static void MapSubscriptions(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/subscriptions").WithTags("Subscriptions");
        group.MapGet("/", (
            int? page,
            int? pageSize,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ListSubscriptionsAsync(
                page is null or <= 0 ? 1 : page.Value,
                pageSize is null or <= 0 ? 20 : pageSize.Value,
                cancellationToken))
            .RequireAuthorization("billing.read");
        group.MapPost("/", async (
            CreateSubscriptionCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            var created = await engine.CreateSubscriptionAsync(command, cancellationToken);
            return Results.Created($"/api/v1/subscriptions/{created.Id}", created);
        }).RequireAuthorization("billing.write");
        group.MapPost("/{id:guid}/change", (
            Guid id,
            ChangeSubscriptionCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ChangeSubscriptionAsync(id, command, cancellationToken))
            .RequireAuthorization("billing.write");
        group.MapPost("/{id:guid}/pause", (
            Guid id,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.PauseSubscriptionAsync(id, cancellationToken))
            .RequireAuthorization("billing.write");
        group.MapPost("/{id:guid}/resume", (
            Guid id,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ResumeSubscriptionAsync(id, cancellationToken))
            .RequireAuthorization("billing.write");
        group.MapPost("/{id:guid}/cancel", (
            Guid id,
            CancelSubscriptionCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.CancelSubscriptionAsync(id, command, cancellationToken))
            .RequireAuthorization("billing.write");
        group.MapPost("/{id:guid}/reactivate", (
            Guid id,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ReactivateSubscriptionAsync(id, cancellationToken))
            .RequireAuthorization("billing.write");
        group.MapPost("/{id:guid}/charges", (
            Guid id,
            AddOneOffChargeCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            CatalogueEndpoints.ValidateRequired(
                ("description", command.Description),
                ("currency", command.Currency));
            return engine.AddOneOffChargeAsync(id, command, cancellationToken);
        }).RequireAuthorization("billing.write");
    }

    private static void MapUsage(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/usage", async (
            RecordUsageCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            CatalogueEndpoints.ValidateRequired(("eventId", command.EventId));
            var receipt = await engine.RecordUsageAsync(command, cancellationToken);
            return receipt.Duplicate
                ? Results.Ok(receipt)
                : Results.Accepted($"/api/v1/usage/{receipt.EventId}", receipt);
        }).WithTags("Usage").RequireAuthorization("billing.write");
    }

    private static void MapInvoices(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/invoices").WithTags("Invoices");
        group.MapGet("/", (
            int? page,
            int? pageSize,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.ListInvoicesAsync(
                page is null or <= 0 ? 1 : page.Value,
                pageSize is null or <= 0 ? 20 : pageSize.Value,
                cancellationToken))
            .RequireAuthorization("billing.read");
        group.MapGet("/{id:guid}", async (
            Guid id,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            var invoice = await engine.GetInvoiceAsync(id, cancellationToken);
            return invoice is null ? Results.NotFound() : Results.Ok(invoice);
        }).RequireAuthorization("billing.read");
        group.MapPost("/preview", (
            PreviewInvoiceCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.PreviewInvoiceAsync(command, cancellationToken))
            .RequireAuthorization("billing.read");
        group.MapPost("/run", (
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.RunInvoicesAsync(cancellationToken))
            .RequireAuthorization("billing.admin");
        group.MapPost("/dunning/run", (
            DunningProcessor processor,
            CancellationToken cancellationToken) =>
            processor.RunDueAsync(cancellationToken))
            .RequireAuthorization("billing.admin");
        group.MapPost("/{id:guid}/void", (
            Guid id,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.VoidInvoiceAsync(id, cancellationToken))
            .RequireAuthorization("billing.admin");
        group.MapPost("/{id:guid}/pay", (
            Guid id,
            PayInvoiceRequest request,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            CatalogueEndpoints.ValidateRequired(("paymentMethodToken", request.PaymentMethodToken));
            return engine.PayInvoiceAsync(id, request.PaymentMethodToken, cancellationToken);
        }).RequireAuthorization("billing.write");
        group.MapPost("/{id:guid}/credit-notes", (
            Guid id,
            CreateCreditNoteCommand command,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            CatalogueEndpoints.ValidateRequired(("reason", command.Reason));
            return engine.CreateCreditNoteAsync(id, command, cancellationToken);
        }).RequireAuthorization("billing.admin");
        group.MapGet("/{id:guid}/dunning", async (
            Guid id,
            BillingDbContext db,
            CancellationToken cancellationToken) =>
        {
            var dunning = await db.DunningCases.AsNoTracking().SingleOrDefaultAsync(
                item => item.InvoiceId == id,
                cancellationToken);
            return dunning is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    dunning.Id,
                    dunning.InvoiceId,
                    dunning.SubscriptionId,
                    dunning.InitialFailureAt,
                    dunning.AttemptsCompleted,
                    dunning.LastAttemptAt,
                    dunning.Recovered,
                    dunning.Escalated
                });
        }).RequireAuthorization("billing.admin");
        group.MapGet("/{id:guid}/render", async (
            Guid id,
            string? format,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
        {
            var invoice = await engine.GetInvoiceAsync(id, cancellationToken);
            if (invoice is null)
            {
                return Results.NotFound();
            }

            if (!string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(invoice);
            }

            var encoder = HtmlEncoder.Default;
            var rows = string.Join(
                string.Empty,
                invoice.Lines.Select(line =>
                    $"<tr><td>{encoder.Encode(line.Description)}</td><td>{line.AmountMinor}</td><td>{encoder.Encode(line.Currency)}</td></tr>"));
            var html = $"""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8"><title>{encoder.Encode(invoice.Number ?? "Draft invoice")}</title></head>
                <body><h1>{encoder.Encode(invoice.Number ?? "Draft invoice")}</h1>
                <p>Status: {encoder.Encode(invoice.Status.ToString())}</p>
                <table><thead><tr><th>Description</th><th>Minor units</th><th>Currency</th></tr></thead>
                <tbody>{rows}</tbody></table>
                <p>Total: {invoice.TotalMinor} {encoder.Encode(invoice.Currency)}</p></body></html>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        }).RequireAuthorization("billing.read");
    }

    private static void MapWebhooks(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/webhooks/payments", async (
            HttpRequest request,
            WebhookSignatureService signatures,
            IPaymentWebhookProcessor processor,
            CancellationToken cancellationToken) =>
        {
            request.EnableBuffering();
            request.Body.Position = 0;
            using var reader = new StreamReader(
                request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(cancellationToken);
            request.Body.Position = 0;
            var signature = request.Headers["X-Billing-Signature"].FirstOrDefault() ?? string.Empty;
            var nonce = request.Headers["X-Webhook-Nonce"].FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nonce) ||
                !await signatures.VerifyAsync(rawBody, signature, nonce, cancellationToken))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Invalid webhook signature",
                    detail: "Signature, timestamp, or replay validation failed.");
            }

            var payload = JsonSerializer.Deserialize<PaymentWebhookRequest>(
                rawBody,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    PropertyNameCaseInsensitive = true
                });
            if (payload is null || payload.InvoiceId == Guid.Empty)
            {
                throw new RequestValidationException(
                    "Webhook body is invalid.",
                    new Dictionary<string, string[]>
                    {
                        ["body"] = ["eventType and invoiceId are required."]
                    });
            }

            await processor.ProcessAsync(
                payload.EventType,
                payload.InvoiceId,
                cancellationToken);
            return Results.Accepted();
        }).WithTags("Webhooks").AllowAnonymous();

        var outbound = app.MapGroup("/api/v1/webhooks/outbound")
            .WithTags("Webhooks")
            .RequireAuthorization("billing.admin");
        outbound.MapGet("/dead-letter", async (
            BillingDbContext db,
            CancellationToken cancellationToken) =>
            await db.OutboundWebhooks.AsNoTracking()
                .Where(item => item.Status == OutboundWebhookStatus.DeadLetter)
                .OrderBy(item => item.CreatedAt)
                .Select(item => new
                {
                    item.Id,
                    item.Endpoint,
                    item.EventType,
                    item.Attempts,
                    item.LastError,
                    item.CreatedAt
                })
                .ToListAsync(cancellationToken));
        outbound.MapPost("/{id:guid}/replay", async (
            Guid id,
            BillingDbContext db,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var delivery = await db.OutboundWebhooks.SingleOrDefaultAsync(
                item => item.Id == id,
                cancellationToken);
            if (delivery is null)
            {
                return Results.NotFound();
            }

            delivery.Status = OutboundWebhookStatus.Pending;
            delivery.Attempts = 0;
            delivery.NextAttemptAt = clock.UtcNow;
            delivery.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Accepted();
        });
    }

    private static void MapReports(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/reports/mrr", (
            string currency,
            IBillingEngine engine,
            CancellationToken cancellationToken) =>
            engine.GetMrrAsync(
                string.IsNullOrWhiteSpace(currency) ? "USD" : currency,
                cancellationToken))
            .WithTags("Reports")
            .RequireAuthorization("billing.read");
    }

    public sealed record TokenRequest(string Subject, string[] Scopes);
    public sealed record PayInvoiceRequest(string PaymentMethodToken);
    public sealed record PaymentWebhookRequest(string EventType, Guid InvoiceId);
}
