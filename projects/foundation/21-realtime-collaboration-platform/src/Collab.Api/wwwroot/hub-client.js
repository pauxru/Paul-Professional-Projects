// A tiny, dependency-free SignalR client speaking the JSON hub protocol directly over a WebSocket.
// We connect straight to the hub (skipping negotiation) with the JWT in the access_token query
// string, perform the handshake, frame messages with the 0x1e record separator, and support
// client→server invocations plus server→client event handlers. This is deliberately hand-vendored
// so the web client needs no npm install (documented in the README).

const RS = "\u001e"; // record separator

export class HubClient {
  constructor(url, accessToken) {
    this.url = url;
    this.accessToken = accessToken;
    this.ws = null;
    this.nextId = 0;
    this.pending = new Map();     // invocationId -> {resolve, reject}
    this.handlers = new Map();    // target -> [fn]
    this.buffer = "";
    this._handshakeResolve = null;
    this._handshakeReject = null;
    this._pingTimer = null;
  }

  on(target, fn) {
    if (!this.handlers.has(target)) this.handlers.set(target, []);
    this.handlers.get(target).push(fn);
  }

  start() {
    const sep = this.url.includes("?") ? "&" : "?";
    const full = `${this.url}${sep}access_token=${encodeURIComponent(this.accessToken)}`;
    this.ws = new WebSocket(full);

    return new Promise((resolve, reject) => {
      this._handshakeResolve = resolve;
      this._handshakeReject = reject;
      this.ws.onopen = () => this._send({ protocol: "json", version: 1 });
      this.ws.onerror = () => reject(new Error("WebSocket error"));
      this.ws.onclose = () => {
        if (this._pingTimer) clearInterval(this._pingTimer);
        for (const { reject } of this.pending.values()) reject(new Error("Connection closed"));
        this.pending.clear();
        const closeHandlers = this.handlers.get("__closed__") || [];
        closeHandlers.forEach(fn => fn());
      };
      this.ws.onmessage = (evt) => this._onMessage(evt.data);
    });
  }

  onClosed(fn) { this.on("__closed__", fn); }

  invoke(target, ...args) {
    const invocationId = String(this.nextId++);
    const message = { type: 1, invocationId, target, arguments: args };
    return new Promise((resolve, reject) => {
      this.pending.set(invocationId, { resolve, reject });
      this._send(message);
    });
  }

  send(target, ...args) {
    this._send({ type: 1, target, arguments: args });
  }

  stop() {
    if (this.ws) this.ws.close();
  }

  _send(obj) {
    this.ws.send(JSON.stringify(obj) + RS);
  }

  _onMessage(data) {
    this.buffer += typeof data === "string" ? data : "";
    let idx;
    while ((idx = this.buffer.indexOf(RS)) >= 0) {
      const frame = this.buffer.slice(0, idx);
      this.buffer = this.buffer.slice(idx + 1);
      if (frame.length === 0) continue;
      this._handleFrame(frame);
    }
  }

  _handleFrame(frame) {
    let msg;
    try { msg = JSON.parse(frame); } catch { return; }

    // The very first frame is the handshake response ({} or {"error":"..."}).
    if (this._handshakeResolve) {
      const resolve = this._handshakeResolve, reject = this._handshakeReject;
      this._handshakeResolve = this._handshakeReject = null;
      if (msg.error) { reject(new Error(msg.error)); return; }
      this._pingTimer = setInterval(() => this._send({ type: 6 }), 15000); // keepalive ping
      resolve();
      return;
    }

    switch (msg.type) {
      case 1: { // server → client invocation (event)
        const fns = this.handlers.get(msg.target) || [];
        fns.forEach(fn => fn(...(msg.arguments || [])));
        break;
      }
      case 3: { // completion of a client invocation
        const p = this.pending.get(msg.invocationId);
        if (!p) break;
        this.pending.delete(msg.invocationId);
        if (msg.error) p.reject(new Error(msg.error));
        else p.resolve(msg.result);
        break;
      }
      case 6: // ping
        break;
      case 7: // close
        if (this.ws) this.ws.close();
        break;
    }
  }
}
