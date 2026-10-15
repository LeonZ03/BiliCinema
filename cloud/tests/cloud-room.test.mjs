import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join as joinPath } from "node:path";
import { afterEach, beforeEach, test } from "node:test";
import { Miniflare, convertV4MiniflareOptions } from "miniflare";

let mf;

async function createMiniflare(extra = {}) {
  const cloudRoomPath = "src/cloud-room.mjs";
  let cloudRoomContents;
  if (extra.testClock) {
    cloudRoomContents = await readFile(cloudRoomPath, "utf8");
    cloudRoomContents = cloudRoomContents
      .replace("const now = () => Date.now();", "const now = () => globalThis.__testNow ?? Date.now();")
      .replace("  async fetch(request) {", `  async fetch(request) {
    const testUrl = new URL(request.url);
    if (testUrl.pathname === "/__test/advance") {
      globalThis.__testNow = Number(testUrl.searchParams.get("time"));
      if (testUrl.searchParams.get("alarm") === "1") await this.alarm();
      return new Response("ok");
    }`);
  }
  return new Miniflare(convertV4MiniflareOptions({
    resourcePersistencePath: extra.resourcePersistencePath,
    bindings: extra.bindings,
    modules: [
      { type: "ESModule", path: "src/worker.mjs" },
      { type: "ESModule", path: cloudRoomPath, ...(cloudRoomContents ? { contents: cloudRoomContents } : {}) },
    ],
    durableObjects: {
      REGISTRY: { className: "RoomRouteRegistry", useSQLite: true },
      ROOMS: { className: "CloudRoom", useSQLite: true },
    },
    compatibilityDate: "2026-10-08",
  }));
}

beforeEach(async () => { mf = await createMiniflare(); });

afterEach(async () => mf?.dispose());

async function connect(roomCode, headers = {}) {
  const suffix = roomCode ? `?room=${encodeURIComponent(roomCode)}` : "";
  const response = await mf.dispatchFetch(`http://localhost/v2/ws${suffix}`, {
    headers: { upgrade: "websocket", ...headers },
  });
  if (response.status !== 101) return { response };
  const socket = response.webSocket;
  socket.accept();
  const messages = [];
  socket.addEventListener("message", event => messages.push(JSON.parse(event.data)));
  socket.addEventListener("error", () => {});
  return { socket, messages, response };
}

function send(peer, value) { peer.socket.send(JSON.stringify(value)); }

async function message(peer, predicate = () => true) {
  const found = peer.messages.findIndex(predicate);
  if (found >= 0) return peer.messages.splice(found, 1)[0];
  return new Promise((resolve, reject) => {
    let pollTimer;
    const timeout = setTimeout(() => {
      clearTimeout(pollTimer);
      reject(new Error("Timed out waiting for room message"));
    }, 1500);
    const poll = () => {
      const index = peer.messages.findIndex(predicate);
      if (index < 0) {
        pollTimer = setTimeout(poll, 5);
        return;
      }
      clearTimeout(timeout);
      resolve(peer.messages.splice(index, 1)[0]);
    };
    poll();
  });
}

async function createRoom() {
  const host = await connect();
  assert.equal(host.response.status, 101);
  send(host, { type: "create" });
  const welcome = await message(host, value => value.type === "welcome");
  return { host, welcome };
}

async function join(roomCode, clientId) {
  const guest = await connect(roomCode);
  assert.equal(guest.response.status, 101);
  send(guest, { type: "join", roomCode, ...(clientId ? { clientId } : {}) });
  return { guest, welcome: await message(guest, value => value.type === "welcome") };
}

async function nextError(peer) { return message(peer, value => value.type === "error"); }

test("v2 create and join use the room query and return reconnect identities", async () => {
  const { host, welcome: hostWelcome } = await createRoom();
  assert.equal(hostWelcome.role, "host");
  assert.equal(hostWelcome.snapshot.capacity, 5);
  assert.equal(hostWelcome.roomCode.length, 22);

  const { welcome: guestWelcome } = await join(hostWelcome.roomCode);
  assert.equal(guestWelcome.role, "guest");
  assert.match(guestWelcome.clientId, /^[A-Za-z0-9_-]{22}$/);
  assert.match(guestWelcome.memberId, /^[A-Za-z0-9_-]{22}$/);
  assert.equal(guestWelcome.snapshot.memberCount, 2);
  host.socket.close();
});

