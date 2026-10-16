package com.leonz03.bilicinema;

import java.util.ArrayDeque;

/** Presentation-only rules shared by the video surface and transient chat preview. */
final class CinemaInteraction {
    private CinemaInteraction() { }

    static boolean opensChat(float x, float y, float width, float height, float edge,
                             float controlsTop, float controlsBottom, float movement, float slop) {
        return width > 0 && height > 0 && movement <= slop
                && x >= width * .75f && x < width - edge
                && y >= Math.max(edge, controlsTop) && y < height - Math.max(edge, controlsBottom);
    }

    enum Back { KEYBOARD, DRAWER, FULLSCREEN, NONE }

    static Back back(boolean keyboard, boolean drawer, boolean fullscreen) {
        if (keyboard) return Back.KEYBOARD;
        if (drawer) return Back.DRAWER;
        return fullscreen ? Back.FULLSCREEN : Back.NONE;
    }

    static String excerpt(String value, int maximumCodePoints) {
        String clean = value == null ? "" : value.replaceAll("[\\r\\n\\t]+", " ").trim();
        int count = clean.codePointCount(0, clean.length());
        return count <= maximumCodePoints ? clean
                : clean.substring(0, clean.offsetByCodePoints(0, maximumCodePoints)) + "…";
    }

    static final class Preview {
        final String nickname;
        final String text;
        final String accessibilityText;
        Preview(String nickname, String text) {
            this.nickname = excerpt(nickname, 24);
            this.text = excerpt(text, 72);
            this.accessibilityText = (nickname == null ? "" : nickname) + "说：" + (text == null ? "" : text);
        }
    }

    static final class Previews {
        private final int capacity;
        private final ArrayDeque<Preview> pending = new ArrayDeque<>();
        Previews(int capacity) {
            if (capacity < 1) throw new IllegalArgumentException("Preview capacity must be positive");
            this.capacity = capacity;
        }
        void offer(String nickname, String text, boolean own, boolean chatVisible) {
            if (own || chatVisible) return;
            while (pending.size() >= capacity) pending.removeFirst();
            pending.addLast(new Preview(nickname, text));
        }
        Preview poll() { return pending.pollFirst(); }
        void clear() { pending.clear(); }
        int size() { return pending.size(); }
    }
}
