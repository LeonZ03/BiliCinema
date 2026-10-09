const jsonHeaders = {
  "content-type": "application/json; charset=utf-8",
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

const roomCodePattern = /^[A-Za-z0-9_-]{20,32}$/;
const idPattern = /^[A-Za-z0-9_-]{20,64}$/;
const routePattern = /^[A-Za-z0-9_-]{32,128}$/;
const now = () => Date.now();

function response(data, status = 200, extraHeaders = {}) {
  return Response.json(data, {
    status,
    headers: { ...jsonHeaders, ...extraHeaders },
  });
}

function failure(status, message, code = "router_error") {
  return response({ error: code, message }, status);
}

function randomToken(bytes = 32) {
  const value = crypto.getRandomValues(new Uint8Array(bytes));
  let binary = "";
  for (const byte of value) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function randomBase64(bytes = 32) {
  const value = crypto.getRandomValues(new Uint8Array(bytes));
  let binary = "";
  for (const byte of value) binary += String.fromCharCode(byte);
  return btoa(binary);
}

async function digest(value) {
  const bytes = new TextEncoder().encode(value);
  const hash = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(hash)].map(byte => byte.toString(16).padStart(2, "0")).join("");
}

function envNumber(env, name, fallback) {
  const value = Number(env[name]);
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

function requireSecrets(env) {
  for (const name of ["CLOUDFLARE_API_TOKEN", "CLOUDFLARE_ACCOUNT_ID", "CLOUDFLARE_ZONE_ID"]) {
    if (!env[name]) throw new RouterError(503, "云端路由尚未完成配置。", "router_not_configured");
  }
}

class RouterError extends Error {
  constructor(status, message, code) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

async function cloudflareApi(env, path, init = {}, acceptNotFound = false) {
  requireSecrets(env);
  const headers = new Headers(init.headers);
  headers.set("authorization", `Bearer ${env.CLOUDFLARE_API_TOKEN}`);
  headers.set("content-type", "application/json");
  let result;
  try {
    result = await fetch(`https://api.cloudflare.com/client/v4${path}`, {
      ...init,
      headers,
    });
  } catch {
    throw new RouterError(502, "Cloudflare 管理接口网络暂时不可用。", "cloudflare_api_network");
  }
  let body = null;
  try { body = await result.json(); } catch { /* handled as a generic API failure */ }
  if ((init.method === "DELETE" || acceptNotFound) && result.status === 404) return { alreadyDeleted: true };
  if (!result.ok || !body?.success) {
    throw new RouterError(502, "Cloudflare 管理接口暂时不可用。", "cloudflare_api_error");
  }
  return body.result;
}

async function createHostTunnel(env, sessionId, originHost, recordResource) {
  const account = encodeURIComponent(env.CLOUDFLARE_ACCOUNT_ID);
  const tunnelSecret = randomBase64(32);
  let tunnelId = null;
  let dnsRecordId = null;
  try {
    const created = await cloudflareApi(env, `/accounts/${account}/cfd_tunnel`, {
      method: "POST",
      body: JSON.stringify({
        name: `bilicinema-host-${sessionId}`,
        config_src: "cloudflare",
        tunnel_secret: tunnelSecret,
      }),
    });
    tunnelId = created.id;
    recordResource("tunnel_id", tunnelId);
    const configuration = await cloudflareApi(env,
      `/accounts/${account}/cfd_tunnel/${encodeURIComponent(tunnelId)}/configurations`, {
        method: "PUT",
        body: JSON.stringify({
          config: {
            ingress: [
              { hostname: originHost, service: "http://127.0.0.1:5077" },
              { service: "http_status:404" },
            ],
          },
        }),
      });
    const zone = encodeURIComponent(env.CLOUDFLARE_ZONE_ID);
    const record = await cloudflareApi(env, `/zones/${zone}/dns_records`, {
      method: "POST",
      body: JSON.stringify({
        type: "CNAME",
        name: originHost,
        content: `${tunnelId}.cfargotunnel.com`,
        proxied: true,
        ttl: 1,
      }),
    });
    dnsRecordId = record.id;
    recordResource("dns_record_id", dnsRecordId);
    const token = await cloudflareApi(env,
      `/accounts/${account}/cfd_tunnel/${encodeURIComponent(tunnelId)}/token`, {
        method: "GET",
      });
    return { tunnelId, dnsRecordId, originHost, tunnelToken: token, configuration };
  } catch (error) { throw error; }
}

async function deleteDnsRecord(env, recordId) {
  const zone = encodeURIComponent(env.CLOUDFLARE_ZONE_ID);
  await cloudflareApi(env, `/zones/${zone}/dns_records/${encodeURIComponent(recordId)}`, {
    method: "DELETE",
  });
}

async function deleteTunnel(env, tunnelId) {
  const account = encodeURIComponent(env.CLOUDFLARE_ACCOUNT_ID);
  const path = `/accounts/${account}/cfd_tunnel/${encodeURIComponent(tunnelId)}`;
  // Revoke the old connector credential before disconnecting it, so an
  // orphaned host cannot reconnect between connection cleanup and deletion.
  const retired = await cloudflareApi(env, path, {
    method: "PATCH",
    body: JSON.stringify({ tunnel_secret: randomBase64(32) }),
  }, true);
  if (retired.alreadyDeleted) return;
  await cloudflareApi(env, `${path}/connections`, { method: "DELETE" });
  await cloudflareApi(env, path, {
    method: "DELETE",
  });
}

function registryStub(env) {
  return env.REGISTRY.get(env.REGISTRY.idFromName("bilicinema-router-v1"));
}

async function registryFetch(env, path, init = {}) {
  return registryStub(env).fetch(`https://registry.internal${path}`, init);
}

async function readJson(request, maxBytes = 32_000) {
  const length = Number(request.headers.get("content-length"));
  if (Number.isFinite(length) && length > maxBytes) {
    throw new RouterError(413, "请求内容过大。", "request_too_large");
  }
  try { return await request.json(); }
  catch { throw new RouterError(400, "请求格式无效。", "invalid_json"); }
}

function bearer(request) {
  const value = request.headers.get("authorization") || "";
  return value.startsWith("Bearer ") ? value.slice(7) : "";
}

function validateRoomCode(value) {
  if (typeof value !== "string" || !roomCodePattern.test(value)) {
    throw new RouterError(400, "房间号格式无效。", "invalid_room");
  }
  return value;
}

function validateId(value, message = "会话标识无效。") {
  if (typeof value !== "string" || !idPattern.test(value)) {
    throw new RouterError(400, message, "invalid_session");
  }
  return value;
}

function validateRouteKey(value) {
  if (typeof value !== "string" || !routePattern.test(value)) {
    throw new RouterError(400, "路由凭据无效。", "invalid_route_key");
  }
  return value;
}

async function handleApi(request, env) {
  const url = new URL(request.url);
  const path = url.pathname;
  if (path === "/api/v1/hosts/session" && request.method === "POST") {
    const body = await readJson(request);
    const operationId = validateId(body.operationId, "操作标识无效。");
    const sessionSecret = validateRouteKey(body.sessionSecret);
    const routeKey = validateRouteKey(body.routeKey);
    const ip = request.headers.get("CF-Connecting-IP") || "unknown";
    return registryFetch(env, "/sessions/create", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ operationId, sessionSecret, routeKey, ipHash: await digest(ip) }),
    });
  }

  const heartbeat = /^\/api\/v1\/hosts\/([A-Za-z0-9_-]{20,64})\/heartbeat$/.exec(path);
  if (heartbeat && request.method === "POST") {
    const body = await readJson(request);
    return registryFetch(env, `/sessions/${heartbeat[1]}/heartbeat`, {
      method: "POST",
      headers: { authorization: `Bearer ${bearer(request)}`, "content-type": "application/json" },
      body: JSON.stringify(body),
    });
  }

  const ready = /^\/api\/v1\/hosts\/([A-Za-z0-9_-]{20,64})\/ready$/.exec(path);
  if (ready && request.method === "GET") {
    const registered = await registryFetch(env, `/sessions/${ready[1]}/ready`, {
      headers: { authorization: `Bearer ${bearer(request)}` },
    });
    if (!registered.ok) return registered;
    const route = await registered.json();
    const reachable = await fetch(`https://${route.originHost}/health`, {
      headers: { "x-bilicinema-route-key": route.routeKey, accept: "application/json" },
    });
    if (!reachable.ok) {
      const text = await reachable.text();
      let problem = null;
      try { problem = JSON.parse(text); } catch { /* only standard numeric codes are returned */ }
      const code = Number.isInteger(problem?.error_code) ? problem.error_code
        : Number(/(?:error(?:\s+code)?|code)\s*[:=]?\s*(1\d{3})/i.exec(text)?.[1]) || null;
      const edgeCode = code ? `，Cloudflare ${code}` : "";
      return failure(503, `主机 Tunnel 尚未就绪（HTTP ${reachable.status}${edgeCode}）。`, "tunnel_not_ready");
    }
    return response({ ok: true });
  }

  const room = /^\/api\/v1\/hosts\/([A-Za-z0-9_-]{20,64})\/rooms\/([A-Za-z0-9_-]{20,32})$/.exec(path);
  if (room && (request.method === "POST" || request.method === "DELETE")) {
    const body = request.method === "POST" ? await readJson(request) : {};
    return registryFetch(env, `/rooms/${room[2]}`, {
      method: request.method,
      headers: { authorization: `Bearer ${bearer(request)}`, "content-type": "application/json" },
      body: JSON.stringify({ ...body, sessionId: room[1] }),
    });
  }

  throw new RouterError(404, "接口不存在。", "not_found");
}

