package com.leonz03.bilicinema;

import android.content.Context;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.HttpDataSource;
import androidx.media3.datasource.okhttp.OkHttpDataSource;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.source.MediaSource;
import androidx.media3.exoplayer.source.MergingMediaSource;
import androidx.media3.exoplayer.source.ProgressiveMediaSource;
import androidx.media3.ui.AspectRatioFrameLayout;
import androidx.media3.ui.PlayerView;
import java.util.concurrent.TimeUnit;
import okhttp3.CookieJar;
import okhttp3.OkHttpClient;

/** Main-thread Media3 owner. Only its explicit local control methods emit user intent. */
@UnstableApi
final class NativePlaybackAdapter implements AutoCloseable {
    enum State { IDLE, BUFFERING, READY, ENDED }
    interface Listener {
        void onState(long generation, State state);
        void onProgress(long generation, double position, double duration, boolean actuallyPlaying);
        void onUserIntent(long generation, double position, double rate, boolean playing);
        void onError(long generation, PlaybackFailure failure);
    }

    private final Context context;
    private final Listener listener;
    private final PlayerView view;
    private final Handler main = new Handler(Looper.getMainLooper());
    // A separate jar/client prevents API cookies (including a global CookieHandler) reaching a CDN.
    private final OkHttpClient mediaHttp = new OkHttpClient.Builder().cookieJar(CookieJar.NO_COOKIES)
            .connectTimeout(15, TimeUnit.SECONDS).readTimeout(20, TimeUnit.SECONDS)
            .followRedirects(true).followSslRedirects(false).build();
    private final HttpResourceLifecycle network = new HttpResourceLifecycle(mediaHttp);
    private ExoPlayer player;
    private PlaybackSource source;
    private long generation;
    private final PlaybackIntentState intent = new PlaybackIntentState();
    private boolean closed, failed, suppressionScheduled;
    private final Runnable progress = new Runnable() {
        @Override public void run() {
            if (closed || !intent.foreground() || player == null) return;
            publishProgress();
            main.postDelayed(this, 500);
        }
    };
    private final Runnable suppression = this::reportSuppression;
    private void reportSuppression() {
        suppressionScheduled = false;
        if (!closed && player != null && !failed && player.getPlaybackState() == Player.STATE_READY && intent.reportBlocked()) {
            listener.onError(generation, new PlaybackFailure(PlaybackFailure.Reason.PLAYBACK_BLOCKED));
            publishProgress();
        }
    }

    NativePlaybackAdapter(Context context, Listener listener) {
        requireMain();
        this.context = context.getApplicationContext();
        this.listener = listener;
        // Media3's internal exception logging may contain signed media URLs.
        androidx.media3.common.util.Log.setLogLevel(androidx.media3.common.util.Log.LOG_LEVEL_OFF);
        view = new PlayerView(context);
        view.setUseController(false);
        view.setResizeMode(AspectRatioFrameLayout.RESIZE_MODE_FIT);
        view.setKeepContentOnPlayerReset(false);
        view.setContentDescription("影片画面");
    }

    View view() { return view; }

