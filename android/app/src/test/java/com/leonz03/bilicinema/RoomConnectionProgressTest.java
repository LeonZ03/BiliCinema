package com.leonz03.bilicinema;

import org.junit.Test;
import static org.junit.Assert.*;

public class RoomConnectionProgressTest {
    @Test public void socketOpenAndRequestSendDoNotClaimRoomCompletion() {
        RoomConnectionProgress progress = RoomConnectionProgress.start(RoomConnectionProgress.Operation.CREATE, 100);
        assertSame(progress, progress.advance(RoomConnectionProgress.Stage.COMPLETE, 110));
        progress = progress.advance(RoomConnectionProgress.Stage.SENDING_REQUEST, 120);
        progress = progress.advance(RoomConnectionProgress.Stage.WAITING_ROOM, 130);
        assertEquals(RoomConnectionProgress.Outcome.RUNNING, progress.outcome);
        assertSame(progress, progress.advance(RoomConnectionProgress.Stage.COMPLETE, 140));
        progress = progress.advance(RoomConnectionProgress.Stage.SYNCHRONIZING, 150);
        progress = progress.advance(RoomConnectionProgress.Stage.COMPLETE, 180);
        assertEquals(RoomConnectionProgress.Outcome.COMPLETE, progress.outcome);
        assertEquals(80, progress.elapsedMillis(900));
    }
    @Test public void failedSendRetainsStageAndCannotBeCompletedByLateEvents() {
        RoomConnectionProgress progress = RoomConnectionProgress.start(RoomConnectionProgress.Operation.JOIN, 100)
                .advance(RoomConnectionProgress.Stage.SENDING_REQUEST, 120).fail("发送失败", 140);
        assertEquals(RoomConnectionProgress.Stage.SENDING_REQUEST, progress.stage);
        assertEquals(RoomConnectionProgress.Outcome.FAILED, progress.outcome);
        assertEquals(40, progress.elapsedMillis(900));
        assertSame(progress, progress.advance(RoomConnectionProgress.Stage.WAITING_ROOM, 150));
        assertTrue(progress.needsResolution());
    }
    @Test public void cancelledRestoreCannotAdvanceAndNewAttemptStartsAtCloudConnection() {
        RoomConnectionProgress progress = RoomConnectionProgress.start(RoomConnectionProgress.Operation.RESTORE, 100).cancel(110);
        assertFalse(progress.needsResolution());
        assertSame(progress, progress.advance(RoomConnectionProgress.Stage.SENDING_REQUEST, 120));
        RoomConnectionProgress retry = RoomConnectionProgress.start(progress.operation, 200);
        assertEquals(RoomConnectionProgress.Stage.CONNECTING, retry.stage);
        assertEquals(RoomConnectionProgress.Operation.RESTORE, retry.operation);
        assertEquals(0, retry.elapsedMillis(200));
    }
}
