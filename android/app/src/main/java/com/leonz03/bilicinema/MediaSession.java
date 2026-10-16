package com.leonz03.bilicinema;

import org.json.JSONObject;

/** Single owner of expected media, native source binding and published preparation state. UI thread only. */
final class MediaSession {
    enum State { EMPTY, RESOLVING, LOADING, READY, BUFFERING, SUSPENDED, ENDED, ERROR }
    private State state = State.EMPTY;
    private JSONObject selected, resolvedMedia;
    private long generation, selectionRequest;
    private boolean bound, foreground = true;
    private InitialAlignment initialAlignment;

    static final class InitialAlignment {
        final double position, rate;
        InitialAlignment(double position, double rate) { this.position = position; this.rate = rate; }
    }

    long requestSelection() { return ++selectionRequest; }
    boolean currentSelectionRequest(long request) { return request == selectionRequest; }
    long begin(JSONObject media) {
        return begin(media, 0, 1, false);
    }
    long begin(JSONObject media, double position, double rate, boolean alignInitially) {
        if (media == null) throw new IllegalArgumentException("A selected media identity is required.");
        selected = copy(media); resolvedMedia = null;
        generation++; selectionRequest++; bound = false; state = State.RESOLVING;
        initialAlignment = alignInitially ? new InitialAlignment(position, rate) : null;
        return generation;
    }
    boolean queueInitialSeek(long token, double position) {
        if (!current(token) || initialAlignment == null || !Double.isFinite(position) || position < 0 || position > 86400) return false;
        initialAlignment = new InitialAlignment(position, initialAlignment.rate);
        return true;
    }
    boolean queueInitialRate(long token, double rate) {
        if (!current(token) || initialAlignment == null || !Double.isFinite(rate) || rate < .25 || rate > 3) return false;
        initialAlignment = new InitialAlignment(initialAlignment.position, rate);
        return true;
    }
    InitialAlignment takeInitialAlignment(long token) {
        if (!current(token) || !bound || state != State.READY) return null;
        InitialAlignment result = initialAlignment;
        initialAlignment = null;
        return result;
    }
    boolean awaitingInitialAlignment() { return initialAlignment != null; }
    boolean resolved(long token, BilibiliMediaResolver.Resolved result) {
        if (!current(token) || state != State.RESOLVING) return false;
        if (result == null || !complete(result.media) || !matches(selected, result.media)) { fail(token); return false; }
        resolvedMedia = copy(result.media); state = State.LOADING;
        return true;
    }
    /** READY is trusted only after the exact resolved source has been bound. */
    boolean bind(long token, JSONObject sourceMedia) {
        if (!current(token) || state != State.LOADING || resolvedMedia == null) return false;
        if (!complete(sourceMedia) || !MediaIdentity.key(resolvedMedia).equals(MediaIdentity.key(sourceMedia))) { fail(token); return false; }
        bound = true;
        return true;
    }
    boolean prepared(long token) {
        if (!current(token) || !bound) return false;
        state = foreground ? State.READY : State.SUSPENDED;
        return true;
    }
    boolean buffering(long token) {
        if (!current(token) || !bound) return false;
        state = foreground ? State.BUFFERING : State.SUSPENDED;
        return true;
    }
    boolean ended(long token) {
        if (!current(token) || !bound) return false;
        state = State.ENDED;
        return true;
    }
    boolean fail(long token) {
        if (!current(token)) return false;
        state = State.ERROR;
        return true;
    }
    void setForeground(boolean value) {
        foreground = value;
        if (state == State.EMPTY || state == State.ERROR || state == State.ENDED) return;
        if (!value && bound) state = State.SUSPENDED;
        else if (value && state == State.SUSPENDED) state = State.LOADING;
    }
    void suspend() { if (bound && state != State.ERROR && state != State.ENDED) state = State.SUSPENDED; }
    void reset() {
        generation++; selectionRequest++; selected = null; resolvedMedia = null; initialAlignment = null; bound = false; state = State.EMPTY;
    }
    private boolean current(long token) { return token == generation && selected != null; }
    private static boolean complete(JSONObject media) {
        return media != null && media.optLong("aid") > 0 && media.optLong("cid") > 0
            && media.optString("bvid").matches("BV[0-9A-Za-z]{10}")
            && (!media.has("episodeId") || media.optLong("episodeId") > 0);
    }
    private static boolean matches(JSONObject expected, JSONObject actual) {
        for (String field : new String[]{"episodeId", "aid", "bvid", "cid"})
            if (expected.has(field) && !String.valueOf(expected.opt(field)).equals(String.valueOf(actual.opt(field)))) return false;
        return true;
    }
    private static JSONObject copy(JSONObject value) {
        if (value == null) return null;
        try { return new JSONObject(value.toString()); }
        catch (Exception error) { throw new IllegalArgumentException("Invalid public media identity.", error); }
    }
    JSONObject selected() { return copy(selected); }
    String key() { return MediaIdentity.key(selected); }
    long generation() { return generation; }
    State state() { return state; }
    boolean ready() { return state == State.READY; }
    boolean bound() { return bound; }
    /** An ended source needs a paused reposition before it can become ready for a playing room. */
    boolean acceptsPausedTimeline(boolean roomPlaying) { return bound && (!roomPlaying || state == State.ENDED); }
    boolean buffering() { return state == State.RESOLVING || state == State.LOADING || state == State.BUFFERING || state == State.SUSPENDED; }
}
