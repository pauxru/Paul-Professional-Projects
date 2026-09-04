using System.Net;
using System.Net.Http.Json;
using RagAssistant.Api.Contracts;
using RagAssistant.Domain.Documents;
using RagAssistant.IntegrationTests.Fixtures;

namespace RagAssistant.IntegrationTests.Api;

public sealed class ChatEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ChatEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ChatTurn_CreatesSessionAndAnswers()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "alice", ["employee"], ["hr"], Classification.Internal);

        var first = await client.PostAsJsonAsync("/api/v1/chat/sessions", new ChatRequestDto(
            SessionId: null,
            Query: "How many paid time off days do employees receive?"));
        if (!first.IsSuccessStatusCode)
        {
            var body = await first.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"First chat request failed: {(int)first.StatusCode} {first.StatusCode}: {body}");
        }
        var payload = await first.Content.ReadFromJsonAsync<ChatResponse>(TestAuth.Json);
        Assert.NotNull(payload);
        Assert.NotEqual(Guid.Empty, payload!.SessionId);

        var followUp = await client.PostAsJsonAsync("/api/v1/chat/sessions", new ChatRequestDto(
            SessionId: payload.SessionId,
            Query: "What about the carry-over cap?"));
        if (!followUp.IsSuccessStatusCode)
        {
            var body = await followUp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Follow-up chat request failed: {(int)followUp.StatusCode}: {body}");
        }
        var second = await followUp.Content.ReadFromJsonAsync<ChatResponse>(TestAuth.Json);
        Assert.NotNull(second);
        Assert.Equal(payload.SessionId, second!.SessionId);
    }

    [Fact]
    public async Task Feedback_RecordsThumbsDown_Returns201()
    {
        var client = await _factory.AuthenticatedClientAsync(
            "alice", ["employee"], [], Classification.Internal);

        var response = await client.PostAsJsonAsync("/api/v1/feedback", new FeedbackRequestDto(
            Query: "What is PTO?",
            Answer: "Employees receive 20 days.",
            Rating: -1,
            Reason: "Missed the accrual detail",
            PromptVersion: "rag.answer@v1",
            CitedChunkIds: new[] { Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
