package com.leonz03.bilicinema;

/** Sole owner of requested playback and recoverable system blocks. No Android dependencies. */
final class PlaybackIntentState {
    private enum Block { NONE, TRANSIENT_PENDING, TRANSIENT_REPORTED, USER_ACTION_PENDING, USER_ACTION_REPORTED }
    private Block block = Block.NONE;
    private boolean requestedPlaying, foreground = true;

    void prepare() { requestedPlaying = false; block = Block.NONE; }
    void remote(boolean playing) {
        requestedPlaying = playing;
        // A pause before the grace period is an ordinary pause. A reported block stays latched.
        if (!playing && block == Block.TRANSIENT_PENDING) block = Block.NONE;
    }
    boolean localPlayback(boolean playing) {
        boolean recovered = block != Block.NONE;
        requestedPlaying = playing;
        block = Block.NONE;
        return recovered;
    }
    void ended() { requestedPlaying = false; }
    void transientSuppression() {
        if (foreground && requestedPlaying && block == Block.NONE) block = Block.TRANSIENT_PENDING;
    }
    void systemPause() {
        if (foreground && (requestedPlaying || reported())) block = reported() ? Block.USER_ACTION_REPORTED : Block.USER_ACTION_PENDING;
    }
    /** Call only for actual native suppression recovery/playing, never for a remote pause. */
    boolean nativeUnsuppressed() {
        if (block != Block.TRANSIENT_PENDING && block != Block.TRANSIENT_REPORTED) return false;
        block = Block.NONE;
        return true;
    }
    boolean reportBlocked() {
        if (!needsReport()) return false;
        block = block == Block.TRANSIENT_PENDING ? Block.TRANSIENT_REPORTED : Block.USER_ACTION_REPORTED;
        return true;
    }
    boolean needsReport() {
        return foreground && requestedPlaying && (block == Block.TRANSIENT_PENDING || block == Block.USER_ACTION_PENDING);
    }
    boolean reported() { return block == Block.TRANSIENT_REPORTED || block == Block.USER_ACTION_REPORTED; }
    boolean canPublishReady() { return block == Block.NONE; }
    boolean requestedPlaying() { return requestedPlaying; }
    boolean foreground() { return foreground; }
    boolean playWhenReady() {
        if (!foreground || block == Block.USER_ACTION_PENDING || block == Block.USER_ACTION_REPORTED) return false;
        // Keep transient focus observation alive while the room waits for our ready state. On real
        // focus recovery, nativeUnsuppressed removes this hold and the latest remote pause applies.
        return requestedPlaying || block == Block.TRANSIENT_REPORTED;
    }
    void setForeground(boolean active) {
        foreground = active;
        // Background pause ends native focus observation, so it cannot prove a prior block recovered.
        if (!active && block == Block.TRANSIENT_REPORTED) block = Block.USER_ACTION_REPORTED;
        else if (!active && block == Block.TRANSIENT_PENDING) block = Block.NONE;
    }
    static double boundedRate(double value) {
        return Double.isFinite(value) ? Math.max(.25, Math.min(3, value)) : 1;
    }
}
