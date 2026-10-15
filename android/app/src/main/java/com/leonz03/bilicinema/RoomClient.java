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
    interface Listener { void message(JSONObject message); void status(String message); void credentials(String room, String client); }
    static final String ENDPOINT = "wss://bilicinema.leonz03.dpdns.org/v2/ws";
    private final Handler main = new Handler(Looper.getMainLooper());
    private final OkHttpClient http = new OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS).readTimeout(0, TimeUnit.SECONDS).pingInterval(30, TimeUnit.SECONDS).build();
    private final Listener listener;
    private WebSocket socket;
    private String room = "", client = "";
    private boolean stopped = true;
    private boolean reconnecting;
    private int generation, attempt, welcomedGeneration = -1;
    private long reconnectDeadline;
    RoomClient(Listener listener) { this.listener = listener; }
    void connect(String code, String credential) {
        stop(false);
        room = code; client = credential; stopped = false; attempt = 0; reconnecting = false;
        reconnectDeadline = System.currentTimeMillis() + 110_000;
        open(++generation);
    }
    private void open(int current) {
        if (stopped || current != generation) return;
        listener.status(attempt == 0 ? "正在连接云端房间…" : "连接已断开，正在重连（" + attempt + "）…");
        String url = ENDPOINT + (room.isEmpty() ? "" : "?room=" + room);
        try {
            socket = http.newWebSocket(new Request.Builder().url(url).build(), new WebSocketListener() {
                @Override public void onOpen(WebSocket ws, Response response) {
                    main.post(() -> {
                    if (stopped || current != generation) { ws.close(1000, "superseded"); return; }
                    try {
                        JSONObject first = new JSONObject().put("type", room.isEmpty() ? "create" : "join");
                        if (!room.isEmpty()) first.put("roomCode", room);
                        if (!client.isEmpty()) first.put("clientId", client);
                        ws.send(first.toString());
                    } catch (Exception error) { ws.close(1000, "invalid request"); }
                    });
                }
                @Override public void onMessage(WebSocket ws, String text) {
                    main.post(() -> {
                        if (stopped || current != generation) return;
                        try {
                            JSONObject message = new JSONObject(text);
                            String type = message.optString("type");
                            if ("welcome".equals(type)) {
                                room = message.getString("roomCode"); client = message.getString("clientId");
                                attempt = 0; reconnecting = false; welcomedGeneration = current;
                                listener.credentials(room, client);
                                listener.status("房间已连接");
                                ping(current);
                            }
                            if ("closed".equals(type) || "left".equals(type)) stop(false);
                            if ("error".equals(type) && terminal(message.optString("code"))) stop(false);
                            listener.message(message);
                        } catch (Exception error) { listener.status("收到无法识别的房间消息，请重连"); }
                    });
                }
                @Override public void onFailure(WebSocket ws, Throwable error, Response response) {
                    if (response != null && (response.code() == 404 || response.code() == 403)) {
                        main.post(() -> { if (stopped || current != generation) return; stop(false); try { listener.message(new JSONObject().put("type", "error").put("code", "room_unavailable")); } catch (Exception invalid) { listener.status("房间已结束或凭据失效，请重新加入"); } });
                    } else lost(current);
                }
                @Override public void onClosed(WebSocket ws, int code, String reason) { lost(current); }
                @Override public void onClosing(WebSocket ws, int code, String reason) { ws.close(code, reason); }
            });
            main.postDelayed(() -> {
                if (stopped || current != generation || welcomedGeneration == current) return;
                if (socket != null) socket.cancel();
                lost(current);
            }, 20_000);
        } catch (RuntimeException error) { lost(current); }
    }
    private void lost(int current) {
        main.post(() -> {
            if (stopped || current != generation) return;
            try { listener.message(new JSONObject().put("type", "transportLost")); } catch (Exception error) { listener.status("连接已断开"); }
            if (!reconnecting) { reconnecting = true; reconnectDeadline = System.currentTimeMillis() + 110_000; }
            if (room.isEmpty() || client.isEmpty() || System.currentTimeMillis() >= reconnectDeadline) {
                stopped = true; listener.status("房间连接失败或重连窗口已结束，请检查网络后重新加入");
                try { listener.message(new JSONObject().put("type", "reconnectEnded")); } catch (Exception error) { listener.status("请重新加入房间"); }
                return;
            }
            attempt++;
            int next = ++generation;
            main.postDelayed(() -> open(next), Math.min(10_000, 1000L << Math.min(attempt, 3)));
        });
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
    void stop(boolean leave) {
        if (leave && !stopped) {
            try { send(new JSONObject().put("type", "leave")); } catch (Exception ignored) { listener.status("退出消息未送达，云端将自动清理"); }
        }
        stopped = true; generation++;
        if (socket != null) { socket.close(1000, "client leaving"); socket = null; }
    }
    void destroy() { stop(false); http.dispatcher().executorService().shutdown(); http.connectionPool().evictAll(); }
}
