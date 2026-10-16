package com.leonz03.bilicinema;

import org.junit.Test;
import static org.junit.Assert.*;

public class PlaybackIntentStateTest {
    private PlaybackIntentState playing() {
        PlaybackIntentState state = new PlaybackIntentState();
        state.remote(true);
        return state;
    }
    private PlaybackIntentState transientBlock() {
        PlaybackIntentState state = playing();
        state.transientSuppression();
        assertTrue(state.reportBlocked());
        return state;
    }
    private PlaybackIntentState permanentBlock() {
        PlaybackIntentState state = playing();
        state.systemPause();
        assertTrue(state.reportBlocked());
        return state;
    }

    @Test public void remotePauseAfterReportedSuppressionCannotPublishReadyOrClearTheBlock() {
        PlaybackIntentState state = transientBlock();
        state.remote(false);
        assertTrue(state.reported());
        assertFalse(state.canPublishReady());
        assertFalse(state.requestedPlaying());
        // Preserve native transient-focus observation instead of manufacturing a recovery by pause.
        assertTrue(state.playWhenReady());
        assertFalse(state.needsReport());
        assertFalse(state.reportBlocked());
    }
    @Test public void realTransientRecoveryAppliesLatestRemotePauseBeforeReady() {
        PlaybackIntentState state = transientBlock();
        state.remote(false);
        assertTrue(state.nativeUnsuppressed());
        assertFalse(state.playWhenReady());
        assertTrue(state.canPublishReady());
        assertFalse(state.reported());
        assertFalse(state.nativeUnsuppressed());
    }
    @Test public void transientRecoveryWhileRequestedPlayingResumesWithoutUserIntent() {
        PlaybackIntentState state = transientBlock();
        assertTrue(state.nativeUnsuppressed());
        assertTrue(state.requestedPlaying());
        assertTrue(state.playWhenReady());
        assertTrue(state.canPublishReady());
    }
    @Test public void quickSuppressionRecoveryStillPublishesAnInitiallyWithheldReady() {
        PlaybackIntentState state = playing();
        state.transientSuppression();
        assertFalse(state.canPublishReady());
        assertFalse(state.reported());
        assertTrue(state.nativeUnsuppressed());
        assertTrue(state.canPublishReady());
        assertFalse(state.reportBlocked());
    }
    @Test public void remotePauseBeforeGracePeriodRemainsAnOrdinaryPause() {
        PlaybackIntentState state = playing();
        state.transientSuppression();
        state.remote(false);
        assertFalse(state.playWhenReady());
        assertTrue(state.canPublishReady());
        assertFalse(state.reportBlocked());
    }
    @Test public void permanentFocusLossAndHeadphoneRemovalRequireAnExplicitLocalAction() {
        PlaybackIntentState state = permanentBlock();
        state.remote(false); state.remote(true); state.remote(false); state.remote(true);
        assertFalse(state.nativeUnsuppressed());
        assertFalse(state.canPublishReady());
        assertFalse(state.playWhenReady());
        assertTrue(state.reported());
        assertTrue(state.localPlayback(true));
        assertTrue(state.canPublishReady());
        assertTrue(state.playWhenReady());
        assertFalse(state.reported());
    }
    @Test public void losingFocusPermanentlyWhileRoomWaitsDoesNotLookLikeTransientRecovery() {
        PlaybackIntentState state = transientBlock();
        state.remote(false);
        state.systemPause();
        assertFalse(state.nativeUnsuppressed());
        assertFalse(state.canPublishReady());
        assertFalse(state.playWhenReady());
        assertTrue(state.reported());
    }
    @Test public void explicitPauseCanAcknowledgeABlockWithoutRequestingPlayback() {
        PlaybackIntentState state = permanentBlock();
        assertTrue(state.localPlayback(false));
        assertFalse(state.playWhenReady());
        assertFalse(state.requestedPlaying());
        assertTrue(state.canPublishReady());
    }
    @Test public void deniedFocusAfterUserRetryLatchesAgainBeforeReadyCanBePublished() {
        PlaybackIntentState state = permanentBlock();
        state.localPlayback(true);
        state.systemPause();
        assertFalse(state.canPublishReady());
        assertFalse(state.playWhenReady());
        assertTrue(state.needsReport());
        assertTrue(state.reportBlocked());
    }
    @Test public void backgroundPauseCannotManufactureRecoveryOfAReportedBlock() {
        PlaybackIntentState state = transientBlock();
        state.setForeground(false);
        assertFalse(state.playWhenReady());
        assertFalse(state.nativeUnsuppressed());
        state.setForeground(true);
        assertFalse(state.canPublishReady());
        assertFalse(state.playWhenReady());
        assertTrue(state.reported());
        assertTrue(state.localPlayback(true));
        assertTrue(state.playWhenReady());
    }
    @Test public void foregroundRestorationRetainsTheLocalPlayingRequest() {
        PlaybackIntentState state = new PlaybackIntentState();
        state.localPlayback(true);
        state.setForeground(false);
        assertTrue(state.requestedPlaying());
        assertFalse(state.playWhenReady());
        state.setForeground(true);
        assertTrue(state.playWhenReady());
        assertTrue(state.canPublishReady());
        assertFalse(state.reported());
    }
    @Test public void foregroundRestorationDoesNotTurnAPausedRequestIntoPlayback() {
        PlaybackIntentState state = new PlaybackIntentState();
        state.localPlayback(false);
        state.setForeground(false); state.setForeground(true);
        assertFalse(state.requestedPlaying());
        assertFalse(state.playWhenReady());
        assertTrue(state.canPublishReady());
    }
    @Test public void remotePauseWhileBackgroundedOverridesTheRetainedPlayingRequest() {
        PlaybackIntentState state = playing();
        state.setForeground(false);
        state.remote(false);
        state.setForeground(true);
        assertFalse(state.playWhenReady());
        assertTrue(state.canPublishReady());
    }
    @Test public void newPreparationClearsOldBlocksAndRetainsBackgroundSafety() {
        PlaybackIntentState state = permanentBlock();
        state.setForeground(false); state.prepare();
        assertTrue(state.canPublishReady());
        assertFalse(state.reported());
        assertFalse(state.requestedPlaying());
        assertFalse(state.foreground());
        state.remote(true);
        assertFalse(state.playWhenReady());
    }
    @Test public void protocolRatesCoverTheFullQuarterToTripleSpeedRange() {
        assertEquals(.25, PlaybackIntentState.boundedRate(.25), 0);
        assertEquals(3, PlaybackIntentState.boundedRate(3), 0);
        assertEquals(.25, PlaybackIntentState.boundedRate(0), 0);
        assertEquals(3, PlaybackIntentState.boundedRate(4), 0);
        assertEquals(1, PlaybackIntentState.boundedRate(Double.NaN), 0);
        assertEquals(1, PlaybackIntentState.boundedRate(Double.POSITIVE_INFINITY), 0);
    }
}