async function lookupRoute(env, roomCode) {
  const result = await registryFetch(env, `/rooms/${encodeURIComponent(roomCode)}`);
  if (!result.ok) return null;
  return result.json();
}

export function bridgeWebSockets(client, upstream) {
  let closed = false;
  const closeBoth = (code = 1000, reason = "closed") => {
    if (closed) return;
    closed = true;
    // Abrupt disconnects report reserved codes (1005/1006/1015), which cannot
    // be sent in a close frame. Normalize them so the other side is released.
    const sendCode = ((code >= 1000 && code <= 1014 && ![1004, 1005, 1006].includes(code))
      || (code >= 3000 && code <= 4999)) ? code : 1000;
    try { client.close(sendCode, reason); } catch {}
    try { upstream.close(sendCode, reason); } catch {}
  };
  client.addEventListener("message", event => {
    try { upstream.send(event.data); } catch { closeBoth(1011, "upstream unavailable"); }
  });
  upstream.addEventListener("message", event => {
    try { client.send(event.data); } catch { closeBoth(1011, "client unavailable"); }
  });
  client.addEventListener("close", event => closeBoth(event.code, event.reason || "client closed"));
  upstream.addEventListener("close", event => closeBoth(event.code, event.reason || "upstream closed"));
  client.addEventListener("error", () => closeBoth(1011, "client error"));
  upstream.addEventListener("error", () => closeBoth(1011, "upstream error"));
}

