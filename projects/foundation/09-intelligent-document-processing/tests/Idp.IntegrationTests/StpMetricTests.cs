using System.Net.Http.Headers;
using System.Text.Json;

namespace Idp.IntegrationTests;

/// <summary>
/// Straight-through-processing measured over a pristine seeded corpus. This class has its own factory
/// instance (its own throwaway database) so no upload from another test can perturb the counts, making
/// the STP figure deterministic and reproducible.
/// </summary>
public sealed class StpMetricTests : IClassFixture<IdpApiFactory>
{
    private readonly IdpApiFactory _factory;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public StpMetricTests(IdpApiFactory factory, Xunit.Abstractions.ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Stp_is_deterministic_over_the_seeded_corpus()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.MintToken());

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/metrics/stp"));
        var root = doc.RootElement;

        var total = root.GetProperty("totalDocuments").GetInt32();
        var processed = root.GetProperty("processed").GetInt32();
        var autoApproved = root.GetProperty("autoApproved").GetInt32();
        var inReview = root.GetProperty("inReview").GetInt32();
        var rejected = root.GetProperty("rejected").GetInt32();
        var queueDepth = root.GetProperty("reviewQueueDepth").GetInt32();
        var rate = root.GetProperty("straightThroughRate").GetDouble();

        _output.WriteLine($"MEASURE_STP_CLEAN total={total} processed={processed} " +
            $"autoApproved={autoApproved} inReview={inReview} rejected={rejected} " +
            $"queueDepth={queueDepth} rate={rate:P2}");

        Assert.Equal(19, total);
        Assert.Equal(19, processed);
        Assert.Equal(6, autoApproved);
        Assert.Equal(6.0 / 19.0, rate, 4);
    }
}
