package com.leonz03.bilicinema;

import android.os.Handler;
import android.os.Looper;
import org.json.JSONObject;
import java.util.concurrent.TimeUnit;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;

/** Owns exactly one room connection; reconnect credentials never enter an invite. */
final class RoomClient {
    interface Listener {
        void message(JSONObject message); void status(String message); void credentials(String room, String client);
        default void connectionProgress(RoomConnectionProgress progress) { }
    }
    static final String ENDPOINT = "wss://bilicinema.leonz03.dpdns.org/v2/ws";
    private final Handler main = new Handler(Looper.getMainLooper());
    private final OkHttpClient http = new OkHttpClient.Builder().connectTimeout(30, TimeUnit.SECONDS).readTimeout(0, TimeUnit.SECONDS).pingInterval(30, TimeUnit.SECONDS).build();
    private final HttpResourceLifecycle network = new HttpResourceLifecycle(http);
    private final Listener listener;
    private WebSocket socket;
    private String room = "", client = "";
    private boolean stopped = true;
    private boolean reconnecting;
    private int generation, attempt, welcomedGeneration = -1;
    private long reconnectDeadline;
    private JSONObject pendingWelcome;
    private RoomConnectionProgress progress;
    RoomClient(Listener listener) { this.listener = listener; }
    RoomConnectionProgress progress() { return progress; }
    private static long monotonicMillis() { return System.nanoTime() / 1_000_000; }
    void connect(String code, String credential, RoomConnectionProgress.Operation operation) {
        stop(false);
        room = code; client = credential; stopped = false; attempt = 0; reconnecting = false;
        reconnectDeadline = System.currentTimeMillis() + 110_000;
        progress = RoomConnectionProgress.start(operation, monotonicMillis(), !code.isEmpty());
        emitProgress();
        open(++generation);
    }
    boolean retry() {
        if (progress == null || progress.outcome != RoomConnectionProgress.Outcome.FAILED) return false;
        // A received credential belongs to the original room: retries must join it, never create a replacement.
        connect(room, client, progress.operation);
        return true;
    }
    private void emitProgress() { listener.connectionProgress(progress); }
    private void advance(RoomConnectionProgress.Stage stage) {
        progress = progress.advance(stage, monotonicMillis()); emitProgress();
    }
    private void open(int current) {
        if (stopped || current != generation) return;
        pendingWelcome = null;
        listener.status(attempt == 0 ? "正在连接云端房间…" : "连接已断开，正在重连（" + attempt + "）…");
        String url = ENDPOINT + (room.isEmpty() ? "" : "?room=" + room);
        try {
            socket = http.newWebSocket(new Request.Builder().url(url).build(), new WebSocketListener() {
                @Override public void onOpen(WebSocket ws, Response response) {
                    main.post(() -> {
                    if (stopped || current != generation) { network.cancel(ws::cancel); return; }
                    advance(RoomConnectionProgress.Stage.SENDING_REQUEST);
                    try {
                        JSONObject first = new JSONObject().put("type", room.isEmpty() ? "create" : "join");
                        if (!room.isEmpty()) first.put("roomCode", room);
                        if (!client.isEmpty()) first.put("clientId", client);
                        if (!ws.send(first.toString())) { fail(current, "房间请求未能发送，请重试"); return; }
                        advance(RoomConnectionProgress.Stage.WAITING_ROOM);
                    } catch (Exception error) { fail(current, "房间请求未能发送，请重试"); }
                    });
                }
                @Override public void onMessage(WebSocket ws, String text) {
                    main.post(() -> {
                        if (stopped || current != generation) return;
                        receive(current, text);
                    });
                }
                @Override public void onFailure(WebSocket ws, Throwable error, Response response) {
                    if (response != null && (response.code() == 404 || response.code() == 403)) {
                        main.post(() -> fail(current, "房间已结束或邀请已失效，请取消后重新加入"));
                    } else lost(current);
                }
                @Override public void onClosed(WebSocket ws, int code, String reason) { lost(current); }
                @Override public void onClosing(WebSocket ws, int code, String reason) { ws.close(code, reason); }
            });
            main.postDelayed(() -> {
                if (stopped || current != generation || welcomedGeneration == current) return;
                if (reconnecting) lost(current); else fail(current, "等待房间响应超时，请重试或取消");
            }, 45_000);
        } catch (RuntimeException error) { lost(current); }
    }
    private void receive(int current, String text) {
        if (stopped || current != generation) return;
        try {
            JSONObject message = new JSONObject(text);
            String type = message.optString("type");
            if ("welcome".equals(type)) {
                if (welcomedGeneration == current) return;
                if (!validWelcome(message)) { fail(current, "收到的房间信息不完整，请重试"); return; }
                room = message.getString("roomCode"); client = message.getString("clientId");
                listener.credentials(room, client); pendingWelcome = message;
                advance(RoomConnectionProgress.Stage.SYNCHRONIZING);
                if (validSnapshot(message.optJSONObject("snapshot"))) finishWelcome(current, message);
                return;
            }
            if ("snapshot".equals(type) && pendingWelcome != null) {
                JSONObject snapshot = message.optJSONObject("snapshot");
                if (validSnapshot(snapshot)) finishWelcome(current, new JSONObject(pendingWelcome.toString()).put("snapshot", snapshot));
                return;
            }
            if ("error".equals(type) && progress.outcome == RoomConnectionProgress.Outcome.RUNNING) {
                fail(current, connectionError(message.optString("code"))); return;
            }
            if (welcomedGeneration != current) return;
            if ("closed".equals(type) || "left".equals(type) || ("error".equals(type) && terminal(message.optString("code")))) stop(false);
            listener.message(message);
        } catch (Exception error) {
            if (progress != null && progress.outcome == RoomConnectionProgress.Outcome.RUNNING) fail(current, "无法确认房间响应，请重试");
            else listener.status("收到无法识别的房间消息，请重连");
        }
    }
    private void finishWelcome(int current, JSONObject message) {
        // Complete only after the activity has applied the authoritative snapshot.
        listener.message(message);
        if (stopped || current != generation) return;
        pendingWelcome = null; attempt = 0; reconnecting = false; welcomedGeneration = current;
        advance(RoomConnectionProgress.Stage.COMPLETE);
        listener.status("房间已连接"); ping(current);
    }
    static boolean validWelcome(JSONObject message) {
        return message != null && message.optString("roomCode").matches("[A-Za-z0-9_-]{22}")
                && message.optString("clientId").matches("[A-Za-z0-9_-]{22}")
                && message.optString("memberId").matches("[A-Za-z0-9_-]{22}")
                && ("host".equals(message.optString("role")) || "guest".equals(message.optString("role")));
    }
    static boolean validSnapshot(JSONObject snapshot) {
        return snapshot != null && snapshot.optLong("version", -1) >= 0 && snapshot.optLong("syncRevision", -1) >= 0
                && snapshot.optJSONObject("host") != null && snapshot.optJSONArray("guests") != null
                && snapshot.has("media") && snapshot.opt("playing") instanceof Boolean
                && Double.isFinite(snapshot.optDouble("positionSeconds", Double.NaN))
                && snapshot.optDouble("positionSeconds") >= 0 && snapshot.optDouble("positionSeconds") <= 86400
                && Double.isFinite(snapshot.optDouble("rate", Double.NaN))
                && snapshot.optDouble("rate") >= .25 && snapshot.optDouble("rate") <= 3;
    }
    private void fail(int current, String message) {
        if (stopped || current != generation) return;
        progress = progress.fail(message, monotonicMillis());
        invalidateSocket(false); reconnecting = false; emitProgress();
    }
    private void lost(int current) {
        main.post(() -> {
            if (stopped || current != generation) return;
            if (!reconnecting && progress.outcome == RoomConnectionProgress.Outcome.RUNNING) {
                fail(current, "连接在当前阶段中断，请检查网络后重试"); return;
            }
            if (!reconnecting) { reconnecting = true; reconnectDeadline = System.currentTimeMillis() + 110_000; }
            if (room.isEmpty() || client.isEmpty() || System.currentTimeMillis() >= reconnectDeadline) {
                fail(current, "重连窗口已结束，可重试或取消连接");
                return;
            }
            invalidateSocket(false); stopped = false;
            attempt++;
            progress = RoomConnectionProgress.start(RoomConnectionProgress.Operation.RECONNECT, monotonicMillis()); emitProgress();
            try { listener.message(new JSONObject().put("type", "transportLost")); } catch (Exception error) { listener.status("连接已断开，正在重连"); }
            int next = ++generation;
            main.postDelayed(() -> open(next), Math.min(10_000, 1000L << Math.min(attempt, 3)));
        });
    }
    private static String connectionError(String code) {
        if ("room_full".equals(code)) return "房间已满（最多5人），请稍后重试或取消";
        if ("invalid_client".equals(code) || "invalid_client_id".equals(code) || "client_expired".equals(code)) return "恢复凭据已失效，请取消后用邀请重新加入";
        if (terminal(code)) return "房间已结束或邀请已失效，请取消后重新加入";
        return "房间请求未完成，请稍后重试或取消";
    }
    private void ping(int current) {
        main.postDelayed(() -> {
            if (stopped || current != generation) return;
            try { send(new JSONObject().put("type", "ping").put("clientTimeUnixMs", System.currentTimeMillis())); } catch (Exception ignored) { listener.status("时间校准失败，请重连"); }
            ping(current);
        }, 30_000);
    }
    static boolean terminal(String code) { return "room_unavailable".equals(code) || "room_not_found".equals(code) || "room_expired".equals(code) || "invalid_client".equals(code) || "invalid_client_id".equals(code) || "client_expired".equals(code) || "room_full".equals(code); }
    boolean send(JSONObject message) {
        boolean sent = !stopped && socket != null && socket.send(message.toString());
        if (!sent) listener.status("房间尚未连接，此操作未发送");
        return sent;
    }
    private void invalidateSocket(boolean leave) {
        if (leave && !stopped) {
            try { send(new JSONObject().put("type", "leave")); } catch (Exception ignored) { listener.status("退出消息未送达，云端将自动清理"); }
        }
        stopped = true; generation++;
        pendingWelcome = null;
        WebSocket previous = socket; socket = null;
        if (previous != null) {
            if (leave) {
                previous.close(1000, "client leaving");
                main.postDelayed(() -> network.cancel(previous::cancel), 1000);
            } else network.cancel(previous::cancel);
        }
    }
    void stop(boolean leave) {
        invalidateSocket(leave); reconnecting = false;
        if (progress != null) { progress = progress.cancel(monotonicMillis()); emitProgress(); }
    }
    void destroy() {
        stop(false);
        network.close();
    }
}