test("only the room creator can create, hosts own playback controls, and capacity is five", async () => {
  const { host, welcome } = await createRoom();
  const second = await connect(welcome.roomCode);
  send(second, { type: "create" });
  assert.equal((await nextError(second)).code, "join_or_create_required");

  const guests = [];
  for (let index = 0; index < 4; index++) guests.push(await join(welcome.roomCode));
  const full = await connect(welcome.roomCode);
  send(full, { type: "join", roomCode: welcome.roomCode });
  assert.equal((await nextError(full)).code, "room_full");

  send(guests[0].guest, { type: "select", media: { aid: 1 } });
  assert.equal((await nextError(guests[0].guest)).code, "host_only");
  for (const item of guests) item.guest.socket.close();
  host.socket.close();
});

test("play waits for every online member to pass the ready barrier", async () => {
  const { host, welcome } = await createRoom();
  const { guest } = await join(welcome.roomCode);
  send(host, { type: "select", media: { aid: 123 } });
  await message(host, value => value.type === "snapshot" && value.snapshot.media?.aid === 123);
  send(host, { type: "play" });
  const waiting = await message(host, value => value.type === "snapshot" && value.snapshot.waitingForReady);
  assert.equal(waiting.snapshot.playing, false);
  send(host, { type: "ready", ready: true });
  await message(host, value => value.type === "snapshot" && value.snapshot.host.ready);
  send(guest, { type: "ready", ready: true });
  const started = await message(host, value => value.type === "snapshot" && value.snapshot.playing);
  assert.equal(started.snapshot.waitingForReady, false);
  guest.socket.close();
  host.socket.close();
});

test("chat is shared only as chat data and protocol fields cannot carry secrets", async () => {
  const { host, welcome } = await createRoom();
  const { guest, welcome: guestWelcome } = await join(welcome.roomCode);
  send(guest, { type: "chat", nickname: "Guest", text: "hello" });
  const chat = await message(host, value => value.type === "chat");
  assert.deepEqual(Object.keys(chat).sort(), ["memberId", "nickname", "role", "sentAtUnixMs", "text", "type"]);
  assert.equal(chat.text, "hello");
  assert.equal(chat.memberId, guestWelcome.memberId);
  send(guest, { type: "chat", nickname: "Guest", text: "x", sessionSecret: "must-not-be-accepted" });
  assert.equal((await nextError(guest)).code, "unexpected_field");
  guest.socket.close();
  host.socket.close();
});

test("reconnect replaces a stale socket and its later close cannot mark the member offline", async () => {
  const { host, welcome } = await createRoom();
  const { guest: oldGuest, welcome: prior } = await join(welcome.roomCode);
  const { guest: newGuest, welcome: resumed } = await join(welcome.roomCode, prior.clientId);
  assert.equal(resumed.memberId, prior.memberId);
  assert.equal(resumed.clientId, prior.clientId);
  host.messages.length = 0;
  oldGuest.socket.close();
  send(newGuest, { type: "ping", clientTimeUnixMs: Date.now(), nonce: "reconnect-barrier" });
  assert.equal((await message(newGuest, value => value.type === "pong" && value.nonce === "reconnect-barrier")).nonce, "reconnect-barrier");
  send(host, { type: "select", media: { aid: 987 } });
  const snapshot = await message(host, value => value.type === "snapshot" && value.snapshot.media?.aid === 987);
  assert.equal(snapshot.snapshot.guests.length, 1);
  assert.equal(snapshot.snapshot.guests[0].memberId, resumed.memberId);
  assert.equal(snapshot.snapshot.guests[0].online, true);
  assert.equal(snapshot.snapshot.memberCount, 2);
  newGuest.socket.close();
  host.socket.close();
});

test("host close ends the room and later joins are unavailable", async () => {
  const { host, welcome } = await createRoom();
  send(host, { type: "close" });
  await message(host, value => value.type === "closed");
  const late = await connect(welcome.roomCode);
  assert.equal(late.response.status, 404);
});