    void prepare(PlaybackSource next, long nextGeneration) {
        requireMain();
        if (closed) return;
        releasePlayer();
        source = next;
        generation = nextGeneration;
        intent.prepare();
        failed = false;
        if (next == null) {
            listener.onError(generation, new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA));
            return;
        }
        final ExoPlayer current = new ExoPlayer.Builder(context).setLooper(Looper.getMainLooper()).build();
        final long preparedGeneration = generation;
        player = current;
        current.setAudioAttributes(new AudioAttributes.Builder().setUsage(C.USAGE_MEDIA)
                .setContentType(C.AUDIO_CONTENT_TYPE_MOVIE).build(), true);
        current.setHandleAudioBecomingNoisy(true);
        current.addListener(new Player.Listener() {
            private boolean active() { return !closed && player == current && generation == preparedGeneration; }
            @Override public void onPlaybackStateChanged(int state) {
                if (!active() || failed) return;
                if (state == Player.STATE_ENDED) intent.ended();
                publishState();
                updateSuppression();
                publishProgress();
            }
            @Override public void onIsPlayingChanged(boolean isPlaying) {
                if (!active() || failed) return;
                if (isPlaying) recoverNativeSuppression();
                updateSuppression();
                publishProgress();
            }
            @Override public void onPlaybackSuppressionReasonChanged(int reason) {
                if (!active() || failed) return;
                if (reason == Player.PLAYBACK_SUPPRESSION_REASON_NONE) recoverNativeSuppression();
                else intent.transientSuppression();
                updateSuppression();
            }
            @Override public void onPlayWhenReadyChanged(boolean playWhenReady, int reason) {
                if (!active() || failed) return;
                if (!playWhenReady && (reason == Player.PLAY_WHEN_READY_CHANGE_REASON_AUDIO_FOCUS_LOSS
                        || reason == Player.PLAY_WHEN_READY_CHANGE_REASON_AUDIO_BECOMING_NOISY)) intent.systemPause();
                updateSuppression();
            }
            @Override public void onPlayerError(PlaybackException error) {
                if (!active()) return;
                failed = true;
                main.removeCallbacks(suppression);
                listener.onError(generation, failure(error));
                publishProgress();
            }
        });
        view.setPlayer(current);
        try {
            OkHttpDataSource.Factory data = new OkHttpDataSource.Factory(mediaHttp).setDefaultRequestProperties(next.headers);
            ProgressiveMediaSource.Factory factory = new ProgressiveMediaSource.Factory(data);
            MediaSource video = factory.createMediaSource(MediaItem.fromUri(Uri.parse(next.videoUri.toString())));
            MediaSource selected = next.audioUri == null ? video : new MergingMediaSource(video,
                    factory.createMediaSource(MediaItem.fromUri(Uri.parse(next.audioUri.toString()))));
            current.setMediaSource(selected);
            listener.onState(generation, State.BUFFERING);
            current.prepare();
            startProgress();
        } catch (RuntimeException exception) {
            failed = true;
            listener.onError(generation, new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED));
            releasePlayer();
        }
    }

    /** Room state application deliberately never emits onUserIntent. */
    void apply(double position, double rate, boolean playing, boolean force) {
        requireMain();
        if (closed || player == null || failed) return;
        intent.remote(playing);
        double target = boundedPosition(position);
        if (needsSeek(player.getPlaybackState(), positionSeconds(), target, force)) player.seekTo((long) (target * 1000));
        player.setPlaybackSpeed((float) PlaybackIntentState.boundedRate(rate));
        enforcePlayerIntent();
        updateSuppression();
    }

    void play() {
        requireMain();
        if (!usable()) return;
        if (player.getPlaybackState() == Player.STATE_ENDED) player.seekTo(0);
        boolean recovered = intent.localPlayback(true);
        enforcePlayerIntent();
        publishIntent();
        if (recovered && intent.canPublishReady()) publishState();
        updateSuppression();
    }

    void pause() {
        requireMain();
        if (!usable()) return;
        boolean recovered = intent.localPlayback(false);
        enforcePlayerIntent();
        publishIntent();
        if (recovered && intent.canPublishReady()) publishState();
        updateSuppression();
    }

    void seek(double position) {
        requireMain();
        if (!usable()) return;
        player.seekTo((long) (boundedPosition(position) * 1000));
        publishIntent();
    }

    void setRate(double rate) {
        requireMain();
        if (!usable()) return;
        player.setPlaybackSpeed((float) PlaybackIntentState.boundedRate(rate));
        publishIntent();
    }

    void setForeground(boolean active) {
        requireMain();
        if (closed) return;
        intent.setForeground(active);
        main.removeCallbacks(progress);
        main.removeCallbacks(suppression);
        suppressionScheduled = false;
        if (player == null) return;
        if (!active) {
            player.pause();
            publishProgress();
            return;
        }
        // Restore the retained local request before reporting actual state. The room owner can then
        // override it with its latest snapshot; system blocks remain governed by the intent owner.
        if (!failed) enforcePlayerIntent();
        publishState();
        publishProgress();
        startProgress();
        updateSuppression();
    }

    private boolean usable() { return !closed && intent.foreground() && player != null && !failed; }
    static boolean needsSeek(int state, double position, double target, boolean force) {
        // Leaving the terminal state needs a real seek even within the normal drift tolerance.
        return force || Math.abs(position - target) > 1.25 || (state == Player.STATE_ENDED && target < position);
    }
    private void publishIntent() {
        listener.onUserIntent(generation, positionSeconds(), player.getPlaybackParameters().speed, intent.requestedPlaying());
        publishProgress();
    }
    private void startProgress() {
        main.removeCallbacks(progress);
        if (intent.foreground() && player != null && !closed) main.post(progress);
    }
    private void publishProgress() {
        if (player != null) listener.onProgress(generation, positionSeconds(), durationSeconds(), player.isPlaying());
    }
    private void publishState() {
        if (player == null || failed) return;
        switch (player.getPlaybackState()) {
            case Player.STATE_BUFFERING: listener.onState(generation, State.BUFFERING); break;
            case Player.STATE_READY: if (intent.canPublishReady()) listener.onState(generation, State.READY); break;
            case Player.STATE_ENDED: listener.onState(generation, State.ENDED); break;
            default: listener.onState(generation, State.IDLE); break;
        }
    }
    private void enforcePlayerIntent() {
        boolean playing = intent.playWhenReady();
        if (player != null && player.getPlayWhenReady() != playing) player.setPlayWhenReady(playing);
    }
    private void recoverNativeSuppression() {
        boolean recovered = intent.nativeUnsuppressed();
        enforcePlayerIntent();
        if (recovered) publishState();
    }
    private void updateSuppression() {
        if (!intent.foreground() || player == null || failed) {
            main.removeCallbacks(suppression);
            suppressionScheduled = false;
            return;
        }
        if (intent.requestedPlaying() && player.getPlaybackState() == Player.STATE_READY) {
            if (!player.getPlayWhenReady()) intent.systemPause();
            else if (player.getPlaybackSuppressionReason() != Player.PLAYBACK_SUPPRESSION_REASON_NONE) intent.transientSuppression();
        }
        if (intent.needsReport() && player.getPlaybackState() == Player.STATE_READY) {
            if (!suppressionScheduled) {
                suppressionScheduled = true;
                main.postDelayed(suppression, 1500);
            }
        } else {
            main.removeCallbacks(suppression);
            suppressionScheduled = false;
        }
    }
    private double positionSeconds() { return player == null ? 0 : Math.max(0, player.getCurrentPosition() / 1000.0); }
    private double durationSeconds() {
        if (player == null) return source == null ? 0 : source.durationSeconds;
        long duration = player.getDuration();
        return duration == C.TIME_UNSET || duration <= 0 ? (source == null ? 0 : source.durationSeconds) : duration / 1000.0;
    }
    private double boundedPosition(double value) { return Math.max(0, Math.min(durationSeconds(), Double.isFinite(value) ? value : 0)); }

    static PlaybackFailure failure(PlaybackException error) {
        for (Throwable cause = error; cause != null; cause = cause.getCause()) {
            if (cause instanceof HttpDataSource.InvalidResponseCodeException) {
                int status = ((HttpDataSource.InvalidResponseCodeException) cause).responseCode;
                if (status == 401 || status == 403 || status == 404 || status == 410) return new PlaybackFailure(PlaybackFailure.Reason.EXPIRED_SOURCE);
            }
        }
        if (error.errorCode >= PlaybackException.ERROR_CODE_DRM_UNSPECIFIED
                && error.errorCode <= PlaybackException.ERROR_CODE_DRM_LICENSE_EXPIRED) return new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
        if (error.errorCode >= PlaybackException.ERROR_CODE_DECODER_INIT_FAILED
                && error.errorCode <= PlaybackException.ERROR_CODE_AUDIO_TRACK_WRITE_FAILED) return new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
        if (error.errorCode == PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED
                || error.errorCode == PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED
                || error.errorCode == PlaybackException.ERROR_CODE_PARSING_MANIFEST_MALFORMED
                || error.errorCode == PlaybackException.ERROR_CODE_PARSING_MANIFEST_UNSUPPORTED) return new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
        return new PlaybackFailure(PlaybackFailure.Reason.NETWORK_RETRYABLE);
    }

    private void releasePlayer() {
        main.removeCallbacks(progress);
        main.removeCallbacks(suppression);
        suppressionScheduled = false;
        ExoPlayer previous = player;
        player = null; // Invalidate callbacks before detaching/releasing the previous generation.
        view.setPlayer(null);
        if (previous != null) previous.release();
    }
    private static void requireMain() {
        if (Looper.myLooper() != Looper.getMainLooper()) throw new IllegalStateException("Playback requires the main thread");
    }
    @Override public void close() {
        requireMain();
        if (closed) return;
        closed = true;
        releasePlayer();
        source = null;
        network.close();
    }
}
