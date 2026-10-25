package com.leonz03.bilicinema;

import org.junit.Test;
import static org.junit.Assert.*;

public class PlaybackFailureTest {
    @Test public void mediaServerRejectionDoesNotClaimThatAFreshAddressExpired() {
        for (int status : new int[]{401, 403}) {
            PlaybackFailure failure = PlaybackFailure.fromMediaHttpStatus(status);
            assertEquals(PlaybackFailure.Reason.SOURCE_REJECTED, failure.reason);
            assertFalse(failure.refreshRequired());
            assertFalse(failure.safeMessage().contains("已失效"));
        }
    }

    @Test public void missingOrGoneMediaStillRequestsAFreshAddress() {
        for (int status : new int[]{404, 410}) {
            PlaybackFailure failure = PlaybackFailure.fromMediaHttpStatus(status);
            assertEquals(PlaybackFailure.Reason.EXPIRED_SOURCE, failure.reason);
            assertTrue(failure.refreshRequired());
        }
        assertEquals(PlaybackFailure.Reason.NETWORK_RETRYABLE, PlaybackFailure.fromMediaHttpStatus(503).reason);
    }
}
