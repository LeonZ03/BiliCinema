package com.leonz03.bilicinema;
import org.junit.Test;
import static org.junit.Assert.*;

public class RoomClientTest {
    @Test public void expiredRoomAndReclaimedCredentialAreTerminal() { assertTrue(RoomClient.terminal("room_unavailable")); assertTrue(RoomClient.terminal("invalid_client")); assertFalse(RoomClient.terminal("rate_limited")); }
}
