import test from "node:test";
import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import { RoomRouteRegistry, bridgeWebSockets } from "../src/worker.mjs";

function createHarness({ failConfiguration = false, failTunnelDeletes = 0, activeConnectors = false, alreadyDeletedTunnels = false } = {}) {
  const db = new DatabaseSync(":memory:");
  const sql = {
    exec(query, ...args) {
      const trimmed = query.trimStart();
      if (/^(SELECT|WITH)/i.test(trimmed)) {
        const statement = db.prepare(query);
        return {
          one: () => {
            const rows = statement.all(...args);
            if (rows.length !== 1) throw new Error("Expected exactly one row");
            return rows[0];
          },
          toArray: () => statement.all(...args),
        };
      }
      if (!args.length) db.exec(query);
      else db.prepare(query).run(...args);
      return { one: () => undefined, toArray: () => [] };
    },
  };
  const alarms = [];
  const state = { storage: { sql, async setAlarm(value) { alarms.push(value); } } };
  let tunnelNumber = 0;
  const calls = [];
  let credentialRevoked = false;
  let connectionsClosed = false;
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url, init = {}) => {
    const target = String(url);
    calls.push([target, init.method || "GET"]);
    if (target.includes("api.cloudflare.com")) {
      if (target.endsWith("/cfd_tunnel") && init.method === "POST") {
        tunnelNumber += 1;
        return Response.json({ success: true, result: { id: `tunnel-${tunnelNumber}` } });
      }
      if (target.includes("/configurations")) {
        return failConfiguration
          ? Response.json({ success: false }, { status: 503 })
          : Response.json({ success: true, result: {} });
      }
      if (target.includes("/cfd_tunnel?") || target.includes("/dns_records?")) return Response.json({ success: true, result: [] });
      if (target.includes("/dns_records") && init.method === "POST") {
        return Response.json({ success: true, result: { id: `dns-${tunnelNumber}` } });
      }
      if (target.endsWith("/token")) return Response.json({ success: true, result: "tunnel-token" });
      if (target.includes("/cfd_tunnel/") && init.method === "PATCH") {
        if (alreadyDeletedTunnels) return Response.json({ success: false }, { status: 404 });
        credentialRevoked = true;
        return Response.json({ success: true, result: {} });
      }
      if (init.method === "DELETE") {
        if (target.endsWith("/connections")) {
          assert.ok(credentialRevoked, "Disconnect only after revoking the old connector credential");
          connectionsClosed = true;
        } else if (target.includes("/cfd_tunnel/")) {
          if (activeConnectors && !connectionsClosed) return Response.json({ success: false }, { status: 409 });
          if (failTunnelDeletes > 0) {
            failTunnelDeletes -= 1;
            return Response.json({ success: false }, { status: 503 });
          }
        }
        return Response.json({ success: true, result: {} });
      }
    }
    if (target.includes("https://bilicinema-h-")) return new Response("ok", { status: 200 });
    return Response.json({ success: true, result: {} });
  };
  const env = {
    CLOUDFLARE_API_TOKEN: "test",
    CLOUDFLARE_ACCOUNT_ID: "account",
    CLOUDFLARE_ZONE_ID: "zone",
    PUBLIC_ORIGIN: "https://bilicinema.leonz03.dpdns.org",
    ORIGIN_ZONE: "leonz03.dpdns.org",
    HOST_EXPIRY_MS: "120000",
    HOST_STALE_MS: "60000",
    MAX_HOST_SESSIONS: "64",
  };
  const registry = new RoomRouteRegistry(state, env);
  return { registry, db, alarms, calls, restore: () => { globalThis.fetch = originalFetch; } };
}

const sessionBody = {
  operationId: "operation-12345678901234567890",
  sessionSecret: "session-secret-123456789012345678901234567890",
  routeKey: "test-route-" + "x".repeat(40),
};
const roomCode = "room-code-1234567890123456";

test("an aborted guest closes its upstream socket with a valid close code", () => {
  const makeSocket = () => ({
    handlers: new Map(), closedWith: null,
    addEventListener(name, handler) { this.handlers.set(name, handler); },
    close(code) {
      if ([1005, 1006, 1015].includes(code)) throw new Error("Reserved close code");
      this.closedWith = code;
    },
  });
  const client = makeSocket();
  const upstream = makeSocket();
  bridgeWebSockets(client, upstream);
  client.handlers.get("close")({ code: 1006, reason: "" });
  assert.equal(upstream.closedWith, 1000);
});

