using System.Text.RegularExpressions;
using RagAssistant.Domain.Chat;

namespace RagAssistant.Application.Chat;

public interface IQueryRewriter
{
    string Rewrite(string query, IReadOnlyList<ChatMessage> history);
}

public sealed partial class ContextualQueryRewriter : IQueryRewriter
{
    private static readonly HashSet<string> Pronouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "it", "this", "that", "they", "them", "these", "those", "he", "she",
    };

    public string Rewrite(string query, IReadOnlyList<ChatMessage> history)
    {
        if (string.IsNullOrWhiteSpace(query) || history is null || history.Count == 0)
        {
            return query?.Trim() ?? string.Empty;
        }

        var normalized = query.Trim();
        var lower = normalized.ToLowerInvariant();
        var previousUser = history
            .Reverse()
            .FirstOrDefault(m => m.Role == ChatMessageRole.User);

        if (previousUser is null)
        {
            return normalized;
        }

        var previousQuery = previousUser.Content?.Trim() ?? string.Empty;
        if (previousQuery.Length == 0)
        {
            return normalized;
        }

        if (ContainsAnyPronoun(lower))
        {
            var subject = ExtractSubject(previousQuery);
            if (!string.IsNullOrEmpty(subject))
            {
                return $"{normalized} (referring to {subject})";
            }

            return $"{normalized} ({previousQuery})";
        }

        if (lower.StartsWith("and ", StringComparison.Ordinal)
            || lower.StartsWith("also ", StringComparison.Ordinal)
            || lower.StartsWith("what about ", StringComparison.Ordinal))
        {
            return $"{previousQuery}; {normalized}";
        }

        return normalized;
    }

    private static bool ContainsAnyPronoun(string lower)
    {
        var words = WordPattern().Matches(lower);
        foreach (Match m in words)
        {
            if (Pronouns.Contains(m.Value))
            {
                return true;
            }
        }

        return false;
    }

    internal static string ExtractSubject(string previousQuery)
    {
        var lower = previousQuery.ToLowerInvariant();
        var about = lower.IndexOf(" about ", StringComparison.Ordinal);
        if (about >= 0)
        {
            var tail = previousQuery[(about + " about ".Length)..].TrimEnd('?', '.', '!').Trim();
            if (tail.Length > 0)
            {
                return tail;
            }
        }

        var of = lower.IndexOf(" of ", StringComparison.Ordinal);
        if (of >= 0)
        {
            var tail = previousQuery[(of + " of ".Length)..].TrimEnd('?', '.', '!').Trim();
            if (tail.Length > 0)
            {
                return tail;
            }
        }

        var words = previousQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return string.Empty;
        }

        var significant = words
            .Where(w => w.Length > 3)
            .Reverse()
            .Take(2)
            .Reverse()
            .ToArray();

        return significant.Length == 0 ? string.Empty : string.Join(' ', significant).TrimEnd('?', '.', '!');
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex WordPattern();
}
