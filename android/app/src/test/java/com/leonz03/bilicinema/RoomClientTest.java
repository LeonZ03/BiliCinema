package com.leonz03.bilicinema;
import org.junit.Test;
import org.json.JSONObject;
import org.json.JSONArray;
import static org.junit.Assert.*;

public class RoomClientTest {
    @Test public void expiredRoomAndReclaimedCredentialAreTerminal() { assertTrue(RoomClient.terminal("room_unavailable")); assertTrue(RoomClient.terminal("invalid_client")); assertFalse(RoomClient.terminal("rate_limited")); }
    @Test public void welcomeRequiresActualRoomAndMemberCredentials() throws Exception {
        JSONObject message = new JSONObject().put("roomCode", "a".repeat(22)).put("clientId", "b".repeat(22))
                .put("memberId", "c".repeat(22)).put("role", "host");
        assertTrue(RoomClient.validWelcome(message));
        message.remove("memberId"); assertFalse(RoomClient.validWelcome(message));
    }
    @Test public void missingRoomSnapshotCannotCompleteConnection() throws Exception {
        assertFalse(RoomClient.validSnapshot(null));
        assertFalse(RoomClient.validSnapshot(new JSONObject().put("version", 1)));
        JSONObject snapshot = new JSONObject().put("version", 0).put("syncRevision", 0)
                .put("host", new JSONObject()).put("guests", new JSONArray()).put("media", JSONObject.NULL)
                .put("playing", false).put("positionSeconds", 0).put("rate", 1);
        assertTrue(RoomClient.validSnapshot(snapshot));
        snapshot.put("rate", -1); assertFalse(RoomClient.validSnapshot(snapshot)); snapshot.put("rate", 1);
        snapshot.remove("media"); assertFalse(RoomClient.validSnapshot(snapshot));
    }
}
