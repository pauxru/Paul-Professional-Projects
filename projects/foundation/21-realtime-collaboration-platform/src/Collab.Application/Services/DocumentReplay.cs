using Collab.Application.Contracts;
using Collab.Application.Serialization;
using Collab.Domain.Crdt;
using Collab.Domain.Documents;
using Collab.Domain.Structured;

namespace Collab.Application.Services;

/// <summary>
/// Rebuilds document CRDT state from a checkpoint plus a tail of operation-log entries. This is the
/// heart of "snapshot + log replay reproduces the exact document" and of time-travel reads: load the
/// latest snapshot at-or-before the target sequence, then replay the entries after it in order.
/// </summary>
internal static class DocumentReplay
{
    public static RgaDocument BuildText(DocumentSnapshot? snapshot, IEnumerable<OperationLogEntry> tail)
    {
        var doc = snapshot is null
            ? new RgaDocument()
            : RgaDocument.FromState(CollabJson.Deserialize<RgaState>(snapshot.State));

        foreach (var entry in tail)
        {
            var payload = CollabJson.Deserialize<OperationPayload>(entry.Payload);
            foreach (var op in payload.TextOps)
                doc.Apply(op.ToDomain());
        }
        return doc;
    }

    public static StructuredDocument BuildStructured(DocumentSnapshot? snapshot, IEnumerable<OperationLogEntry> tail)
    {
        var doc = snapshot is null
            ? new StructuredDocument()
            : StructuredDocument.FromState(CollabJson.Deserialize<StructuredState>(snapshot.State));

        foreach (var entry in tail)
        {
            var payload = CollabJson.Deserialize<OperationPayload>(entry.Payload);
            foreach (var op in payload.StructuredOps)
                doc.Apply(op.ToDomain());
        }
        return doc;
    }

    public static string RenderText(DocumentSnapshot? snapshot, IEnumerable<OperationLogEntry> tail) =>
        BuildText(snapshot, tail).Materialize();

    public static string RenderStructured(DocumentSnapshot? snapshot, IEnumerable<OperationLogEntry> tail) =>
        BuildStructured(snapshot, tail).ToCanonicalJson();
}
