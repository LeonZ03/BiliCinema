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
    const timeout = setTimeout(() => { socket.close(); reject(new Error("WSS connection timed out")); }, 20000);
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
try {
  const health = await (await fetch(endpoint.replace("wss:", "https:").replace("/v2/ws", "/health"))).json();
  if (health.ok !== true || health.version !== "2") throw new Error("Unexpected service version");
  host = await connect();
  host.send({ type: "create" });
  const welcome = await host.next((value) => value.type === "welcome");
  guest = await connect(welcome.roomCode);
  guest.send({ type: "join", roomCode: welcome.roomCode });
  await guest.next((value) => value.type === "welcome");
  host.send({ type: "select", media: { bvid: "BV1xx411c7mD", aid: 170001, cid: 279786 } });
  await host.next((value) => value.type === "snapshot" && value.snapshot.media?.cid === 279786);
  host.send({ type: "ready", ready: true });
  host.send({ type: "play" });
  await host.next((value) => value.type === "snapshot" && value.snapshot.waitingForReady);
  guest.send({ type: "ready", ready: true });
  await host.next((value) => value.type === "snapshot" && value.snapshot.playing);
  guest.send({ type: "chat", nickname: "Smoke test", text: "Synthetic protocol check" });
  await host.next((value) => value.type === "chat" && value.text === "Synthetic protocol check");
  host.send({ type: "close" });
  await guest.next((value) => value.type === "closed");
  await host.next((value) => value.type === "closed");
  let reopened = false;
  try { const late = await connect(welcome.roomCode); late.socket.close(); reopened = true; }
  catch { /* A closed invitation must refuse the WebSocket handshake. */ }
  if (reopened) throw new Error("Closed invitation was accepted");
  process.stdout.write("Live cloud rooms passed: create, join, ready barrier, chat, close, expired invite.\n");
} catch (error) {
  process.stderr.write(`Live cloud rooms failed: ${error.message}\n`);
  process.exitCode = 1;
} finally {
  if (host?.socket.readyState === WebSocket.OPEN) host.send({ type: "close" });
  guest?.socket.close();
  host?.socket.close();
}