async function proxyWebSocket(request, route) {
  const pair = new WebSocketPair();
  const [client, server] = Object.values(pair);
  const headers = new Headers(request.headers);
  headers.delete("origin");
  headers.delete("host");
  headers.set("upgrade", "websocket");
  headers.set("x-bilicinema-route-key", route.routeKey);
  const upstream = await fetch(`https://${route.originHost}/ws`, {
    method: "GET",
    headers,
  });
  if (upstream.status !== 101 || !upstream.webSocket) {
    return failure(502, "房主公网通道暂时不可用。", "host_tunnel_unavailable");
  }
  server.binaryType = "arraybuffer";
  upstream.webSocket.binaryType = "arraybuffer";
  server.accept({ allowHalfOpen: true });
  upstream.webSocket.accept({ allowHalfOpen: true });
  bridgeWebSockets(server, upstream.webSocket);
  return new Response(null, { status: 101, webSocket: client });
}

export default {
  async fetch(request, env) {
    try {
      const url = new URL(request.url);
      if (url.pathname === "/health" && request.method === "GET") {
        return response({ ok: true, service: "bilicinema-router", version: "1" });
      }
      if (url.pathname.startsWith("/api/")) return await handleApi(request, env);
      if (url.pathname === "/ws" && request.method === "GET"
          && request.headers.get("upgrade")?.toLowerCase() === "websocket") {
        const roomCode = validateRoomCode(url.searchParams.get("room"));
        const routeResponse = await lookupRoute(env, roomCode);
        if (!routeResponse) return failure(404, "房间已结束或尚未准备好。", "room_unavailable");
        if (routeResponse.status === "offline") {
          return failure(503, "房主通道暂时离线，请等待房主恢复。", "host_offline");
        }
        return await proxyWebSocket(request, routeResponse);
      }
      return failure(404, "页面或接口不存在。", "not_found");
    } catch (error) {
      if (error instanceof RouterError) return failure(error.status, error.message, error.code);
      return failure(500, "云端路由暂时不可用。", "router_failure");
    }
  },
  async scheduled(_event, env, ctx) {
    ctx.waitUntil(registryFetch(env, "/maintenance", { method: "POST" }));
  },
};

