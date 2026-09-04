using System.Globalization;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public sealed class QueryStringParser(QueryLimits limits)
{
    private enum TokenKind { Word, Phrase, LeftParen, RightParen, Colon, LeftBracket, RightBracket, Minus, Plus, And, Or, Not, To, End }
    private sealed record Token(TokenKind Kind, string Value, int Offset);
    private List<Token> _tokens = [];
    private int _position;

    public SearchClause Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new MatchAllClause();
        if (input.Length > 2_048) throw new QueryParseException("Query string exceeds the 2048-character limit.");
        _tokens = Lex(input);
        _position = 0;
        var clause = ParseOr();
        if (Current.Kind != TokenKind.End) throw Error($"Unexpected token '{Current.Value}'.");
        if (CountClauses(clause) > limits.MaxClauses) throw new QueryValidationException($"Query has more than {limits.MaxClauses} clauses.");
        return clause;
    }

    private SearchClause ParseOr()
    {
        var clauses = new List<SearchClause> { ParseAnd() };
        while (Match(TokenKind.Or))
        {
            if (!CanStartClause(Current.Kind)) throw Error("OR must be followed by a clause.");
            clauses.Add(ParseAnd());
        }
        return clauses.Count == 1 ? clauses[0] : new BooleanClause(Should: clauses, MinimumShouldMatch: 1);
    }

    private SearchClause ParseAnd()
    {
        var clauses = new List<SearchClause> { ParseUnary() };
        while (true)
        {
            if (Match(TokenKind.And))
            {
                if (!CanStartClause(Current.Kind)) throw Error("AND must be followed by a clause.");
                clauses.Add(ParseUnary());
                continue;
            }
            if (Current.Kind is TokenKind.Or or TokenKind.RightParen or TokenKind.End) break;
            if (CanStartClause(Current.Kind))
            {
                clauses.Add(ParseUnary());
                continue;
            }
            throw Error($"Unexpected token '{Current.Value}'.");
        }
        return clauses.Count == 1 ? clauses[0] : new BooleanClause(Must: clauses);
    }

    private SearchClause ParseUnary()
    {
        if (Match(TokenKind.Minus) || Match(TokenKind.Not))
        {
            if (!CanStartClause(Current.Kind)) throw Error("NOT must be followed by a clause.");
            return new BooleanClause(Must: [new MatchAllClause()], MustNot: [ParseUnary()]);
        }
        Match(TokenKind.Plus);
        return ParsePrimary();
    }

    private SearchClause ParsePrimary()
    {
        if (Match(TokenKind.LeftParen))
        {
            var nested = ParseOr();
            Expect(TokenKind.RightParen, "Expected ')' to close group.");
            return nested;
        }
        if (Current.Kind is TokenKind.Word && Peek().Kind == TokenKind.Colon)
        {
            var field = Take().Value;
            Take();
            return ParseFieldValue(field);
        }
        if (Current.Kind == TokenKind.Phrase)
        {
            return ParsePhrase(null);
        }
        if (Current.Kind == TokenKind.Word)
        {
            return ParseTerm(null, Take().Value);
        }
        throw Error("Expected a query clause.");
    }

    private SearchClause ParseFieldValue(string field)
    {
        if (string.IsNullOrWhiteSpace(field)) throw Error("Field name is required before ':'.");
        if (Match(TokenKind.LeftParen))
        {
            var nested = ParseOr();
            Expect(TokenKind.RightParen, "Expected ')' to close field group.");
            return ApplyField(nested, field);
        }
        if (Match(TokenKind.LeftBracket))
        {
            var lower = ParseRangeBoundary();
            Expect(TokenKind.To, "Range query requires TO.");
            var upper = ParseRangeBoundary();
            Expect(TokenKind.RightBracket, "Range query requires closing ']'.");
            return new RangeClause(field, lower, upper);
        }
        if (Current.Kind == TokenKind.Phrase) return ParsePhrase(field);
        if (Current.Kind == TokenKind.Word) return ParseTerm(field, Take().Value);
        throw Error($"Expected a value after '{field}:'.");
    }

    private decimal? ParseRangeBoundary()
    {
        if (Current.Kind != TokenKind.Word) throw Error("Range boundary must be a number or '*'.");
        var raw = Take().Value;
        if (raw == "*") return null;
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) throw Error($"Range boundary '{raw}' is not a decimal number.");
        return parsed;
    }

    private SearchClause ParseTerm(string? field, string raw)
    {
        if (raw.Length > 128) throw Error("Term exceeds the 128-character limit.");
        var fuzzyIndex = raw.LastIndexOf('~');
        if (fuzzyIndex > 0)
        {
            var distanceText = raw[(fuzzyIndex + 1)..];
            var edits = distanceText.Length == 0 ? 2 : int.TryParse(distanceText, out var parsed) ? parsed : throw Error("Fuzzy distance must be an integer.");
            if (edits is < 0 or > 2) throw new QueryValidationException("Fuzzy edit distance must be between 0 and 2.");
            return new FuzzyClause(field, raw[..fuzzyIndex], edits);
        }
        if (raw.Contains('*') || raw.Contains('?'))
        {
            if (raw.Count(character => character is '*' or '?') > 8) throw new QueryValidationException("Wildcard query has too many wildcard operators.");
            return raw.EndsWith('*') && raw.Count(character => character is '*' or '?') == 1
                ? new PrefixClause(field, raw[..^1])
                : new WildcardClause(field, raw);
        }
        return new TermClause(field, raw);
    }

    private PhraseClause ParsePhrase(string? field)
    {
        var phrase = Take().Value;
        var slop = 0;
        if (Current.Kind == TokenKind.Word && Current.Value.StartsWith('~'))
        {
            var distance = Current.Value[1..];
            if (!int.TryParse(distance, out slop) || slop is < 0 or > 10)
            {
                throw new QueryValidationException("Phrase slop must be an integer between 0 and 10.");
            }
            Take();
        }
        return new PhraseClause(field, SplitPhrase(phrase), slop);
    }

    private static IReadOnlyList<string> SplitPhrase(string phrase) => phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static SearchClause ApplyField(SearchClause clause, string field) => clause switch
    {
        TermClause term when term.Field is null => term with { Field = field },
        PhraseClause phrase when phrase.Field is null => phrase with { Field = field },
        PrefixClause prefix when prefix.Field is null => prefix with { Field = field },
        WildcardClause wildcard when wildcard.Field is null => wildcard with { Field = field },
        FuzzyClause fuzzy when fuzzy.Field is null => fuzzy with { Field = field },
        BooleanClause boolean => boolean with
        {
            Must = boolean.Must?.Select(item => ApplyField(item, field)).ToArray(),
            Should = boolean.Should?.Select(item => ApplyField(item, field)).ToArray(),
            MustNot = boolean.MustNot?.Select(item => ApplyField(item, field)).ToArray(),
            Filter = boolean.Filter?.Select(item => ApplyField(item, field)).ToArray()
        },
        _ => clause
    };

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind) return false;
        _position++;
        return true;
    }

    private Token Take() => _tokens[_position++];
    private Token Current => _tokens[_position];
    private Token Peek() => _tokens[Math.Min(_position + 1, _tokens.Count - 1)];

    private void Expect(TokenKind kind, string message)
    {
        if (!Match(kind)) throw Error(message);
    }

    private QueryParseException Error(string message) => new($"{message} At character {Current.Offset}.");

    private static bool CanStartClause(TokenKind kind) => kind is TokenKind.Word or TokenKind.Phrase or TokenKind.LeftParen or TokenKind.Minus or TokenKind.Plus or TokenKind.Not;

    private static int CountClauses(SearchClause clause) => clause switch
    {
        BooleanClause boolean => 1 + (boolean.Must?.Sum(CountClauses) ?? 0) + (boolean.Should?.Sum(CountClauses) ?? 0) + (boolean.MustNot?.Sum(CountClauses) ?? 0) + (boolean.Filter?.Sum(CountClauses) ?? 0),
        _ => 1
    };

    private static List<Token> Lex(string input)
    {
        var tokens = new List<Token>();
        for (var index = 0; index < input.Length;)
        {
            if (char.IsWhiteSpace(input[index])) { index++; continue; }
            var offset = index;
            switch (input[index])
            {
                case '(' : tokens.Add(new Token(TokenKind.LeftParen, "(", index++)); continue;
                case ')' : tokens.Add(new Token(TokenKind.RightParen, ")", index++)); continue;
                case ':' : tokens.Add(new Token(TokenKind.Colon, ":", index++)); continue;
                case '[' : tokens.Add(new Token(TokenKind.LeftBracket, "[", index++)); continue;
                case ']' : tokens.Add(new Token(TokenKind.RightBracket, "]", index++)); continue;
                case '-' : tokens.Add(new Token(TokenKind.Minus, "-", index++)); continue;
                case '+' : tokens.Add(new Token(TokenKind.Plus, "+", index++)); continue;
                case '"':
                    index++;
                    var start = index;
                    while (index < input.Length && input[index] != '"') index++;
                    if (index == input.Length) throw new QueryParseException($"Unterminated quoted phrase at character {offset}.");
                    var phrase = input[start..index++];
                    if (phrase.Length > 512) throw new QueryParseException("Quoted phrase exceeds the 512-character limit.");
                    tokens.Add(new Token(TokenKind.Phrase, phrase, offset));
                    continue;
            }
            var wordStart = index;
            while (index < input.Length && !char.IsWhiteSpace(input[index]) && !"():[]+-\"".Contains(input[index])) index++;
            if (wordStart == index) throw new QueryParseException($"Unsupported character '{input[index]}' at character {index}.");
            var word = input[wordStart..index];
            var kind = word.ToUpperInvariant() switch
            {
                "AND" => TokenKind.And,
                "OR" => TokenKind.Or,
                "NOT" => TokenKind.Not,
                "TO" => TokenKind.To,
                _ => TokenKind.Word
            };
            tokens.Add(new Token(kind, word, offset));
        }
        tokens.Add(new Token(TokenKind.End, string.Empty, input.Length));
        return tokens;
    }
}