test("host session registers, routes and deletes a room", async () => {
  const harness = createHarness();
  try {
    const jsonHeaders = { "content-type": "application/json" };
    let result = await harness.registry.fetch(new Request("https://registry/sessions/create", {
      method: "POST",
      headers: jsonHeaders,
      body: JSON.stringify({ ...sessionBody, ipHash: "ip" }),
    }));
    assert.equal(result.status, 200);
    const session = await result.json();
    assert.match(session.originHost, /^bilicinema-h-[a-f0-9]{32}\.leonz03\.dpdns\.org$/);
    const auth = {
      authorization: `Bearer ${sessionBody.sessionSecret}`,
      "content-type": "application/json",
    };

    result = await harness.registry.fetch(new Request(`https://registry/rooms/${roomCode}`, {
      method: "POST",
      headers: auth,
      body: JSON.stringify({ sessionId: session.sessionId }),
    }));
    assert.equal(result.status, 200);

    result = await harness.registry.fetch(new Request(`https://registry/rooms/${roomCode}`));
    assert.equal(result.status, 200);
    const route = await result.json();
    assert.equal(route.status, "ready");
    assert.equal(route.routeKey, sessionBody.routeKey);

    result = await harness.registry.fetch(new Request(`https://registry/rooms/${roomCode}`, {
      method: "DELETE",
      headers: auth,
      body: JSON.stringify({ sessionId: session.sessionId }),
    }));
    assert.equal(result.status, 200);

    result = await harness.registry.fetch(new Request(`https://registry/rooms/${roomCode}`));
    assert.equal(result.status, 404);
    assert.ok(harness.calls.some(([, method]) => method === "DELETE"));
  } finally {
    harness.restore();
  }
});

test("different hosts share the gateway without crossing room routes", async () => {
  const harness = createHarness();
  try {
    const sessions = [];
    for (const suffix of ["a", "b"]) {
      const credentials = {
        ...sessionBody,
        operationId: sessionBody.operationId + suffix,
        sessionSecret: sessionBody.sessionSecret + suffix,
        routeKey: sessionBody.routeKey + suffix,
      };
      const created = await harness.registry.fetch(new Request("https://registry/sessions/create", {
        method: "POST", headers: { "content-type": "application/json" },
        body: JSON.stringify(credentials),
      }));
      assert.equal(created.status, 200);
      const session = await created.json();
      const room = roomCode + suffix;
      const init = {
        method: "POST",
        headers: { authorization: `Bearer ${credentials.sessionSecret}`, "content-type": "application/json" },
        body: JSON.stringify({ sessionId: session.sessionId }),
      };
      assert.equal((await harness.registry.fetch(new Request(`https://registry/rooms/${room}`, init))).status, 200);
      sessions.push({ session, room, init, credentials });
    }
    assert.notEqual(sessions[0].session.originHost, sessions[1].session.originHost);
    for (const item of sessions) {
      const route = await (await harness.registry.fetch(new Request(`https://registry/rooms/${item.room}`))).json();
      assert.equal(route.originHost, item.session.originHost);
      assert.equal(route.routeKey, item.credentials.routeKey);
    }
    const first = sessions[0];
    assert.equal((await harness.registry.fetch(new Request(`https://registry/rooms/${first.room}`,
      { ...first.init, method: "DELETE" }))).status, 200);
    assert.equal((await harness.registry.fetch(new Request(`https://registry/rooms/${first.room}`))).status, 404);
    const remaining = await (await harness.registry.fetch(new Request(`https://registry/rooms/${sessions[1].room}`))).json();
    assert.equal(remaining.status, "ready");
    assert.equal(remaining.originHost, sessions[1].session.originHost);
  } finally { harness.restore(); }
});

test("wrong host secret cannot register a route", async () => {
  const harness = createHarness();
  try {
    const result = await harness.registry.fetch(new Request(`https://registry/rooms/${roomCode}`, {
      method: "POST",
      headers: {
        authorization: "Bearer wrong-secret-123456789012345678901234567890",
        "content-type": "application/json",
      },
      body: JSON.stringify({ sessionId: "missing-session-123456789012345678" }),
    }));
    assert.equal(result.status, 403);
  } finally {
    harness.restore();
  }
});

test("retries reuse the host session without consuming the new-session allowance", async () => {
  const harness = createHarness();
  try {
    let firstSessionId;
    for (let attempt = 0; attempt < 5; attempt += 1) {
      const result = await harness.registry.fetch(new Request("https://registry/sessions/create", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ ...sessionBody, ipHash: "retry-ip" }),
      }));
      assert.equal(result.status, 200);
      const session = await result.json();
      firstSessionId ??= session.sessionId;
      assert.equal(session.sessionId, firstSessionId);
    }
    assert.equal(harness.calls.filter(([url, method]) => url.endsWith("/cfd_tunnel") && method === "POST").length, 1);
    assert.equal(harness.db.prepare("SELECT count FROM create_limits WHERE ip_hash=?").get("retry-ip").count, 1);
  } finally {
    harness.restore();
  }
});