export class RoomRouteRegistry {
  constructor(state, env) {
    this.state = state;
    this.env = env;
    this.sql = state.storage.sql;
    this.sql.exec(`
      CREATE TABLE IF NOT EXISTS host_sessions (
        id TEXT PRIMARY KEY,
        operation_id TEXT NOT NULL UNIQUE,
        session_secret_hash TEXT NOT NULL,
        route_key_hash TEXT NOT NULL,
        tunnel_id TEXT NOT NULL,
        dns_record_id TEXT NOT NULL,
        origin_host TEXT NOT NULL,
        last_seen INTEGER NOT NULL,
        expires_at INTEGER NOT NULL,
        cleanup_pending INTEGER NOT NULL DEFAULT 0
      );
      CREATE TABLE IF NOT EXISTS room_routes (
        room_code TEXT PRIMARY KEY,
        host_id TEXT NOT NULL,
        created_at INTEGER NOT NULL,
        expires_at INTEGER NOT NULL
      );
      CREATE INDEX IF NOT EXISTS room_routes_host ON room_routes(host_id);
      CREATE TABLE IF NOT EXISTS create_limits (
        ip_hash TEXT PRIMARY KEY,
        window_started INTEGER NOT NULL,
        count INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS active_route_keys (
        host_id TEXT PRIMARY KEY,
        route_key TEXT NOT NULL
      );
    `);
  }

  async fetch(request) {
    const url = new URL(request.url);
    try {
      if (url.pathname === "/sessions/create" && request.method === "POST") {
        return await this.createSession(await request.json());
      }
      const session = /^\/sessions\/([A-Za-z0-9_-]{20,64})\/(heartbeat|ready)$/.exec(url.pathname);
      if (session && (request.method === "POST" || request.method === "GET")) {
        return await this.sessionAction(session[1], session[2], request);
      }
      const room = /^\/rooms\/([A-Za-z0-9_-]{20,32})$/.exec(url.pathname);
      if (room && request.method === "GET") return await this.getRoute(room[1]);
      if (room && (request.method === "POST" || request.method === "DELETE")) {
        return await this.roomAction(room[1], request);
      }
      if (url.pathname === "/maintenance" && request.method === "POST") {
        await this.cleanupExpired();
        return response({ ok: true });
      }
      return failure(404, "registry route not found", "not_found");
    } catch (error) {
      if (error instanceof RouterError) return failure(error.status, error.message, error.code);
      return failure(500, "registry failure", "registry_failure");
    }
  }

