using Lakehouse.Domain.Data;

namespace Lakehouse.Domain.Cdc;

/// <summary>Change operation in a CDC feed. I=insert, U=update, D=delete (tombstone).</summary>
public enum ChangeOp
{
    Insert,
    Update,
    Delete
}

public static class ChangeOpExtensions
{
    public static string Code(this ChangeOp op) => op switch
    {
        ChangeOp.Insert => "I",
        ChangeOp.Update => "U",
        ChangeOp.Delete => "D",
        _ => "?"
    };

    public static ChangeOp Parse(string code) => code?.ToUpperInvariant() switch
    {
        "I" => ChangeOp.Insert,
        "U" => ChangeOp.Update,
        "D" => ChangeOp.Delete,
        _ => throw new ArgumentException($"Unknown change op '{code}'.", nameof(code))
    };
}

/// <summary>
/// One row-level change emitted by a source system's change feed. <see cref="Sequence"/> is a global
/// monotonically-increasing log position used to order and de-duplicate; <see cref="CommitTs"/> is the
/// source-side commit time (which may arrive late or out of order relative to sequence).
/// </summary>
public sealed record ChangeEvent(
    string Source,
    string Entity,
    ChangeOp Op,
    long Sequence,
    DateTimeOffset CommitTs,
    string Key,
    Row After);
