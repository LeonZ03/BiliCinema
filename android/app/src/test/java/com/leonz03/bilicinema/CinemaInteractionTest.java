package com.leonz03.bilicinema;

import org.junit.Test;
import static org.junit.Assert.*;

public class CinemaInteractionTest {
    @Test public void chatTapExcludesEdgesControlsAndDrags() {
        assertTrue(tap(850, 250, 0));
        assertFalse(tap(700, 250, 0));
        assertFalse(tap(990, 250, 0));
        assertFalse(tap(850, 20, 0));
        assertFalse(tap(850, 460, 0));
        assertFalse(tap(850, 250, 30));
    }
    private boolean tap(float x, float y, float movement) {
        return CinemaInteraction.opensChat(x, y, 1000, 500, 24, 64, 64, movement, 8);
    }
    @Test public void backDismissesOneLayerAtATime() {
        assertEquals(CinemaInteraction.Back.KEYBOARD, CinemaInteraction.back(true, true, true));
        assertEquals(CinemaInteraction.Back.DRAWER, CinemaInteraction.back(false, true, true));
        assertEquals(CinemaInteraction.Back.FULLSCREEN, CinemaInteraction.back(false, false, true));
        assertEquals(CinemaInteraction.Back.NONE, CinemaInteraction.back(false, false, false));
    }
    @Test public void previewsAreBoundedAndSuppressOwnOrVisibleChat() {
        CinemaInteraction.Previews previews = new CinemaInteraction.Previews(2);
        previews.offer("a", "one", false, false);
        previews.offer("b", "two", false, false);
        previews.offer("c", "three", false, false);
        previews.offer("self", "own", true, false);
        previews.offer("seen", "visible", false, true);
        assertEquals(2, previews.size());
        assertEquals("b", previews.poll().nickname);
        assertEquals("three", previews.poll().text);
        previews.clear();
        assertNull(previews.poll());
    }
    @Test public void previewTruncationPreservesUnicodeAndNormalizesNewlines() {
        assertEquals("名字 😀…", CinemaInteraction.excerpt("名字 😀你好", 4));
        assertEquals("你好 世界", CinemaInteraction.excerpt("你好\n世界", 10));
    }
}
