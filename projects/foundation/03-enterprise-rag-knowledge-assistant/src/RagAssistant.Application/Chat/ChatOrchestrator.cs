using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Answering;
using RagAssistant.Application.Cost;
using RagAssistant.Application.Retrieval;
using RagAssistant.Domain.Chat;
using RagAssistant.Domain.Common;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Application.Chat;

public sealed record ChatTurnRequest(
    Guid? SessionId,
    string Query,
    UserPrincipal User,
    RetrievalMode Mode = RetrievalMode.Hybrid,
    int TopK = 4,
    string Tenant = "default");

public sealed record ChatTurnResult(
    Guid SessionId,
    string RewrittenQuery,
    AnswerResult Answer);

public sealed class ChatOrchestrator
{
    private readonly IChatSessionRepository _sessions;
    private readonly IQueryRewriter _rewriter;
    private readonly AnsweringService _answering;
    private readonly IBudgetGuard _budget;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public ChatOrchestrator(
        IChatSessionRepository sessions,
        IQueryRewriter rewriter,
        AnsweringService answering,
        IBudgetGuard budget,
        IClock clock,
        IIdGenerator ids)
    {
        _sessions = sessions;
        _rewriter = rewriter;
        _answering = answering;
        _budget = budget;
        _clock = clock;
        _ids = ids;
    }

    public async Task<ChatTurnResult> HandleAsync(ChatTurnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("Query must be provided.", nameof(request));
        }

        await _budget.EnsureAllowedAsync(request.Tenant, _clock.UtcNow, ct).ConfigureAwait(false);

        var isNewSession = request.SessionId is null;
        ChatSession session;
        IReadOnlyList<ChatMessage> history;

        if (!isNewSession)
        {
            session = await _sessions.GetByIdAsync(request.SessionId!.Value, request.User.UserId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session {request.SessionId} not found.");
            history = await _sessions.GetMessagesAsync(session.Id, ct).ConfigureAwait(false);
        }
        else
        {
            session = new ChatSession(_ids.NewId(), request.User.UserId, TitleFrom(request.Query), _clock.UtcNow);
            history = Array.Empty<ChatMessage>();
        }

        var rewritten = _rewriter.Rewrite(request.Query, history);

        var answer = await _answering.AnswerAsync(
            new AnswerRequest(rewritten, request.User, request.Mode, request.TopK, request.Tenant),
            ct).ConfigureAwait(false);

        var timestamp = _clock.UtcNow;
        var userMessage = new ChatMessage(_ids.NewId(), session.Id, ChatMessageRole.User, request.Query, timestamp, null);
        var assistantMessage = new ChatMessage(_ids.NewId(), session.Id, ChatMessageRole.Assistant, answer.Answer, timestamp, answer.PromptVersion);

        if (isNewSession)
        {
            session.AppendMessage(userMessage.Id, ChatMessageRole.User, request.Query, timestamp);
            session.AppendMessage(assistantMessage.Id, ChatMessageRole.Assistant, answer.Answer, timestamp, answer.PromptVersion);
            await _sessions.AddAsync(session, ct).ConfigureAwait(false);
        }
        else
        {
            await _sessions.AppendMessagesAsync(session.Id, new[] { userMessage, assistantMessage }, timestamp, ct).ConfigureAwait(false);
        }

        return new ChatTurnResult(session.Id, rewritten, answer);
    }

    private static string TitleFrom(string query)
    {
        var trimmed = query.Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
    }
}