  async createSession(body) {
    const operationId = validateId(body.operationId, "操作标识无效。");
    const sessionSecret = validateRouteKey(body.sessionSecret);
    const routeKey = validateRouteKey(body.routeKey);
    const ipHash = typeof body.ipHash === "string" ? body.ipHash : "unknown";
    const current = now();
    const rateWindow = envNumber(this.env, "CREATE_RATE_WINDOW_MS", 600000);
    const rateLimit = envNumber(this.env, "CREATE_RATE_LIMIT", 10);
    const id = randomToken(24);
    const [secretHash, routeHash, originDigest] = await Promise.all([digest(sessionSecret), digest(routeKey), digest(id)]);
    const originHost = `bilicinema-h-${originDigest.slice(0, 32)}.${this.env.ORIGIN_ZONE}`;
    const existing = this.sql.exec("SELECT * FROM host_sessions WHERE operation_id=?", operationId).toArray()[0];
    if (existing) {
      if (existing.session_secret_hash !== secretHash || existing.route_key_hash !== routeHash) {
        throw new RouterError(403, "主机会话凭据无效。", "session_forbidden");
      }
      if (existing.cleanup_pending || Number(existing.expires_at) <= current) {
        throw new RouterError(404, "主机会话已结束，请重新创建。", "session_unavailable");
      }
      if (!existing.tunnel_id || !existing.dns_record_id) {
        throw new RouterError(503, "主机会话正在准备，请稍后重试。", "session_preparing");
      }
      const token = await this.getTunnelToken(existing.tunnel_id);
      return response(this.sessionResponse(existing, token));
    }
    const limit = this.sql.exec("SELECT window_started,count FROM create_limits WHERE ip_hash=?", ipHash).toArray()[0];
    if (limit && current - Number(limit.window_started) < rateWindow && Number(limit.count) >= rateLimit) {
      throw new RouterError(429, "房间创建请求过于频繁，请稍后重试。", "create_rate_limited");
    }
    if (limit && current - Number(limit.window_started) >= rateWindow) {
      this.sql.exec("UPDATE create_limits SET window_started=?,count=1 WHERE ip_hash=?", current, ipHash);
    } else if (limit) {
      this.sql.exec("UPDATE create_limits SET count=count+1 WHERE ip_hash=?", ipHash);
    } else {
      this.sql.exec("INSERT INTO create_limits VALUES(?,?,1)", ipHash, current);
    }
    const active = this.sql.exec("SELECT COUNT(*) AS count FROM host_sessions WHERE cleanup_pending=0 AND expires_at>?", current).one();
    if (Number(active.count) >= envNumber(this.env, "MAX_HOST_SESSIONS", 64)) {
      throw new RouterError(503, "当前公网房间服务已达到临时容量上限，请稍后重试。", "host_capacity");
    }
    const expiresAt = current + envNumber(this.env, "HOST_EXPIRY_MS", 120000);
    this.sql.exec(
      "INSERT INTO host_sessions VALUES(?,?,?,?,?,?,?,?,?,0)",
      id, operationId, secretHash, routeHash, "", "", originHost, current, expiresAt,
    );
    this.sql.exec("INSERT INTO active_route_keys VALUES(?,?)", id, routeKey);
    await this.scheduleNextAlarm();
    let tunnel;
    try {
      tunnel = await createHostTunnel(this.env, id, originHost, (column, value) => {
        this.sql.exec(`UPDATE host_sessions SET ${column}=? WHERE id=?`, value, id);
      });
    } catch (error) {
      const row = this.sql.exec("SELECT * FROM host_sessions WHERE id=?", id).one();
      this.sql.exec("UPDATE host_sessions SET cleanup_pending=1 WHERE id=?", id);
      await this.cleanupTunnel(row);
      await this.scheduleNextAlarm();
      throw error;
    }
    return response(this.sessionResponse({
      id, origin_host: tunnel.originHost, tunnel_id: tunnel.tunnelId, last_seen: current, expires_at: expiresAt,
    }, tunnel.tunnelToken));
  }

  sessionResponse(row, tunnelToken) {
    return {
      sessionId: row.id,
      tunnelToken,
      originHost: row.origin_host,
      expiresAt: Number(row.expires_at),
      gateway: `${this.env.PUBLIC_ORIGIN || "https://bilicinema.leonz03.dpdns.org"}/ws`,
    };
  }

  async getTunnelToken(tunnelId) {
    const account = encodeURIComponent(this.env.CLOUDFLARE_ACCOUNT_ID);
    return await cloudflareApi(this.env,
      `/accounts/${account}/cfd_tunnel/${encodeURIComponent(tunnelId)}/token`, { method: "GET" });
  }

