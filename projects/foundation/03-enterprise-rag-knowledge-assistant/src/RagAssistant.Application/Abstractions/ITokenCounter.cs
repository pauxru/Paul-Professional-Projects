namespace RagAssistant.Application.Abstractions;

public interface ITokenCounter
{
    int CountTokens(string text);
}

public sealed class HeuristicTokenCounter : ITokenCounter
{
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var chars = text.Length;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Max(1, (int)Math.Ceiling(chars / 4.0 + words * 0.1));
    }
}
