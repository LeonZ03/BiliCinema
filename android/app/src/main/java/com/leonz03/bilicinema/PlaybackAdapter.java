package com.leonz03.bilicinema;

import android.view.View;

/** A device player reports observations; MediaSession and the room retain timeline authority. */
interface PlaybackAdapter extends AutoCloseable {
    enum State { IDLE, BUFFERING, READY, ENDED }
    interface Listener {
        void onState(long generation, State state);
        void onProgress(long generation, double position, double duration, boolean playing);
        void onUserIntent(long generation, double position, double rate, boolean playing);
        void onError(long generation, PlaybackFailure failure);
        default void onQuality(long generation, int quality) { }
        default void onVideoAspectRatio(long generation, float ratio) { }
    }
    View view();
    void prepare(PlaybackSource source, long generation);
    /** Retain remote intent even before media is ready; never seek or emit a user command. */
    void requestPlayback(boolean playing);
    void apply(double position, double rate, boolean playing, boolean force);
    void play();
    void pause();
    void seek(double position);
    void setRate(double rate);
    void setForeground(boolean active);
    @Override void close();
}
