const MAX_MESSAGE_BYTES = 4096;
const MAX_MEMBERS = 5;
const HOST_RECONNECT_MS = 2 * 60 * 1000;
const GUEST_RECONNECT_MS = 5 * 60 * 1000;
const ROOM_TTL_MS = 24 * 60 * 60 * 1000;
const ROOM_CODE = /^[A-Za-z0-9_-]{22}$/;

const json = (value) => JSON.stringify(value);
const now = () => Date.now();

function randomToken(bytes = 16) {
  const data = crypto.getRandomValues(new Uint8Array(bytes));
  let binary = "";
  for (const byte of data) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "").slice(0, 22);
}

function protocolError(code) {
  const error = new Error(code);
  error.code = code;
  return error;
}

function parseMessage(message) {
  if (typeof message !== "string") throw protocolError("text_required");
  const text = message;
  if (new TextEncoder().encode(text).byteLength > MAX_MESSAGE_BYTES) throw protocolError("message_too_large");
  let value;
  try { value = JSON.parse(text); } catch { throw protocolError("invalid_json"); }
  if (!value || typeof value !== "object" || Array.isArray(value) || typeof value.type !== "string") {
    throw protocolError("invalid_message");
  }
  const fields = {
    create: [], join: ["roomCode", "clientId"], select: ["media"],
    play: [], pause: [], seek: ["positionSeconds"], rate: ["rate"],
    sync: ["media", "positionSeconds", "rate", "playing"],
    ready: ["ready"], buffering: ["buffering"], ping: ["clientTimeUnixMs", "nonce"],
    chat: ["nickname", "text"], leave: [], close: [],
  }[value.type];
  if (!fields) throw protocolError("unknown_type");
  if (Object.keys(value).some((key) => key !== "type" && !fields.includes(key))) throw protocolError("unexpected_field");
  return value;
}

function stringField(value, min, max, code = "invalid_string") {
  if (typeof value !== "string" || value.length < min || value.length > max || [...value].some((c) => /[\u0000-\u001f\u007f]/u.test(c))) {
    throw protocolError(code);
  }
  return value;
}

function positiveInteger(value, code = "invalid_number") {
  if (!Number.isSafeInteger(value) || value <= 0) throw protocolError(code);
  return value;
}

function boundedNumber(value, min, max) {
  if (typeof value !== "number" || !Number.isFinite(value) || value < min || value > max) throw protocolError("invalid_number");
  return value;
}

function mediaIdentity(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw protocolError("invalid_media");
  const media = {};
  if (Object.keys(value).some((key) => !["episodeId", "aid", "cid", "bvid"].includes(key))) throw protocolError("unexpected_field");
  if (value.episodeId !== undefined) media.episodeId = positiveInteger(value.episodeId, "invalid_media");
  if (value.aid !== undefined) media.aid = positiveInteger(value.aid, "invalid_media");
  if (value.cid !== undefined) media.cid = positiveInteger(value.cid, "invalid_media");
  if (value.bvid !== undefined) {
    if (typeof value.bvid !== "string" || !/^BV[A-Za-z0-9]{10}$/u.test(value.bvid)) throw protocolError("invalid_media");
    media.bvid = value.bvid;
  }
  if (!Object.keys(media).length) throw protocolError("invalid_media");
  return media;
}

function sameMedia(left, right) {
  return json(left) === json(right);
}

function normalizeChatText(value) {
  if (typeof value !== "string") throw protocolError("invalid_string");
  const text = value.replaceAll("\r\n", "\n");
  if (text.length < 1 || text.length > 500 || [...text].some((c) => /[\u0000-\u0009\u000b\u000c\u000e-\u001f\u007f]/u.test(c)) || !text.trim()) {
    throw protocolError("invalid_chat");
  }
  return text;
}

function defaultState(roomCode) {
  return {
    roomCode,
    createdAt: now(),
    lastActivity: now(),
    version: 1,
    syncRevision: 0,
    media: null,
    positionSeconds: 0,
    rate: 1,
    playing: false,
    startRequested: false,
    anchorAt: now(),
    members: [],
  };
}

function memberView(member) {
  return { memberId: member.memberId, online: member.online, ready: member.ready, buffering: member.buffering };
}

function currentPosition(state, timestamp = now()) {
  if (!state.playing) return Math.min(86400, state.positionSeconds);
  return Math.min(86400, state.positionSeconds + ((timestamp - state.anchorAt) / 1000) * state.rate);
}

function settle(state, timestamp = now()) {
  state.positionSeconds = currentPosition(state, timestamp);
  state.anchorAt = timestamp;
}

function canRun(state) {
  return Boolean(state.media) && state.members.some((m) => m.role === "host" && m.online && m.ready && !m.buffering);
}

