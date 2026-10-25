package com.leonz03.bilicinema;

import org.json.JSONObject;

/** Single owner of expected media, native source binding and published preparation state. UI thread only. */
final class MediaSession {
    enum State { EMPTY, RESOLVING, LOADING, READY, BUFFERING, SUSPENDED, ENDED, ERROR }
    enum SyncState { NONE, ALIGNED, CATCHING_UP, STALLED }
    private State state = State.EMPTY;
    private SyncState syncState = SyncState.NONE;
    private long mismatchSince = -1;
    private boolean observedRoomPlaying;
    private JSONObject selected, resolvedMedia;
    private long generation, selectionRequest;
    private boolean bound, foreground = true;
    private InitialAlignment initialAlignment;
    private PlaybackSource source;

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
        selected = copy(media); resolvedMedia = null; source = null;
        generation++; selectionRequest++; bound = false; state = State.RESOLVING;
        clearRoomSync();
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
    boolean bind(long token, PlaybackSource value) {
        if (value == null || !bind(token, value.media())) return false;
        source = value;
        return true;
    }
    PlaybackSource source() { return source; }
    boolean updateQuality(long token, int quality) {
        if (!current(token) || !bound || source == null || quality <= 0 || source.quality == quality) return false;
        try { source = source.withQuality(quality); return true; }
        catch (PlaybackFailure error) { throw new IllegalStateException("Invalid bound playback source", error); }
    }
    boolean prepared(long token) {
        if (!current(token) || !bound) return false;
        state = foreground ? State.READY : State.SUSPENDED;
        return true;
    }
    boolean buffering(long token) {
        if (!current(token) || !bound) return false;
        state = foreground ? State.BUFFERING : State.SUSPENDED;
        clearRoomSync();
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
        clearRoomSync();
        return true;
    }
    void setForeground(boolean value) {
        foreground = value;
        if (!value) clearRoomSync();
        if (state == State.EMPTY || state == State.ERROR || state == State.ENDED) return;
        if (!value && bound) state = State.SUSPENDED;
        else if (value && state == State.SUSPENDED) state = State.LOADING;
    }
    void suspend() { if (bound && state != State.ERROR && state != State.ENDED) state = State.SUSPENDED; clearRoomSync(); }
    void reset() {
        generation++; selectionRequest++; selected = null; resolvedMedia = null; initialAlignment = null; source = null; bound = false; state = State.EMPTY;
        clearRoomSync();
    }
    /** Compare observed playback with the authoritative room target; READY alone is not synchronized. */
    SyncState observeRoomPlayback(boolean expectedPlaying, boolean actualPlaying,
            double targetPosition, double actualPosition, long nowMs) {
        if (!ready() || !foreground || !Double.isFinite(targetPosition) || !Double.isFinite(actualPosition)) {
            clearRoomSync(); return syncState;
        }
        if (expectedPlaying == actualPlaying && Math.abs(targetPosition - actualPosition) <= 1.5) {
            mismatchSince = -1; syncState = SyncState.ALIGNED;
        } else {
            if (mismatchSince < 0 || observedRoomPlaying != expectedPlaying) mismatchSince = nowMs;
            syncState = nowMs - mismatchSince >= 10_000 ? SyncState.STALLED : SyncState.CATCHING_UP;
        }
        observedRoomPlaying = expectedPlaying;
        return syncState;
    }
    SyncState syncState() { return syncState; }
    void clearRoomSync() { mismatchSince = -1; syncState = SyncState.NONE; }
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
    /** Playback may be needed to finish loading. Only timeline alignment waits for readiness. */
    boolean followRoom(PlaybackAdapter player, double position, double rate, boolean playing, boolean force) {
        if (!bound || !foreground || state == State.ERROR) return false;
        if (ready()) {
            player.apply(position, rate, playing, force);
            return true;
        }
        if (acceptsPausedTimeline(playing)) player.apply(position, rate, false, false);
        else player.requestPlayback(playing);
        return false;
    }
    /** An ended source needs a paused reposition before it can become ready for a playing room. */
    boolean acceptsPausedTimeline(boolean roomPlaying) { return bound && (!roomPlaying || state == State.ENDED); }
    boolean buffering() { return state == State.RESOLVING || state == State.LOADING || state == State.BUFFERING || state == State.SUSPENDED; }
}