  async sessionAction(sessionId, action, request) {
    const bearerToken = bearer(request);
    const row = this.sql.exec("SELECT * FROM host_sessions WHERE id=?", sessionId).toArray()[0];
    if (!row || row.cleanup_pending) throw new RouterError(404, "主机会话已结束。", "session_unavailable");
    if (row.session_secret_hash !== await digest(bearerToken)) {
      throw new RouterError(403, "主机会话凭据无效。", "session_forbidden");
    }
    if (action === "ready") {
      return response({ originHost: row.origin_host, routeKey: await this.routeKeyFor(row) });
    }
    const body = await request.json();
    const roomCodes = Array.isArray(body.roomCodes) ? body.roomCodes.filter(value => roomCodePattern.test(value)).slice(0, 256) : [];
    const current = now();
    const expiresAt = current + envNumber(this.env, "HOST_EXPIRY_MS", 120000);
    this.sql.exec("UPDATE host_sessions SET last_seen=?,expires_at=? WHERE id=?", current, expiresAt, sessionId);
    const keep = new Set(roomCodes);
    for (const route of this.sql.exec("SELECT room_code FROM room_routes WHERE host_id=?", sessionId).toArray()) {
      if (!keep.has(route.room_code)) this.sql.exec("DELETE FROM room_routes WHERE room_code=?", route.room_code);
    }
    for (const code of keep) {
      this.sql.exec("UPDATE room_routes SET expires_at=? WHERE room_code=? AND host_id=?", expiresAt, code, sessionId);
    }
    await this.scheduleNextAlarm();
    return response({ ok: true, expiresAt });
  }

  async routeKeyFor(row) {
    // Only the cloud registry retains the route key for the active session.
    // Public readiness responses never include it; the proxy adds it upstream.
    const route = this.sql.exec("SELECT route_key FROM active_route_keys WHERE host_id=?", row.id).toArray()[0];
    return route?.route_key || "";
  }

  async getRoute(roomCode) {
    const row = this.sql.exec(
      "SELECT r.*,h.origin_host,h.route_key_hash,h.last_seen,h.expires_at AS host_expires_at FROM room_routes r JOIN host_sessions h ON h.id=r.host_id WHERE r.room_code=?",
      roomCode,
    ).toArray()[0];
    if (!row) return failure(404, "room unavailable", "room_unavailable");
    const current = now();
    if (Number(row.expires_at) <= current || Number(row.host_expires_at) <= current) {
      await this.expireHostIfNeeded(row.host_id, current);
      return failure(404, "room unavailable", "room_unavailable");
    }
    if (current - Number(row.last_seen) > envNumber(this.env, "HOST_STALE_MS", 60000)) {
      return response({ status: "offline" }, 200);
    }
    const key = this.sql.exec("SELECT route_key FROM active_route_keys WHERE host_id=?", row.host_id).toArray()[0];
    if (!key?.route_key) return failure(503, "route key unavailable", "route_unavailable");
    return response({ status: "ready", originHost: row.origin_host, routeKey: key.route_key });
  }

  async roomAction(roomCode, request) {
    const body = await request.json().catch(() => ({}));
    const sessionId = validateId(body.sessionId);
    const row = this.sql.exec("SELECT * FROM host_sessions WHERE id=?", sessionId).toArray()[0];
    if (!row || row.cleanup_pending || row.session_secret_hash !== await digest(bearer(request))) {
      throw new RouterError(403, "主机会话凭据无效。", "session_forbidden");
    }
    if (request.method === "POST") {
      const current = now();
      const expiresAt = current + envNumber(this.env, "HOST_EXPIRY_MS", 120000);
      const existing = this.sql.exec("SELECT host_id FROM room_routes WHERE room_code=?", roomCode).toArray()[0];
      if (existing && existing.host_id !== sessionId) {
        throw new RouterError(409, "房间路由已经由其他房主占用。", "room_owned");
      }
      this.sql.exec("INSERT OR REPLACE INTO room_routes VALUES(?,?,?,?)", roomCode, sessionId, current, expiresAt);
      this.sql.exec("UPDATE host_sessions SET last_seen=?,expires_at=? WHERE id=?", current, expiresAt, sessionId);
      await this.scheduleNextAlarm();
      return response({
        ok: true,
        inviteUrl: `${this.env.PUBLIC_ORIGIN || "https://bilicinema.leonz03.dpdns.org"}/ws?room=${encodeURIComponent(roomCode)}`,
        gateway: `${this.env.PUBLIC_ORIGIN || "https://bilicinema.leonz03.dpdns.org"}/ws`,
      });
    }
    this.sql.exec("DELETE FROM room_routes WHERE room_code=? AND host_id=?", roomCode, sessionId);
    await this.cleanupEmptySession(sessionId);
    return response({ ok: true });
  }

