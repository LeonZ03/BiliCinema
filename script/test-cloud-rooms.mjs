// Synthetic rooms only; never print invitation codes or private reconnect IDs.
const endpoint = "wss://bilicinema.leonz03.dpdns.org/v2/ws";

async function connect(room) {
  const socket = new WebSocket(endpoint + (room ? `?room=${encodeURIComponent(room)}` : ""));
  const queue = [];
  let waiting;
  socket.addEventListener("message", (event) => {
    queue.push(JSON.parse(event.data));
    waiting?.();
  });
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { socket.close(); reject(new Error("WSS connection timed out")); }, 45000);
    socket.addEventListener("open", () => { clearTimeout(timeout); resolve(); }, { once: true });
    socket.addEventListener("error", () => { clearTimeout(timeout); reject(new Error("WSS connection failed")); }, { once: true });
  });
  return {
    socket,
    send(value) { socket.send(JSON.stringify(value)); },
    next(predicate) {
      return new Promise((resolve, reject) => {
        const timeout = setTimeout(() => { waiting = null; reject(new Error("Room acknowledgement timed out")); }, 20000);
        const read = () => {
          const index = queue.findIndex(predicate);
          if (index < 0) return;
          clearTimeout(timeout);
          waiting = null;
          resolve(queue.splice(index, 1)[0]);
        };
        waiting = read;
        read();
      });
    },
  };
}

let host;
let guest;
function check(condition, message) {
  if (!condition) throw new Error(message);
}

function checkPlayback(snapshot, playing, waitingForReady, context) {
  check(snapshot.playing === playing && snapshot.waitingForReady === waitingForReady,
    `Unexpected playback state: ${context}`);
}

