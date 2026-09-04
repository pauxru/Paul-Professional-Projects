// A faithful browser port of the server's RGA (Replicated Growable Array) text CRDT.
// Character identities are the pair (lamport, replicaId) rendered as "lamport@replica" on the wire,
// exactly matching the C# ElementId. The linear text is a pre-order traversal of the causal tree in
// which siblings are visited in DESCENDING id order, so this converges identically to the server.

export const ROOT_ID = "0@"; // ElementId.Root.ToString() === "0@"

function parseId(id) {
  const at = id.indexOf("@");
  return { lamport: parseInt(id.slice(0, at), 10), replica: id.slice(at + 1) };
}

// Deterministic total order: higher lamport is greater; ties broken by ordinal replica id.
export function compareId(a, b) {
  const pa = parseId(a), pb = parseId(b);
  if (pa.lamport !== pb.lamport) return pa.lamport - pb.lamport;
  return pa.replica < pb.replica ? -1 : pa.replica > pb.replica ? 1 : 0;
}

export class LamportClock {
  constructor(start = 0) { this.value = start; }
  tick() { return ++this.value; }
  observe(remote) { if (remote > this.value) this.value = remote; }
}

export class RgaDocument {
  constructor() {
    this.nodes = new Map();          // idStr -> { id, parent, value, deleted }
    this.children = new Map();       // parentIdStr -> [childIdStr] sorted DESCENDING
    this.children.set(ROOT_ID, []);
    this.pending = [];               // causal buffer for out-of-order ops
  }

  contains(id) { return id === ROOT_ID || this.nodes.has(id); }

  apply(op) {
    const applied = this._applyCore(op);
    if (applied) this._drain();
    return applied;
  }

  _applyCore(op) {
    if (op.type === "insert") {
      if (this.nodes.has(op.id)) return false;          // duplicate
      if (!this.contains(op.reference)) { this._buffer(op); return false; }
      this._link(op.id, op.reference, op.value);
      return true;
    } else {
      const target = this.nodes.get(op.reference);
      if (!target) { this._buffer(op); return false; }
      if (target.deleted) return false;                 // duplicate delete
      target.deleted = true;
      return true;
    }
  }

  _buffer(op) {
    if (!this.pending.some(p => p.type === op.type && p.id === op.id && p.reference === op.reference))
      this.pending.push(op);
  }

  _drain() {
    let progress = true;
    while (progress) {
      progress = false;
      for (let i = this.pending.length - 1; i >= 0; i--) {
        const op = this.pending[i];
        const ready = op.type === "insert"
          ? this.contains(op.reference) && !this.nodes.has(op.id)
          : this.nodes.has(op.reference);
        if (!ready) {
          if (op.type === "insert" && this.nodes.has(op.id)) this.pending.splice(i, 1);
          continue;
        }
        this.pending.splice(i, 1);
        if (this._applyCore(op)) progress = true;
      }
    }
  }

  _link(id, parent, value) {
    this.nodes.set(id, { id, parent, value, deleted: false });
    if (!this.children.has(id)) this.children.set(id, []);
    const siblings = this.children.get(parent) || (this.children.set(parent, []), this.children.get(parent));
    let pos = 0;
    while (pos < siblings.length && compareId(siblings[pos], id) > 0) pos++;
    siblings.splice(pos, 0, id); // keep DESCENDING
  }

  *_traverse() {
    const stack = [];
    const pushChildren = (parent) => {
      const kids = this.children.get(parent);
      if (!kids) return;
      for (let i = kids.length - 1; i >= 0; i--) stack.push(kids[i]); // ascending push → largest pops first
    };
    pushChildren(ROOT_ID);
    while (stack.length) {
      const id = stack.pop();
      yield id;
      pushChildren(id);
    }
  }

  materialize() {
    let s = "";
    for (const id of this._traverse()) {
      const n = this.nodes.get(id);
      if (!n.deleted) s += n.value;
    }
    return s;
  }

  visibleIds() {
    const out = [];
    for (const id of this._traverse()) if (!this.nodes.get(id).deleted) out.push(id);
    return out;
  }

  referenceForInsertAt(index) {
    if (index <= 0) return ROOT_ID;
    const visible = this.visibleIds();
    if (index >= visible.length) return visible.length === 0 ? ROOT_ID : visible[visible.length - 1];
    return visible[index - 1];
  }

  buildInsert(index, text, clock, replicaId) {
    const ops = [];
    let parent = this.referenceForInsertAt(index);
    for (const ch of text) {
      const id = clock.tick() + "@" + replicaId;
      ops.push({ type: "insert", id, reference: parent, value: ch });
      parent = id;
    }
    return ops;
  }

  buildDelete(start, length) {
    const visible = this.visibleIds();
    const ops = [];
    const end = Math.min(visible.length, start + length);
    for (let i = Math.max(0, start); i < end; i++)
      ops.push({ type: "delete", id: "", reference: visible[i], value: "" });
    return ops;
  }

  static fromState(state) {
    const doc = new RgaDocument();
    const ordered = (state.nodes || [])
      .map(n => ({ n, id: n.id }))
      .sort((a, b) => compareId(a.id, b.id));
    for (const { n } of ordered) {
      doc._link(n.id, n.parent, n.value);
      if (n.deleted) doc.nodes.get(n.id).deleted = true;
    }
    return doc;
  }

  maxLamport() {
    let max = 0;
    for (const id of this.nodes.keys()) {
      const l = parseId(id).lamport;
      if (l > max) max = l;
    }
    return max;
  }
}
