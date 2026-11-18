package com.leonz03.bilicinema;

import java.util.ArrayList;
import java.util.List;
import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public class RoomSessionReplayTest {
    @Test public void recreatedScreenReceivesCurrentSnapshotAndBoundedChatWithoutJoiningAgain() throws Exception {
        RoomSessionReplay replay = new RoomSessionReplay();
        replay.record(new JSONObject().put("type", "welcome").put("roomCode", "room").put("clientId", "private")
                .put("snapshot", snapshot(1)));
        replay.record(new JSONObject().put("type", "snapshot").put("snapshot", snapshot(8)));
        for (int i = 0; i < 160; i++) replay.record(new JSONObject().put("type", "chat").put("text", "message " + i));
        Observer observer = new Observer(); replay.deliver(observer);
        assertEquals(151, observer.messages.size());
        assertEquals(8, observer.messages.get(0).getJSONObject("snapshot").getLong("version"));
        assertEquals("message 10", observer.messages.get(1).getString("text"));
        assertEquals("message 159", observer.messages.get(150).getString("text"));
        assertEquals(1, observer.credentials);
        assertEquals("room", observer.room);
        assertEquals("credentials", observer.events.get(0));
        assertEquals("welcome", observer.events.get(1));
    }

    @Test public void detachedScreenDoesNotMistakeAReconnectingSocketForAConnectedRoom() throws Exception {
        RoomSessionReplay replay = new RoomSessionReplay();
        replay.record(new JSONObject().put("type", "welcome").put("roomCode", "room").put("clientId", "private"));
        replay.record(new JSONObject().put("type", "transportLost"));
        Observer observer = new Observer(); replay.deliver(observer);
        assertEquals(1, observer.credentials);
        assertEquals(1, observer.messages.size());
        assertEquals("transportLost", observer.messages.get(0).getString("type"));
        replay.record(new JSONObject().put("type", "closed"));
        assertFalse(replay.active());
        Observer afterClose = new Observer(); replay.deliver(afterClose);
        assertTrue(afterClose.messages.isEmpty());
    }

    private static JSONObject snapshot(long version) throws Exception {
        return new JSONObject().put("version", version).put("syncRevision", 0).put("host", new JSONObject())
                .put("guests", new org.json.JSONArray()).put("media", JSONObject.NULL).put("playing", false)
                .put("positionSeconds", 0).put("rate", 1);
    }

    private static final class Observer implements RoomClient.Listener {
        final List<JSONObject> messages = new ArrayList<>();
        final List<String> events = new ArrayList<>();
        String room = "";
        int credentials;
        public void message(JSONObject message) { events.add(message.optString("type")); messages.add(message); }
        public void status(String message) { }
        public void credentials(String room, String client) { credentials++; this.room = room; events.add("credentials"); }
    }
}
