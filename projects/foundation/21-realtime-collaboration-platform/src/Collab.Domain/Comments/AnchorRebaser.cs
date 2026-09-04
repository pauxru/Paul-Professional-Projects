namespace Collab.Domain.Comments;

/// <summary>
/// A comment's anchor over a text document, expressed as a half-open character range [Start, End).
/// When the underlying text mutates, the anchor is rebased so the comment keeps pointing at the
/// same logical text. If the anchored text is entirely deleted the anchor becomes
/// <see cref="Orphaned"/> — the comment is retained but flagged as detached.
/// </summary>
public readonly record struct TextAnchor(int Start, int End, bool Orphaned = false)
{
    public int Length => Math.Max(0, End - Start);
    public bool IsEmpty => End <= Start;
}

/// <summary>
/// Rebases text anchors against insert/delete edits. This is the "transform against remote ops"
/// mechanic for anchors: the CRDT text itself needs no positional transform (element ids are
/// stable), but anything expressed as an offset — comment anchors and cursors — must be rebased.
/// </summary>
public static class AnchorRebaser
{
    /// <summary>Rebase after <paramref name="count"/> characters are inserted at <paramref name="pos"/>.</summary>
    public static TextAnchor OnInsert(TextAnchor a, int pos, int count)
    {
        if (a.Orphaned || count <= 0) return a;

        // Insertion at or before the start pushes the whole anchor right.
        if (pos <= a.Start) return a with { Start = a.Start + count, End = a.End + count };
        // Insertion strictly inside grows the anchor to include the new text.
        if (pos < a.End) return a with { End = a.End + count };
        // Insertion at or after the end leaves the anchor untouched.
        return a;
    }

    /// <summary>Rebase after characters in [<paramref name="pos"/>, pos+count) are deleted.</summary>
    public static TextAnchor OnDelete(TextAnchor a, int pos, int count)
    {
        if (a.Orphaned || count <= 0) return a;

        var delEnd = pos + count;
        var newStart = MapAfterDelete(a.Start, pos, delEnd, count);
        var newEnd = MapAfterDelete(a.End, pos, delEnd, count);

        // If the anchored characters were all removed the comment is orphaned.
        var orphaned = newEnd <= newStart;
        return new TextAnchor(newStart, orphaned ? newStart : newEnd, orphaned);
    }

    private static int MapAfterDelete(int x, int pos, int delEnd, int count)
    {
        if (x <= pos) return x;
        if (x >= delEnd) return x - count;
        return pos; // inside the deleted span collapses to the deletion point
    }

    /// <summary>Rebase a single cursor caret offset against an insertion.</summary>
    public static int CaretOnInsert(int caret, int pos, int count) =>
        pos <= caret ? caret + count : caret;

    /// <summary>Rebase a single cursor caret offset against a deletion.</summary>
    public static int CaretOnDelete(int caret, int pos, int count)
    {
        var delEnd = pos + count;
        if (caret <= pos) return caret;
        if (caret >= delEnd) return caret - count;
        return pos;
    }
}
