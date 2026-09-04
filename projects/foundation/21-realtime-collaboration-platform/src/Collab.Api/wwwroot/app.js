import { RgaDocument, LamportClock, ROOT_ID } from "./rga.js";
import { HubClient } from "./hub-client.js";

const $ = (id) => document.getElementById(id);
const log = (m) => { const el = $("log"); el.textContent = `${new Date().toLocaleTimeString()}  ${m}\n` + el.textContent; };

let token = null, userId = null, replicaId = null;
let hub = null, rga = new RgaDocument(), clock = new LamportClock();
let docId = null, docType = 0, lastValue = "", connected = false, applyingRemote = false;

function lamportOf(op) {
  const id = op.type === "insert" ? op.id : op.reference;
  return parseInt(id.slice(0, id.indexOf("@")), 10) || 0;
}

// Minimal prefix/suffix diff of the textarea to derive a positional insert/delete.
function diffChange(oldStr, newStr) {
  const min = Math.min(oldStr.length, newStr.length);
  let p = 0;
  while (p < min && oldStr[p] === newStr[p]) p++;
  let s = 0;
  while (s < min - p && oldStr[oldStr.length - 1 - s] === newStr[newStr.length - 1 - s]) s++;
  return { pos: p, deleted: oldStr.slice(p, oldStr.length - s), inserted: newStr.slice(p, newStr.length - s) };
}

async function login() {
  const email = $("email").value.trim();
  const res = await fetch("/api/v1/auth/token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, displayName: email.split("@")[0] })
  });
  if (!res.ok) { log(`Login failed (${res.status})`); return; }
  const body = await res.json();
  token = body.token; userId = body.userId; replicaId = userId.slice(0, 8);
  $("who").textContent = `${body.displayName} (${userId.slice(0, 8)})`;
  $("connect").disabled = false;
  log(`Signed in as ${body.email}`);
}

async function connect() {
  docId = $("docId").value.trim();
  const scheme = location.protocol === "https:" ? "wss" : "ws";
  hub = new HubClient(`${scheme}://${location.host}/hubs/collaboration`, token);

  hub.on("OperationApplied", onOperationApplied);
  hub.on("PresenceChanged", onPresenceChanged);
  hub.on("Rejected", (r) => log(`Rejected: ${r.code} — ${r.reason}`));
  hub.on("Resynced", (r) => log(`Resynced (full=${r.full}, seq=${r.currentSequence})`));
  hub.on("CommentAdded", (c) => log(`Comment added: ${c.body}`));
  hub.on("NotificationReceived", (n) => log(`Notification: ${n.message}`));
  hub.onClosed(() => { connected = false; $("status").textContent = "disconnected"; log("Connection closed"); });

  await hub.start();
  connected = true;
  $("status").textContent = "connected";

  const state = await hub.invoke("JoinDocument", docId);
  docType = state.type;
  if (docType === 0) {
    rga = RgaDocument.fromState(JSON.parse(state.state));
    clock = new LamportClock(rga.maxLamport());
    lastValue = state.content;
    const ta = $("editor");
    ta.value = state.content;
    ta.disabled = false;
  } else {
    lastValue = state.content;
    $("editor").value = state.content;
    $("editor").disabled = true;
    log("Structured document — editing via UI not supported in this minimal client.");
  }
  log(`Joined ${docId} at sequence ${state.sequence}`);

  setInterval(() => { if (connected) hub.invoke("Ping").catch(() => {}); }, 10000);
}

function onEditorInput() {
  if (applyingRemote || docType !== 0) return;
  const oldStr = lastValue, newStr = $("editor").value;
  if (oldStr === newStr) return;
  const { pos, deleted, inserted } = diffChange(oldStr, newStr);

  const ops = [];
  if (deleted.length) { const d = rga.buildDelete(pos, deleted.length); d.forEach(o => rga.apply(o)); ops.push(...d); }
  if (inserted.length) { const ins = rga.buildInsert(pos, inserted, clock, replicaId); ins.forEach(o => rga.apply(o)); ops.push(...ins); }
  lastValue = newStr;

  if (ops.length) {
    const envelope = { documentId: docId, replicaId, textOps: ops, structuredOps: [], clientTag: crypto.randomUUID() };
    hub.invoke("SubmitOperation", envelope).catch((e) => log(`Submit error: ${e.message}`));
  }
  sendPresence();
}

function onOperationApplied(broadcast) {
  if (docType !== 0) return;
  const ta = $("editor");
  const before = ta.value, caret = ta.selectionStart;
  (broadcast.textOps || []).forEach((op) => { clock.observe(lamportOf(op)); rga.apply(op); });
  const after = rga.materialize();

  const { pos } = diffChange(before, after);
  let newCaret = caret;
  if (pos <= caret) newCaret = caret + (after.length - before.length);

  applyingRemote = true;
  ta.value = after;
  lastValue = after;
  newCaret = Math.max(0, Math.min(after.length, newCaret));
  ta.setSelectionRange(newCaret, newCaret);
  applyingRemote = false;
}

function onPresenceChanged(snapshot) {
  const others = (snapshot.participants || []).filter((p) => p.userId !== userId);
  const list = $("presence");
  list.innerHTML = "";
  for (const p of (snapshot.participants || [])) {
    const li = document.createElement("li");
    const me = p.userId === userId ? " (you)" : "";
    const status = p.status === 1 ? "idle" : "active";
    li.textContent = `${p.userName}${me} — cursor ${p.cursorStart}..${p.cursorEnd} [${status}]`;
    list.appendChild(li);
  }
  $("peers").textContent = String(others.length);
}

let presenceTimer = null;
function sendPresence() {
  if (!connected || docType !== 0) return;
  if (presenceTimer) return;
  presenceTimer = setTimeout(() => {
    presenceTimer = null;
    const ta = $("editor");
    hub.send("UpdatePresence", docId, { cursorStart: ta.selectionStart, cursorEnd: ta.selectionEnd, status: 0 });
  }, 120);
}

$("loginBtn").addEventListener("click", () => login().catch((e) => log(e.message)));
$("connect").addEventListener("click", () => connect().catch((e) => log(`Connect error: ${e.message}`)));
$("editor").addEventListener("input", onEditorInput);
["keyup", "click", "select"].forEach((ev) => $("editor").addEventListener(ev, sendPresence));
