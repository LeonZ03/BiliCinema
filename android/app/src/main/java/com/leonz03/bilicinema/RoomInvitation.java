package com.leonz03.bilicinema;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** The room code is the invitation; surrounding share copy never becomes a server address. */
final class RoomInvitation {
    private static final Pattern LINK = Pattern.compile("(?:wss|https)://bilicinema\\.leonz03\\.dpdns\\.org/[^\\s<>\\\"【】]*?[?&]room=([A-Za-z0-9_-]{22})(?![A-Za-z0-9_-])", Pattern.CASE_INSENSITIVE);
    private RoomInvitation() { }
    static String code(String input) {
        if (input == null) return null;
        String value = input.trim();
        if (value.matches("[A-Za-z0-9_-]{22}")) return value;
        Matcher match = LINK.matcher(value);
        return match.find() ? match.group(1) : null;
    }
}
