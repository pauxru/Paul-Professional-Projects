using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Idp.Api.Auth;

namespace Idp.IntegrationTests;

/// <summary>End-to-end API tests: auth, upload validation, listing, review flow and the STP metric.</summary>
public sealed class ApiTests : IClassFixture<IdpApiFactory>
{
    private readonly IdpApiFactory _factory;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public ApiTests(IdpApiFactory factory, Xunit.Abstractions.ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private static MultipartFormDataContent FileForm(byte[] bytes, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static byte[] UniqueInvoiceText() => Encoding.UTF8.GetBytes(
        $"TAX INVOICE\nSupplier: Rift Valley Supplies Ltd\nInvoice Number: IT-{Guid.NewGuid():N}\n" +
        "Invoice Date: 2024-05-10\nCurrency: KES\nSubtotal: 100.00\nTax: 16.00\nTotal: 116.00\n");

    [Fact]
    public async Task Health_is_anonymous_and_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Upload_without_token_is_401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/v1/documents",
            FileForm(UniqueInvoiceText(), "inv.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_with_wrong_permission_is_403()
    {
        var client = _factory.AuthedClient(Permissions.ReviewProcess); // lacks documents:submit
        var response = await client.PostAsync("/api/v1/documents",
            FileForm(UniqueInvoiceText(), "inv.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Upload_valid_document_is_201()
    {
        var client = _factory.AuthedClient(Permissions.DocumentsSubmit);
        var response = await client.PostAsync("/api/v1/documents",
            FileForm(UniqueInvoiceText(), "inv.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Upload_too_large_is_400()
    {
        var client = _factory.AuthedClient(Permissions.DocumentsSubmit);
        var big = new byte[6 * 1024 * 1024]; // exceeds the 5 MB ingestion limit
        var response = await client.PostAsync("/api/v1/documents",
            FileForm(big, "big.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_wrong_type_is_400()
    {
        var client = _factory.AuthedClient(Permissions.DocumentsSubmit);
        var response = await client.PostAsync("/api/v1/documents",
            FileForm(new byte[] { 1, 2, 3 }, "malware.exe", "application/x-msdownload"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_returns_seeded_documents_and_paginates()
    {
        var client = _factory.AuthedClient(Permissions.DocumentsSubmit);
        using var doc = JsonDocument.Parse(
            await client.GetStringAsync("/api/v1/documents?page=1&pageSize=5"));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("totalCount").GetInt32() >= 15);
        Assert.True(root.GetProperty("items").GetArrayLength() <= 5);
    }

    [Fact]
    public async Task Get_by_id_returns_document_and_404_for_unknown()
    {
        var client = _factory.AuthedClient(Permissions.DocumentsSubmit);
        using var list = JsonDocument.Parse(
            await client.GetStringAsync("/api/v1/documents?pageSize=1"));
        var id = list.RootElement.GetProperty("items")[0].GetProperty("id").GetString();

        var ok = await client.GetAsync($"/api/v1/documents/{id}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var missing = await client.GetAsync($"/api/v1/documents/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Document_fields_are_returned_with_evidence()
    {
        var client = _factory.AuthedClient(Permissions.DocumentsSubmit);
        using var list = JsonDocument.Parse(
            await client.GetStringAsync("/api/v1/documents?pageSize=50"));
        var id = list.RootElement.GetProperty("items")[0].GetProperty("id").GetString();

        using var fields = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/documents/{id}/fields"));
        Assert.True(fields.RootElement.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Review_queue_lists_and_a_task_can_be_claimed()
    {
        var client = _factory.AuthedClient(Permissions.ReviewProcess);
        using var queue = JsonDocument.Parse(await client.GetStringAsync("/api/v1/review/queue"));
        Assert.True(queue.RootElement.GetArrayLength() >= 1);

        var taskId = queue.RootElement[0].GetProperty("taskId").GetString();
        var claim = await client.PostAsync($"/api/v1/review/{taskId}/claim", null);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
    }

    [Fact]
    public async Task Exports_can_be_listed()
    {
        var client = _factory.AuthedClient(Permissions.ExportManage);
        var response = await client.GetAsync("/api/v1/exports");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Stp_metric_is_measured_over_the_seeded_corpus()
    {
        var client = _factory.AuthedClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/metrics/stp"));
        var root = doc.RootElement;

        var total = root.GetProperty("totalDocuments").GetInt32();
        var processed = root.GetProperty("processed").GetInt32();
        var autoApproved = root.GetProperty("autoApproved").GetInt32();
        var rate = root.GetProperty("straightThroughRate").GetDouble();

        _output.WriteLine($"MEASURE_STP total={total} processed={processed} " +
            $"autoApproved={autoApproved} inReview={root.GetProperty("inReview").GetInt32()} " +
            $"rejected={root.GetProperty("rejected").GetInt32()} " +
            $"queueDepth={root.GetProperty("reviewQueueDepth").GetInt32()} rate={rate:P2}");

        Assert.True(total >= 15, $"total {total}");
        Assert.Equal(total, processed); // every seeded document reaches a routing decision
        Assert.True(autoApproved >= 1);
        Assert.InRange(rate, 0.0, 1.0);
        Assert.Equal((double)autoApproved / processed, rate, 6);
    }
}
