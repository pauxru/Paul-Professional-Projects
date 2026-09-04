using System.Net;
using System.Net.Http.Json;
using Healthcare.Domain.Common;
using Xunit;

namespace Healthcare.IntegrationTests;

public class ApiSurfaceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ApiSurfaceTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Facilities_Endpoint_Requires_Authentication()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/facilities");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Facilities_Endpoint_Returns_Ok_For_Receptionist()
    {
        await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("recept-1", new[] { Roles.Receptionist });
        var resp = await client.GetAsync("/api/v1/facilities");
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Dev_Token_Endpoint_Rejects_Unknown_Role()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/dev-token",
            new { userId = "someone", roles = new[] { "SuperUser" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Health_Live_Endpoint_Is_Available_Without_Auth()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/live");
        resp.EnsureSuccessStatusCode();
    }
}
