using Idp.Application.Configuration;
using Idp.Application.Documents;
using Idp.Application.Exporting;
using Idp.Application.Extraction;
using Idp.Domain.Documents;
using Idp.Domain.Exports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Idp.UnitTests;

/// <summary>ERP export: bounded retries then dead-letter, retry-then-success, and CSV-injection guard.</summary>
public class ExportServiceTests
{
    private static Document Invoice() => new DocBuilder(DocumentType.Invoice)
        .Field(FieldKeys.SupplierName, "Rift Valley Supplies Ltd")
        .Field(FieldKeys.InvoiceNumber, "INV-1").Field(FieldKeys.Total, "116").Currency("KES")
        .BuildRouted(RoutingDecision.AutoApprove, 0.9);

    private static ExportService Build(
        IErpExportClient erp, IExportOutbox outbox, IDocumentRepository? docs = null)
    {
        var exports = Substitute.For<IExportRepository>();
        exports.GetByDocumentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ExportRecord?)null);
        return new ExportService(
            erp, outbox, exports, docs ?? Substitute.For<IDocumentRepository>(),
            Substitute.For<Idp.Application.Abstractions.IAuditRepository>(),
            new FixedClock(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            Options.Create(new ExportOptions { MaxAttempts = 3, Format = "Json" }),
            Substitute.For<ILogger<ExportService>>());
    }

    [Fact]
    public async Task Exhausted_retries_dead_letter_the_document()
    {
        var erp = Substitute.For<IErpExportClient>();
        erp.SendAsync(Arg.Any<ErpExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErpExportResult.TransientFailure("erp down"));
        var outbox = Substitute.For<IExportOutbox>();
        outbox.WriteDeadLetterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("deadletter/x.json");

        var result = await Build(erp, outbox).ExportAsync(Invoice());

        Assert.Equal(ExportStatus.DeadLettered, result.Status);
        Assert.Equal(3, result.Attempts);
        await erp.Received(3).SendAsync(Arg.Any<ErpExportRequest>(), Arg.Any<CancellationToken>());
        await outbox.Received(1)
            .WriteDeadLetterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Transient_failures_then_success_export_the_document()
    {
        var erp = Substitute.For<IErpExportClient>();
        erp.SendAsync(Arg.Any<ErpExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                ErpExportResult.TransientFailure("t1"),
                ErpExportResult.TransientFailure("t2"),
                ErpExportResult.Ok("ERP-123"));
        var outbox = Substitute.For<IExportOutbox>();
        outbox.WriteOutboxAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("outbox/x.json");

        var document = Invoice();
        var result = await Build(erp, outbox).ExportAsync(document);

        Assert.Equal(ExportStatus.Succeeded, result.Status);
        Assert.Equal(3, result.Attempts);
        Assert.Equal("ERP-123", result.Reference);
        Assert.Equal(PipelineState.Exported, document.State);
        await outbox.Received(1)
            .WriteOutboxAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Permanent_failure_dead_letters_without_retrying()
    {
        var erp = Substitute.For<IErpExportClient>();
        erp.SendAsync(Arg.Any<ErpExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErpExportResult.PermanentFailure("rejected"));
        var outbox = Substitute.For<IExportOutbox>();
        outbox.WriteDeadLetterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("deadletter/x.json");

        var result = await Build(erp, outbox).ExportAsync(Invoice());

        Assert.Equal(ExportStatus.DeadLettered, result.Status);
        await erp.Received(1).SendAsync(Arg.Any<ErpExportRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("=SUM(A1:A2)")]
    [InlineData("+1234")]
    [InlineData("-cmd")]
    [InlineData("@import")]
    public void Csv_neutralises_formula_injection(string dangerous)
    {
        Assert.StartsWith("'", ErpPayloadBuilder.Csv(dangerous));
    }

    [Fact]
    public void Csv_leaves_safe_values_untouched()
    {
        Assert.Equal("Rift Valley", ErpPayloadBuilder.Csv("Rift Valley"));
    }
}