function startIfReady(state, timestamp = now()) {
  if (state.startRequested && canRun(state)) {
    state.anchorAt = timestamp;
    state.playing = true;
    state.startRequested = false;
  }
}

export class CloudRoom {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.sql = ctx.storage.sql;
    this.sql.exec("CREATE TABLE IF NOT EXISTS room_state (id INTEGER PRIMARY KEY CHECK (id=1), payload TEXT NOT NULL)");
    ctx.blockConcurrencyWhile(async () => {
      const state = this.getState();
      if (!state) return;
      const connected = new Set(ctx.getWebSockets().map((socket) => socket.deserializeAttachment()?.socketId));
      const missing = state.members.filter((member) => member.online && !connected.has(member.socketId));
      if (!missing.length) return;
      const timestamp = now();
      for (const member of missing) {
        member.online = false;
        member.ready = false;
        member.buffering = false;
        member.disconnectedAt = timestamp;
      }
      if (missing.some((member) => member.role === "host")) {
        settle(state, timestamp);
        state.playing = false;
        state.startRequested = false;
      }
      state.version++;
      await this.save(state);
    });
  }

  getState() {
    const row = this.sql.exec("SELECT payload FROM room_state WHERE id=1").toArray()[0];
    return row ? JSON.parse(row.payload) : null;
  }

  async save(state) {
    state.lastActivity = now();
    this.sql.exec("INSERT OR REPLACE INTO room_state (id,payload) VALUES(1,?)", json(state));
    await this.scheduleAlarm(state);
  }

  async scheduleAlarm(state) {
    const expiries = state.members.map((member) => member.disconnectedAt && member.disconnectedAt + (member.role === "host" ? HOST_RECONNECT_MS : GUEST_RECONNECT_MS)).filter(Boolean);
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment();
      if (!attachment?.memberId) expiries.push((attachment?.connectedAt || now()) + 20000);
    }
    const roomExpiry = state.createdAt + ROOM_TTL_MS;
    const next = Math.min(roomExpiry, ...(expiries.length ? expiries : [roomExpiry]));
    await this.ctx.storage.setAlarm(Math.max(now() + 1000, next));
  }

  async fetch(request) {
    if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return new Response("WebSocket required", { status: 426 });
    const url = new URL(request.url);
    const roomCode = url.searchParams.get("room");
    if (!ROOM_CODE.test(roomCode || "")) return new Response("Invalid room", { status: 400 });
    if (this.ctx.getWebSockets().length >= 16) return new Response("Too many connections", { status: 429 });
    if (!this.getState() && url.searchParams.get("create") !== "1") return new Response("Room unavailable", { status: 404 });
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment({ clientId: null, memberId: null, socketId: randomToken(), roomCode, createAllowed: url.searchParams.get("create") === "1", connectedAt: now() });
    const state = this.getState();
    if (state) await this.scheduleAlarm(state);
    else await this.ctx.storage.setAlarm(now() + 20000);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(socket, message) {
    try {
      const command = parseMessage(message);
      const attachment = socket.deserializeAttachment() || { clientId: null, memberId: null };
      if (now() - (attachment.windowAt || 0) > 10000) { attachment.windowAt = now(); attachment.messages = 0; }
      attachment.messages = (attachment.messages || 0) + 1;
      if (attachment.messages > 60) throw protocolError("rate_limited");
      socket.serializeAttachment(attachment);
      const state = this.getState() || defaultState(attachment.roomCode);
      if (command.type === "create") {
        if (!attachment.createAllowed || attachment.memberId) throw protocolError("join_or_create_required");
        if (state.members.length) throw protocolError("room_exists");
        if (Object.keys(command).some((key) => key !== "type")) throw protocolError("unexpected_field");
        const member = { clientId: randomToken(), memberId: randomToken(), socketId: attachment.socketId, role: "host", online: true, ready: false, buffering: false, disconnectedAt: null };
        state.members = [member];
        state.createdAt = now();
        state.version++;
        socket.serializeAttachment({ ...attachment, clientId: member.clientId, memberId: member.memberId, createAllowed: false });
        await this.save(state);
        this.sendWelcome(socket, state, member);
        this.broadcast(state);
        return;
      }

      if (!state.members.length) throw protocolError("room_unavailable");
      const host = state.members.find((candidate) => candidate.role === "host");
      if (now() >= state.createdAt + ROOM_TTL_MS || (!host.online && now() - host.disconnectedAt >= HOST_RECONNECT_MS)) {
        await this.endRoom(state);
        throw protocolError("room_unavailable");
      }
      if (!attachment.memberId) {
        if (command.type !== "join") throw protocolError("join_or_create_required");
        if (command.roomCode !== state.roomCode) throw protocolError("room_unavailable");
        const requestedClientId = command.clientId === undefined ? null : stringField(command.clientId, 22, 22, "invalid_invite");
        let member = requestedClientId && state.members.find((candidate) => candidate.clientId === requestedClientId);
        if (requestedClientId && !member) throw protocolError("room_unavailable");
        if (member && !member.online && member.disconnectedAt && now() - member.disconnectedAt >= (member.role === "host" ? HOST_RECONNECT_MS : GUEST_RECONNECT_MS)) {
          throw protocolError("room_unavailable");
        }
        if (!member) {
          if (state.members.length >= MAX_MEMBERS) {
            const offline = state.members.filter((candidate) => candidate.role === "guest" && !candidate.online).sort((a, b) => a.disconnectedAt - b.disconnectedAt)[0];
            if (!offline) throw protocolError("room_full");
            state.members = state.members.filter((candidate) => candidate !== offline);
          }
          member = { clientId: randomToken(), memberId: randomToken(), socketId: attachment.socketId, role: "guest", online: true, ready: false, buffering: false, disconnectedAt: null };
          state.members.push(member);
        } else {
          if (member.online) {
            const previous = this.ctx.getWebSockets().find((candidate) => candidate.deserializeAttachment()?.memberId === member.memberId);
            previous?.close(4001, "replaced");
          }
          member.socketId = attachment.socketId;
          member.online = true;
          member.disconnectedAt = null;
          member.ready = false;
          member.buffering = false;
          if (member.role === "host") {
            settle(state);
            state.playing = false;
            state.startRequested = false;
          }
        }
        socket.serializeAttachment({ ...attachment, clientId: member.clientId, memberId: member.memberId, createAllowed: false });
        state.version++;
        await this.save(state);
        this.sendWelcome(socket, state, member);
        this.broadcast(state);
        return;
      }

      const member = state.members.find((candidate) => candidate.memberId === attachment.memberId && candidate.clientId === attachment.clientId);
      if (!member || !member.online || member.socketId !== attachment.socketId) throw protocolError("not_joined");
      const isHost = member.role === "host";
      switch (command.type) {
        case "select":
          if (!isHost) throw protocolError("host_only");
          state.media = mediaIdentity(command.media);
          state.positionSeconds = 0;
          state.anchorAt = now();
          state.playing = false;
          state.startRequested = false;
          state.members.forEach((candidate) => { candidate.ready = false; candidate.buffering = false; });
          break;
        case "play":
          if (!isHost) throw protocolError("host_only");
          if (!state.media) throw protocolError("media_required");
          settle(state);
          state.startRequested = true;
          startIfReady(state);
          break;
        case "pause":
          if (!isHost) throw protocolError("host_only");
          settle(state);
          state.playing = false;
          state.startRequested = false;
          break;
        case "seek":
          if (!isHost || !state.media) throw protocolError(isHost ? "media_required" : "host_only");
          state.positionSeconds = boundedNumber(command.positionSeconds, 0, 86400);
          state.anchorAt = now();
          break;
        case "rate":
          if (!isHost || !state.media) throw protocolError(isHost ? "media_required" : "host_only");
          settle(state);
          state.rate = boundedNumber(command.rate, 0.25, 3);
          break;
        case "sync":
          if (!isHost || !state.media) throw protocolError(isHost ? "media_required" : "host_only");
          if (!sameMedia(state.media, mediaIdentity(command.media))) throw protocolError("media_changed");
          if (typeof command.playing !== "boolean") throw protocolError("invalid_boolean");
          state.positionSeconds = boundedNumber(command.positionSeconds, 0, 86400);
          state.rate = boundedNumber(command.rate, 0.25, 3);
          state.playing = false;
          state.startRequested = command.playing;
          state.anchorAt = now();
          state.syncRevision++;
          startIfReady(state);
          break;
        case "ready":
          if (!state.media) throw protocolError("media_required");
          if (typeof command.ready !== "boolean") throw protocolError("invalid_boolean");
          member.ready = command.ready;
          if (isHost) {
            if (!member.ready && state.playing) { settle(state); state.playing = false; state.startRequested = true; }
            startIfReady(state);
          }
          break;
        case "buffering":
          if (!state.media) throw protocolError("media_required");
          if (typeof command.buffering !== "boolean") throw protocolError("invalid_boolean");
          member.buffering = command.buffering;
          if (isHost) {
            if (member.buffering && state.playing) { settle(state); state.playing = false; state.startRequested = true; }
            else startIfReady(state);
          }
          break;
        case "ping":
          socket.send(json({ type: "pong", clientTimeUnixMs: positiveInteger(command.clientTimeUnixMs), serverTimeUnixMs: now(), ...(command.nonce ? { nonce: stringField(command.nonce, 1, 64) } : {}) }));
          return;
        case "chat": {
          const nickname = stringField(command.nickname, 1, 24).trim();
          const text = normalizeChatText(command.text);
          if (!nickname || !text) throw protocolError("invalid_chat");
          this.broadcastEvent(state, { type: "chat", memberId: member.memberId, role: member.role, nickname, text, sentAtUnixMs: now() });
          return;
        }
        case "leave":
          if (isHost) { await this.endRoom(state); return; }
          state.members = state.members.filter((candidate) => candidate !== member);
          socket.send(json({ type: "left" }));
          socket.serializeAttachment({ ...attachment, memberId: null, clientId: null });
          socket.close(1000, "left");
          break;
        case "close":
          if (!isHost) throw protocolError("host_only");
          await this.endRoom(state);
          return;
        default:
          throw protocolError("unknown_type");
      }
      state.version++;
      await this.save(state);
      if (isHost && ["select", "play", "pause", "seek", "rate"].includes(command.type)) {
        this.broadcastEvent(state, { type: "hostAction", action: command.type, positionSeconds: command.positionSeconds, rate: command.rate }, true);
      }
      this.broadcast(state);
    } catch (error) {
      if (!error?.code) throw error;
      try { socket.send(json({ type: "error", code: error.code })); } catch { socket.close(1011, "connection failed"); }
    }
  }

  async webSocketClose(socket) {
    const attachment = socket.deserializeAttachment();
    if (!attachment?.memberId) return;
    const state = this.getState();
    if (!state) return;
    const member = state.members.find((candidate) => candidate.memberId === attachment.memberId && candidate.clientId === attachment.clientId);
    if (!member || !member.online || member.socketId !== attachment.socketId) return;
    member.online = false;
    member.disconnectedAt = now();
    member.ready = false;
    member.buffering = false;
    if (member.role === "host") { settle(state); state.playing = false; state.startRequested = false; }
    state.version++;
    await this.save(state);
    this.broadcast(state);
  }

  async alarm() {
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment();
      if (!attachment?.memberId && now() - (attachment?.connectedAt || 0) >= 20000) socket.close(1008, "join timeout");
    }
    const state = this.getState();
    if (!state) { this.closeAll(1000, "room unavailable"); await this.ctx.storage.deleteAlarm(); return; }
    const timestamp = now();
    const before = state.members.length;
    state.members = state.members.filter((member) => member.online || !member.disconnectedAt || timestamp - member.disconnectedAt < (member.role === "host" ? HOST_RECONNECT_MS : GUEST_RECONNECT_MS));
    const host = state.members.find((member) => member.role === "host");
    if (!host || timestamp - state.createdAt >= ROOM_TTL_MS || (host.disconnectedAt && timestamp - host.disconnectedAt >= HOST_RECONNECT_MS)) {
      await this.endRoom(state);
      return;
    }
    if (state.members.length !== before) {
      state.version++;
      await this.save(state);
      this.broadcast(state);
    } else {
      await this.scheduleAlarm(state);
    }
  }

  sendWelcome(socket, state, member) {
    socket.send(json({ type: "welcome", roomCode: state.roomCode, clientId: member.clientId, memberId: member.memberId, role: member.role, snapshot: this.snapshot(state) }));
  }

  snapshot(state) {
    return { version: state.version, syncRevision: state.syncRevision, media: state.media, positionSeconds: currentPosition(state), playing: state.playing, rate: state.rate, serverTimeUnixMs: now(), waitingForReady: state.startRequested && !state.playing, memberCount: state.members.filter((member) => member.online).length, capacity: MAX_MEMBERS, host: memberView(state.members.find((member) => member.role === "host") || { memberId: "", online: false, ready: false, buffering: false }), guests: state.members.filter((member) => member.role === "guest").map(memberView) };
  }

  broadcast(state) { this.broadcastEvent(state, { type: "snapshot", snapshot: this.snapshot(state) }); }

  broadcastEvent(state, value, guestsOnly = false) {
    const message = json(value);
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment();
      if (attachment?.memberId && state.members.some((member) => member.memberId === attachment.memberId && member.socketId === attachment.socketId && member.online && (!guestsOnly || member.role === "guest"))) {
        try { socket.send(message); } catch { socket.close(1011, "send failed"); }
      }
    }
  }

  closeAll(code, reason) {
    for (const socket of this.ctx.getWebSockets()) {
      try { socket.close(code, reason); } catch { /* socket already closed */ }
    }
  }

  async endRoom(state) {
    this.broadcastEvent(state, { type: "closed" });
    this.closeAll(1000, "room closed");
    // deleteAll() also drops SQLite tables while this instance may still receive
    // close callbacks and invite requests. Retain the schema, remove all room
    // data and its alarm so subsequent joins deterministically return 404.
    this.sql.exec("DELETE FROM room_state");
    await this.ctx.storage.deleteAlarm();
  }

  webSocketError(socket) { return this.webSocketClose(socket); }
}