test("expired host session removes routes and retries Cloudflare cleanup", async () => {
  const harness = createHarness();
  try {
    const jsonHeaders = { "content-type": "application/json" };
    const create = await harness.registry.fetch(new Request("https://registry/sessions/create", {
      method: "POST",
      headers: jsonHeaders,
      body: JSON.stringify({ ...sessionBody, operationId: "operation-expiry-123456789012345678" }),
    }));
    const session = await create.json();
    const auth = {
      authorization: `Bearer ${sessionBody.sessionSecret}`,
      "content-type": "application/json",
    };
    await harness.registry.fetch(new Request(`https://registry/rooms/${roomCode}`, {
      method: "POST",
      headers: auth,
      body: JSON.stringify({ sessionId: session.sessionId }),
    }));
    harness.db.exec("UPDATE host_sessions SET expires_at=0");
    const maintenance = await harness.registry.fetch(new Request("https://registry/maintenance", { method: "POST" }));
    assert.equal(maintenance.status, 200);
    const lookup = await harness.registry.fetch(new Request(`https://registry/rooms/${roomCode}`));
    assert.equal(lookup.status, 404);
    assert.ok(harness.calls.filter(([, method]) => method === "DELETE").length >= 2);
  } finally {
    harness.restore();
  }
});

test("failed provisioning keeps durable cleanup ownership when deletion fails", async () => {
  const harness = createHarness({ failConfiguration: true, failTunnelDeletes: 1 });
  try {
    const result = await harness.registry.fetch(new Request("https://registry/sessions/create", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(sessionBody),
    }));
    assert.equal(result.status, 502);
    const retained = harness.db.prepare("SELECT tunnel_id,cleanup_pending FROM host_sessions").get();
    assert.equal(retained.tunnel_id, "tunnel-1");
    assert.equal(retained.cleanup_pending, 1);
    await harness.registry.fetch(new Request("https://registry/maintenance", { method: "POST" }));
    assert.equal(harness.db.prepare("SELECT COUNT(*) AS count FROM host_sessions").get().count, 0);
  } finally {
    harness.restore();
  }
});

test("cleanup retries a failed tunnel delete after DNS has already been removed", async () => {
  const harness = createHarness({ failTunnelDeletes: 1 });
  try {
    const created = await harness.registry.fetch(new Request("https://registry/sessions/create", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(sessionBody),
    }));
    const session = await created.json();
    const result = await harness.registry.fetch(new Request(`https://registry/rooms/${roomCode}`, {
      method: "DELETE",
      headers: { "content-type": "application/json", authorization: `Bearer ${sessionBody.sessionSecret}` },
      body: JSON.stringify({ sessionId: session.sessionId }),
    }));
    assert.equal(result.status, 200);
    const retained = harness.db.prepare("SELECT dns_record_id,cleanup_pending FROM host_sessions").get();
    assert.equal(retained.dns_record_id, "");
    assert.equal(retained.cleanup_pending, 1);
    await harness.registry.fetch(new Request("https://registry/maintenance", { method: "POST" }));
    assert.equal(harness.db.prepare("SELECT COUNT(*) AS count FROM host_sessions").get().count, 0);
    assert.equal(harness.calls.filter(([url, method]) => url.includes("/dns_records/") && method === "DELETE").length, 1);
  } finally {
    harness.restore();
  }
});

for (const [name, options] of [
  ["active orphan connectors", { activeConnectors: true }],
  ["an already deleted tunnel", { alreadyDeletedTunnels: true }],
]) {
  test(`expired host cleanup handles ${name}`, async () => {
    const harness = createHarness(options);
    try {
      const created = await harness.registry.fetch(new Request("https://registry/sessions/create", {
        method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(sessionBody),
      }));
      assert.equal(created.status, 200);
      harness.db.exec("UPDATE host_sessions SET expires_at=0");
      await harness.registry.fetch(new Request("https://registry/maintenance", { method: "POST" }));
      assert.equal(harness.db.prepare("SELECT COUNT(*) AS count FROM host_sessions").get().count, 0);
      assert.equal(harness.db.prepare("SELECT COUNT(*) AS count FROM active_route_keys").get().count, 0);
      if (options.activeConnectors) {
        const operations = harness.calls.filter(([url]) => url.includes("/cfd_tunnel/tunnel-1"));
        assert.deepEqual(operations.slice(-3).map(([url, method]) => [url.endsWith("/connections"), method]),
          [[false, "PATCH"], [true, "DELETE"], [false, "DELETE"]]);
      }
    } finally { harness.restore(); }
  });
}
