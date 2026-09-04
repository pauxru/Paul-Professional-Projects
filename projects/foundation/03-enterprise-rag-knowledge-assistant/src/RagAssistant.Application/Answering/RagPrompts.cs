using System.Text;
using System.Text.Json;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.Application.Answering;

public static class RagPrompts
{
    public const string DefaultPromptName = "rag.answer";
    public const string DefaultPromptVersion = "v1";
    public const string RefusalMessage = "I don't have enough information in the indexed documents to answer that.";

    public const string DefaultPromptBody = """
        You are Acme Manufacturing's knowledge assistant.

        - Answer strictly from the numbered CONTEXT chunks provided.
        - Every sentence must end with a citation marker like [1], [2].
        - If the context does not contain the answer, respond with the exact refusal message.
        - Do not invent policies, product names, or numbers.

        CONTEXT:
        {context}

        QUESTION:
        {question}
        """;

    private const string ContextMarkerStart = "<<CTX_JSON>>";
    private const string ContextMarkerEnd = "<<END_CTX_JSON>>";

    public static string EncodeContext(IReadOnlyList<RetrievedChunk> chunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Acme Manufacturing's knowledge assistant. Answer only from the numbered context.");
        for (var i = 0; i < chunks.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(chunks[i].Content.Trim());
        }

        sb.AppendLine();
        sb.AppendLine(ContextMarkerStart);
        sb.AppendLine(JsonSerializer.Serialize(chunks));
        sb.AppendLine(ContextMarkerEnd);
        return sb.ToString();
    }

    public static bool TryDeserializeContext(string systemContent, out IReadOnlyList<RetrievedChunk> chunks)
    {
        chunks = Array.Empty<RetrievedChunk>();
        if (string.IsNullOrEmpty(systemContent))
        {
            return false;
        }

        var startIndex = systemContent.IndexOf(ContextMarkerStart, StringComparison.Ordinal);
        var endIndex = systemContent.IndexOf(ContextMarkerEnd, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0 || endIndex <= startIndex)
        {
            return false;
        }

        var jsonStart = startIndex + ContextMarkerStart.Length;
        var payload = systemContent[jsonStart..endIndex].Trim();
        try
        {
            var parsed = JsonSerializer.Deserialize<RetrievedChunk[]>(payload);
            if (parsed is null)
            {
                return false;
            }

            chunks = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