test("room membership survives a workerd restart and resumes by clientId", async () => {
  const persistencePath = await mkdtemp(joinPath(tmpdir(), "bilicinema-miniflare-"));
  try {
    await mf.dispose();
    mf = await createMiniflare({ resourcePersistencePath: persistencePath });
    const { host, welcome } = await createRoom();
    const { guest, welcome: joined } = await join(welcome.roomCode);
    host.socket.close();
    const disconnected = await message(guest, value => value.type === "snapshot" && value.snapshot.host.online === false);
    assert.equal(disconnected.snapshot.memberCount, 1);
    await mf.dispose();

    mf = await createMiniflare({ resourcePersistencePath: persistencePath });
    const { guest: resumedGuest, welcome: resumed } = await join(welcome.roomCode, joined.clientId);
    assert.equal(resumed.role, "guest");
    assert.equal(resumed.memberId, joined.memberId);
    assert.equal(resumed.clientId, joined.clientId);
    assert.equal(resumed.snapshot.host.online, false);
    assert.equal(resumed.snapshot.memberCount, 1);
    resumedGuest.socket.close();
  } finally {
    await mf.dispose();
    await rm(persistencePath, { recursive: true, force: true });
  }
});

test("alarm expires disconnected guests without ending the room", async () => {
  await mf.dispose();
  mf = await createMiniflare({ testClock: true });
  const { host, welcome } = await createRoom();
  const { guest } = await join(welcome.roomCode);
  guest.socket.close();
  await message(host, value => value.type === "snapshot" && value.snapshot.guests[0]?.online === false);

  const rooms = await mf.getDurableObjectNamespace("ROOMS");
  const room = rooms.get(rooms.idFromName(welcome.roomCode));
  const future = Date.now() + 6 * 60 * 1000;
  const alarmResponse = await room.fetch(`https://room.internal/__test/advance?time=${future}&alarm=1`);
  assert.equal(alarmResponse.status, 200);
  const afterExpiry = await message(host, value => value.type === "snapshot" && value.snapshot.guests.length === 0);
  assert.equal(afterExpiry.snapshot.memberCount, 1);
  host.socket.close();
});

test("host reconnect grace expires after two minutes", async () => {
  await mf.dispose();
  mf = await createMiniflare({ testClock: true });
  const { host, welcome } = await createRoom();
  const { guest } = await join(welcome.roomCode);
  const rooms = await mf.getDurableObjectNamespace("ROOMS");
  const room = rooms.get(rooms.idFromName(welcome.roomCode));
  const baseline = Date.now();
  await room.fetch(`https://room.internal/__test/advance?time=${baseline}`);
  host.socket.close();
  await message(guest, value => value.type === "snapshot" && value.snapshot.host.online === false);

  const alarmResponse = await room.fetch(`https://room.internal/__test/advance?time=${baseline + 2 * 60 * 1000 + 1}&alarm=1`);
  assert.equal(alarmResponse.status, 200);
  await message(guest, value => value.type === "closed");
  assert.equal((await connect(welcome.roomCode)).response.status, 404);
});

test("24-hour room expiry closes an online room", async () => {
  await mf.dispose();
  mf = await createMiniflare({ testClock: true });
  const { host, welcome } = await createRoom();
  const rooms = await mf.getDurableObjectNamespace("ROOMS");
  const room = rooms.get(rooms.idFromName(welcome.roomCode));
  const future = Date.now() + 24 * 60 * 60 * 1000 + 1;
  const alarmResponse = await room.fetch(`https://room.internal/__test/advance?time=${future}&alarm=1`);
  assert.equal(alarmResponse.status, 200);
  await message(host, value => value.type === "closed");
  assert.equal((await connect(welcome.roomCode)).response.status, 404);
});

test("cloud room creation admission returns 429 after the configured limit", async () => {
  await mf.dispose();
  mf = await createMiniflare({ bindings: { CLOUD_CREATE_RATE_LIMIT: "1" } });
  const registryNamespace = await mf.getDurableObjectNamespace("REGISTRY");
  const registry = registryNamespace.get(registryNamespace.idFromName("bilicinema-router-v1"));
  const request = () => registry.fetch("https://registry.internal/cloud-create", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ ipHash: "a".repeat(64) }),
  });
  assert.equal((await request()).status, 200);
  const limited = await request();
  assert.equal(limited.status, 429);
  assert.equal((await limited.json()).error, "create_rate_limited");
});

test("new rooms reject a foreign browser origin and invalid room codes", async () => {
  const foreignOrigin = await mf.dispatchFetch("http://localhost/v2/ws", {
    headers: { upgrade: "websocket", origin: "https://attacker.example" },
  });
  assert.equal(foreignOrigin.status, 403);
  const invalidRoom = await mf.dispatchFetch("http://localhost/v2/ws?room=short", {
    headers: { upgrade: "websocket" },
  });
  assert.equal(invalidRoom.status, 400);
});
