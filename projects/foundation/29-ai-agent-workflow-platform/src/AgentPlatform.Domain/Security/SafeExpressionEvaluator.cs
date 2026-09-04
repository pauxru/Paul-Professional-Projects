using System.Globalization;

namespace AgentPlatform.Domain.Security;

/// <summary>Raised when an expression is malformed or attempts something unsupported.</summary>
public sealed class ExpressionException(string message) : Exception(message);

/// <summary>
/// A safe arithmetic expression evaluator used by the <c>calculate</c> tool.
///
/// It is a hand-written recursive-descent parser over a tiny grammar: numbers, the binary
/// operators <c>+ - * / %</c> and <c>^</c>, parentheses, unary minus, and a fixed allow-list
/// of functions (<c>abs, min, max, round, floor, ceil, sqrt</c>). There is <b>no</b> access
/// to identifiers, variables, reflection, delegates, the file system or the CLR — a hostile
/// input string cannot escape arithmetic. This is the point: model output is data, not code.
/// </summary>
public sealed class SafeExpressionEvaluator
{
    private const int MaxLength = 512;
    private const int MaxDepth = 64;

    private string _text = string.Empty;
    private int _pos;
    private int _depth;

    public double Evaluate(string expression)
    {
        if (expression is null) throw new ExpressionException("Expression is required.");
        if (expression.Length > MaxLength)
            throw new ExpressionException($"Expression exceeds {MaxLength} characters.");

        _text = expression;
        _pos = 0;
        _depth = 0;

        var value = ParseExpression();
        SkipWhitespace();
        if (_pos != _text.Length)
            throw new ExpressionException($"Unexpected character '{Current}' at position {_pos}.");
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ExpressionException("Expression did not evaluate to a finite number.");
        return value;
    }

    public bool TryEvaluate(string expression, out double value, out string? error)
    {
        try
        {
            value = Evaluate(expression);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            value = 0;
            error = ex.Message;
            return false;
        }
    }

    private char Current => _pos < _text.Length ? _text[_pos] : '\0';

    private void SkipWhitespace()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
    }

    // expression := term (("+" | "-") term)*
    private double ParseExpression()
    {
        var value = ParseTerm();
        while (true)
        {
            SkipWhitespace();
            var op = Current;
            if (op is '+' or '-')
            {
                _pos++;
                var rhs = ParseTerm();
                value = op == '+' ? value + rhs : value - rhs;
            }
            else break;
        }
        return value;
    }

    // term := factor (("*" | "/" | "%") factor)*
    private double ParseTerm()
    {
        var value = ParseFactor();
        while (true)
        {
            SkipWhitespace();
            var op = Current;
            if (op is '*' or '/' or '%')
            {
                _pos++;
                var rhs = ParseFactor();
                value = op switch
                {
                    '*' => value * rhs,
                    '/' => rhs == 0 ? throw new ExpressionException("Division by zero.") : value / rhs,
                    _ => rhs == 0 ? throw new ExpressionException("Modulo by zero.") : value % rhs,
                };
            }
            else break;
        }
        return value;
    }

    // factor := power ("^" factor)?  (right associative)
    private double ParseFactor()
    {
        var value = ParseUnary();
        SkipWhitespace();
        if (Current == '^')
        {
            _pos++;
            var exponent = ParseFactor();
            value = Math.Pow(value, exponent);
        }
        return value;
    }

    private double ParseUnary()
    {
        SkipWhitespace();
        if (Current == '-') { _pos++; return -ParseUnary(); }
        if (Current == '+') { _pos++; return ParseUnary(); }
        return ParsePrimary();
    }

    private double ParsePrimary()
    {
        if (++_depth > MaxDepth) throw new ExpressionException("Expression nesting too deep.");
        try
        {
            SkipWhitespace();
            var c = Current;

            if (c == '(')
            {
                _pos++;
                var value = ParseExpression();
                SkipWhitespace();
                if (Current != ')') throw new ExpressionException("Unbalanced parentheses.");
                _pos++;
                return value;
            }

            if (char.IsLetter(c))
                return ParseFunction();

            if (char.IsDigit(c) || c == '.')
                return ParseNumber();

            throw new ExpressionException($"Unexpected character '{c}' at position {_pos}.");
        }
        finally { _depth--; }
    }

    private double ParseNumber()
    {
        var start = _pos;
        while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] is '.' or 'e' or 'E' or '+' or '-'))
        {
            // Allow signs only immediately after an exponent marker.
            if (_text[_pos] is '+' or '-' && !(_pos > start && (_text[_pos - 1] is 'e' or 'E')))
                break;
            _pos++;
        }

        var slice = _text[start.._pos];
        if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ExpressionException($"Invalid number '{slice}'.");
        return value;
    }

    private double ParseFunction()
    {
        var start = _pos;
        while (_pos < _text.Length && char.IsLetter(_text[_pos])) _pos++;
        var name = _text[start.._pos].ToLowerInvariant();

        SkipWhitespace();
        if (Current != '(') throw new ExpressionException($"Unknown identifier '{name}'.");
        _pos++;

        var args = new List<double> { ParseExpression() };
        SkipWhitespace();
        while (Current == ',')
        {
            _pos++;
            args.Add(ParseExpression());
            SkipWhitespace();
        }
        if (Current != ')') throw new ExpressionException($"Unbalanced parentheses in '{name}'.");
        _pos++;

        return Apply(name, args);
    }

    private static double Apply(string name, IReadOnlyList<double> a) => name switch
    {
        "abs" when a.Count == 1 => Math.Abs(a[0]),
        "sqrt" when a.Count == 1 => a[0] < 0 ? throw new ExpressionException("sqrt of negative number.") : Math.Sqrt(a[0]),
        "floor" when a.Count == 1 => Math.Floor(a[0]),
        "ceil" when a.Count == 1 => Math.Ceiling(a[0]),
        "round" when a.Count == 1 => Math.Round(a[0], MidpointRounding.AwayFromZero),
        "round" when a.Count == 2 => Math.Round(a[0], (int)a[1], MidpointRounding.AwayFromZero),
        "min" when a.Count >= 1 => a.Min(),
        "max" when a.Count >= 1 => a.Max(),
        _ => throw new ExpressionException($"Unknown or misused function '{name}'."),
    };
}
