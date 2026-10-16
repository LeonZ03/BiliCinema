package com.leonz03.bilicinema;

import androidx.media3.common.Player;
import org.junit.Test;
import static org.junit.Assert.*;

public class NativePlaybackAdapterTest {
    @Test public void endedShortSourceMustActuallySeekToReplay() {
        assertTrue(NativePlaybackAdapter.needsSeek(Player.STATE_ENDED, .8, 0, false));
    }
    @Test public void endedGuestCanFollowNearbyBackwardSeekWithoutTerminalLoop() {
        assertTrue(NativePlaybackAdapter.needsSeek(Player.STATE_ENDED, 120, 119.5, false));
        assertFalse(NativePlaybackAdapter.needsSeek(Player.STATE_ENDED, 120, 120, false));
    }
    @Test public void activePlaybackKeepsNormalDriftToleranceAndExplicitAlignment() {
        assertFalse(NativePlaybackAdapter.needsSeek(Player.STATE_READY, 120, 119.5, false));
        assertTrue(NativePlaybackAdapter.needsSeek(Player.STATE_READY, 120, 119.5, true));
        assertTrue(NativePlaybackAdapter.needsSeek(Player.STATE_READY, 120, 118, false));
    }
}