  async cleanupEmptySession(sessionId) {
    const remaining = this.sql.exec("SELECT COUNT(*) AS count FROM room_routes WHERE host_id=?", sessionId).one();
    if (Number(remaining.count) > 0) return;
    const row = this.sql.exec("SELECT * FROM host_sessions WHERE id=?", sessionId).toArray()[0];
    if (!row) return;
    this.sql.exec("UPDATE host_sessions SET cleanup_pending=1 WHERE id=?", sessionId);
    await this.cleanupTunnel(row);
  }

  async expireHostIfNeeded(sessionId, current = now()) {
    const row = this.sql.exec("SELECT * FROM host_sessions WHERE id=?", sessionId).toArray()[0];
    if (!row || Number(row.expires_at) > current) return;
    this.sql.exec("DELETE FROM room_routes WHERE host_id=?", sessionId);
    this.sql.exec("UPDATE host_sessions SET cleanup_pending=1 WHERE id=?", sessionId);
    await this.cleanupTunnel(row);
  }

  async cleanupExpired() {
    const current = now();
    for (const row of this.sql.exec("SELECT * FROM host_sessions WHERE expires_at<=? OR cleanup_pending=1", current).toArray()) {
      this.sql.exec("DELETE FROM room_routes WHERE host_id=?", row.id);
      this.sql.exec("UPDATE host_sessions SET cleanup_pending=1 WHERE id=?", row.id);
      await this.cleanupTunnel(row).catch(() => {});
    }
    for (const row of this.sql.exec("SELECT ip_hash FROM create_limits WHERE window_started<?", current - envNumber(this.env, "CREATE_RATE_WINDOW_MS", 600000)).toArray()) {
      this.sql.exec("DELETE FROM create_limits WHERE ip_hash=?", row.ip_hash);
    }
    await this.scheduleNextAlarm();
  }

  async cleanupTunnel(row) {
    try {
      let tunnelId = row.tunnel_id;
      let dnsRecordId = row.dns_record_id;
      // A timed-out create request may have committed remotely before the ID
      // arrived. Reconcile only this reserved session's exact resource names.
      if (!tunnelId) {
        const name = `bilicinema-host-${row.id}`;
        const candidates = await cloudflareApi(this.env,
          `/accounts/${encodeURIComponent(this.env.CLOUDFLARE_ACCOUNT_ID)}/cfd_tunnel?name=${encodeURIComponent(name)}&is_deleted=false`);
        tunnelId = candidates.find(value => value.name === name && !value.deleted_at)?.id;
        if (tunnelId) this.sql.exec("UPDATE host_sessions SET tunnel_id=? WHERE id=?", tunnelId, row.id);
      }
      if (!dnsRecordId && tunnelId) {
        const candidates = await cloudflareApi(this.env,
          `/zones/${encodeURIComponent(this.env.CLOUDFLARE_ZONE_ID)}/dns_records?type=CNAME&name=${encodeURIComponent(row.origin_host)}`);
        dnsRecordId = candidates.find(value => value.name === row.origin_host && value.content === `${tunnelId}.cfargotunnel.com`)?.id;
        if (dnsRecordId) this.sql.exec("UPDATE host_sessions SET dns_record_id=? WHERE id=?", dnsRecordId, row.id);
      }
      if (dnsRecordId) {
        await deleteDnsRecord(this.env, dnsRecordId);
        this.sql.exec("UPDATE host_sessions SET dns_record_id='' WHERE id=?", row.id);
      }
      if (tunnelId) {
        await deleteTunnel(this.env, tunnelId);
        this.sql.exec("UPDATE host_sessions SET tunnel_id='' WHERE id=?", row.id);
      }
      this.sql.exec("DELETE FROM active_route_keys WHERE host_id=?", row.id);
      this.sql.exec("DELETE FROM host_sessions WHERE id=?", row.id);
    } catch {
      // Keep the row marked pending so the alarm/cron can retry. Room routes
      // have already been removed, so a failed Cloudflare delete cannot leave
      // a usable zombie room.
      this.sql.exec("UPDATE host_sessions SET cleanup_pending=1,expires_at=? WHERE id=?", now() + 30000, row.id);
    }
  }

  async scheduleNextAlarm() {
    const row = this.sql.exec("SELECT MIN(expires_at) AS next FROM host_sessions").one();
    if (row?.next) await this.state.storage.setAlarm(Math.max(now() + 1000, Number(row.next)));
  }

  async alarm() { await this.cleanupExpired(); }
}