try {
  const health = await (await fetch(endpoint.replace("wss:", "https:").replace("/v2/ws", "/health"))).json();
  if (health.ok !== true || health.version !== "2") throw new Error("Unexpected service version");
  host = await connect();
  host.send({ type: "create" });
  const welcome = await host.next((value) => value.type === "welcome");
  check(welcome.role === "host" && welcome.snapshot.capacity === 5, "Invalid host membership");
  guest = await connect(welcome.roomCode);
  guest.send({ type: "join", roomCode: welcome.roomCode });
  const joined = await guest.next((value) => value.type === "welcome");
  check(joined.role === "guest" && joined.snapshot.memberCount === 2, "Invalid guest membership");
  let version = joined.snapshot.version;
  const commandSnapshot = async (peer, command) => {
    peer.send(command);
    // A state-changing command must produce a newer snapshot. Older queued
    // snapshots and pong replies cannot acknowledge its resulting room state.
    const result = await host.next((value) => value.type === "snapshot" && value.snapshot.version > version);
    version = result.snapshot.version;
    return result.snapshot;
  };
  const guestState = (snapshot) => snapshot.guests.find((member) => member.memberId === joined.memberId);

  let snapshot = await commandSnapshot(host, {
    type: "select", media: { bvid: "BV1xx411c7mD", aid: 170001, cid: 279786 },
  });
  check(snapshot.media?.cid === 279786 && !snapshot.host.ready && guestState(snapshot)?.ready === false,
    "Selection did not reset member readiness");
  checkPlayback(snapshot, false, false, "selection");
  snapshot = await commandSnapshot(host, { type: "play" });
  checkPlayback(snapshot, false, true, "unready host must wait");
  snapshot = await commandSnapshot(host, { type: "ready", ready: true });
  checkPlayback(snapshot, true, false, "ready host starts without ready guest");
  check(guestState(snapshot)?.ready === false, "Guest unexpectedly reported ready");

  snapshot = await commandSnapshot(guest, { type: "ready", ready: true });
  checkPlayback(snapshot, true, false, "guest readiness");
  check(guestState(snapshot)?.ready === true, "Guest readiness was not recorded");
  snapshot = await commandSnapshot(guest, { type: "buffering", buffering: true });
  checkPlayback(snapshot, true, false, "guest buffering");
  check(guestState(snapshot)?.buffering === true, "Guest buffering was not recorded");
  snapshot = await commandSnapshot(guest, { type: "ready", ready: false });
  checkPlayback(snapshot, true, false, "guest becomes unavailable");
  check(guestState(snapshot)?.ready === false, "Guest unavailable state was not recorded");

  guest.send({ type: "chat", nickname: "Smoke test", text: "Synthetic protocol check" });
  const chat = await host.next((value) => value.type === "chat" && value.memberId === joined.memberId
    && value.text === "Synthetic protocol check");
  check(chat.role === "guest" && chat.nickname === "Smoke test", "Chat lost authenticated sender identity");
  await guest.next((value) => value.type === "chat" && value.memberId === joined.memberId
    && value.text === "Synthetic protocol check");

  snapshot = await commandSnapshot(host, { type: "buffering", buffering: true });
  checkPlayback(snapshot, false, true, "host buffering preserves play intent");
  const bufferingPosition = snapshot.positionSeconds;
  snapshot = await commandSnapshot(guest, { type: "buffering", buffering: false });
  checkPlayback(snapshot, false, true, "guest recovery cannot release host gate");
  check(guestState(snapshot)?.buffering === false, "Guest buffering recovery was not recorded");
  snapshot = await commandSnapshot(guest, { type: "ready", ready: true });
  checkPlayback(snapshot, false, true, "guest readiness cannot release host gate");
  snapshot = await commandSnapshot(host, { type: "buffering", buffering: false });
  checkPlayback(snapshot, true, false, "host buffering recovery resumes intent");
  check(snapshot.positionSeconds >= bufferingPosition, "Host buffering recovery rewound the timeline");

  snapshot = await commandSnapshot(host, { type: "buffering", buffering: true });
  checkPlayback(snapshot, false, true, "second host buffering pause");
  snapshot = await commandSnapshot(host, { type: "pause" });
  checkPlayback(snapshot, false, false, "explicit host pause clears intent");
  const pausedPosition = snapshot.positionSeconds;
  snapshot = await commandSnapshot(host, { type: "buffering", buffering: false });
  checkPlayback(snapshot, false, false, "buffering recovery after explicit pause");
  snapshot = await commandSnapshot(host, { type: "ready", ready: false });
  checkPlayback(snapshot, false, false, "readiness loss after explicit pause");
  snapshot = await commandSnapshot(host, { type: "ready", ready: true });
  checkPlayback(snapshot, false, false, "readiness recovery after explicit pause");
  check(snapshot.positionSeconds === pausedPosition, "Explicitly paused timeline advanced");

  snapshot = await commandSnapshot(host, { type: "play" });
  checkPlayback(snapshot, true, false, "explicit host restart");
  snapshot = await commandSnapshot(guest, { type: "leave" });
  checkPlayback(snapshot, true, false, "guest departure");
  check(snapshot.memberCount === 1 && snapshot.guests.length === 0, "Guest departure did not free its seat");
  await guest.next((value) => value.type === "left");
  guest.socket.close();
  guest = await connect(welcome.roomCode);
  guest.send({ type: "join", roomCode: welcome.roomCode });
  const closingGuest = await guest.next((value) => value.type === "welcome");
  checkPlayback(closingGuest.snapshot, true, false, "new guest joining active room");
  check(closingGuest.snapshot.version > version && closingGuest.role === "guest"
    && closingGuest.snapshot.memberCount === 2, "New guest did not receive current room membership");
  host.send({ type: "close" });
  await guest.next((value) => value.type === "closed");
  await host.next((value) => value.type === "closed");
  let reopened = false;
  try { const late = await connect(welcome.roomCode); late.socket.close(); reopened = true; }
  catch { /* A closed invitation must refuse the WebSocket handshake. */ }
  if (reopened) throw new Error("Closed invitation was accepted");
  process.stdout.write("Live cloud rooms passed: create, join, host readiness, guest independence, chat, host buffering and pause intent, guest leave, close, expired invite.\n");
} catch (error) {
  process.stderr.write(`Live cloud rooms failed: ${error.message}\n`);
  process.exitCode = 1;
} finally {
  if (host?.socket.readyState === WebSocket.OPEN) host.send({ type: "close" });
  guest?.socket.close();
  host?.socket.close();
}
