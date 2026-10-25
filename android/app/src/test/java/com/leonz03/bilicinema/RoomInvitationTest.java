package com.leonz03.bilicinema;

import org.junit.Test;
import static org.junit.Assert.*;

public final class RoomInvitationTest {
    private static final String CODE = "lKEQymvry3BhozIFB8q35w";
    @Test public void acceptsCopiedShareWithoutTreatingTitleAsUrl() {
        assertEquals(CODE, RoomInvitation.code("【加入我的房间】 wss://bilicinema.leonz03.dpdns.org/v2/ws?room=" + CODE + " 一起观看"));
        assertEquals(CODE, RoomInvitation.code("  " + CODE + "\n"));
    }
    @Test public void rejectsPartialCodesAndUnrelatedUrls() {
        assertNull(RoomInvitation.code("wss://example.com/v2/ws?room=" + CODE));
        assertNull(RoomInvitation.code("wss://bilicinema.leonz03.dpdns.org/v2/ws?room=" + CODE + "x"));
        assertNull(RoomInvitation.code("一段文字"));
    }
}
