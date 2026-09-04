using System.Text;

namespace Sagas.Core;

/// <summary>
/// The set of variable names a model talks about, fixed once per saga.
///
/// Sharing a schema between every state in a run is what makes states cheap to
/// compare: two states are equal when their value vectors are equal, with no
/// dictionary walk and no key comparison. A model checker spends most of its
/// life deciding "have I seen this before?", so that comparison is the hot path.
/// </summary>
public sealed class Schema
{
    private readonly Dictionary<string, int> _index;

    public Schema(params string[] names)
    {
        if (names.Length == 0)
        {
            throw new ArgumentException("a schema with no variables cannot describe anything", nameof(names));
        }

        Names = (string[])names.Clone();
        _index = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++)
        {
            if (!_index.TryAdd(names[i], i))
            {
                throw new ArgumentException($"duplicate variable '{names[i]}'", nameof(names));
            }
        }
    }

    public string[] Names { get; }

    public int Count => Names.Length;

    public int IndexOf(string name) =>
        _index.TryGetValue(name, out var i)
            ? i
            : throw new KeyNotFoundException(
                $"'{name}' is not in the schema; declared variables are {string.Join(", ", Names)}");

    public WorldState State(params (string Name, int Value)[] assignments)
    {
        var values = new int[Count];
        foreach (var (name, value) in assignments)
        {
            values[IndexOf(name)] = value;
        }

        return new WorldState(this, values);
    }
}

/// <summary>
/// A complete assignment of every variable in a schema. Immutable and
/// value-equal: <c>With</c> returns a new state rather than mutating.
///
/// Every participant's effect is modelled as a pure function of this. That is a
/// real modelling restriction -- see docs/known-limitations.md -- but it is the
/// restriction that makes exhaustive exploration possible at all.
/// </summary>
public sealed class WorldState : IEquatable<WorldState>
{
    private readonly int[] _values;
    private readonly int _hash;

    internal WorldState(Schema schema, int[] values)
    {
        Schema = schema;
        _values = values;

        // Cached on construction. States are created once and compared many
        // times; recomputing this per visited-set probe showed up as roughly a
        // fifth of total runtime before it was cached.
        var h = new HashCode();
        foreach (var v in values)
        {
            h.Add(v);
        }

        _hash = h.ToHashCode();
    }

    public Schema Schema { get; }

    public int this[string name] => _values[Schema.IndexOf(name)];

    public int At(int index) => _values[index];

    public WorldState With(string name, int value)
    {
        var index = Schema.IndexOf(name);
        if (_values[index] == value)
        {
            return this;
        }

        var next = (int[])_values.Clone();
        next[index] = value;
        return new WorldState(Schema, next);
    }

    public WorldState Add(string name, int delta) => delta == 0 ? this : With(name, this[name] + delta);

    public bool Equals(WorldState? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other._hash != _hash || !ReferenceEquals(other.Schema, Schema))
        {
            return false;
        }

        return _values.AsSpan().SequenceEqual(other._values);
    }

    public override bool Equals(object? obj) => Equals(obj as WorldState);

    public override int GetHashCode() => _hash;

    /// <summary>Only the variables that differ, which is what a counterexample needs.</summary>
    public string DiffFrom(WorldState baseline)
    {
        var parts = new List<string>();
        for (var i = 0; i < _values.Length; i++)
        {
            if (_values[i] != baseline._values[i])
            {
                parts.Add($"{Schema.Names[i]}: {baseline._values[i]} -> {_values[i]}");
            }
        }

        return parts.Count == 0 ? "(no change)" : string.Join(", ", parts);
    }

    public override string ToString()
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < _values.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(Schema.Names[i]).Append('=').Append(_values[i]);
        }

        return sb.Append('}').ToString();
    }
}
