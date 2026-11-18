package com.leonz03.bilicinema;

import java.util.ArrayDeque;
import org.json.JSONException;
import org.json.JSONObject;

/** Bounded UI replay of one live socket, never a second room timeline or a reconnect owner. */
final class RoomSessionReplay {
    private JSONObject welcome;
    private boolean connected;
    private final ArrayDeque<JSONObject> chat = new ArrayDeque<>();

    boolean active() { return welcome != null; }
    void clear() { welcome = null; connected = false; chat.clear(); }

    void record(JSONObject value) {
        switch (value.optString("type")) {
            case "welcome": welcome = value; connected = true; break;
            case "snapshot":
                if (welcome != null && RoomClient.validSnapshot(value.optJSONObject("snapshot"))) {
                    try { welcome.put("snapshot", value.getJSONObject("snapshot")); }
                    catch (JSONException error) { throw new IllegalArgumentException("Invalid room snapshot", error); }
                }
                break;
            case "transportLost": connected = false; break;
            case "chat":
                if (welcome != null) {
                    while (chat.size() >= 150) chat.removeFirst();
                    chat.addLast(value);
                }
                break;
            case "closed": case "left": clear(); break;
            case "error": if (RoomClient.terminal(value.optString("code"))) clear(); break;
            default: break;
        }
    }

    void deliver(RoomClient.Listener listener) {
        if (welcome == null) return;
        listener.credentials(welcome.optString("roomCode"), welcome.optString("clientId"));
        if (!connected) {
            try { listener.message(new JSONObject().put("type", "transportLost")); }
            catch (JSONException error) { throw new IllegalStateException("Cannot replay room state", error); }
            return;
        }
        listener.message(welcome);
        for (JSONObject message : chat) listener.message(message);
    }
}
