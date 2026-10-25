package com.leonz03.bilicinema;

import android.annotation.SuppressLint;
import android.content.Context;
import android.graphics.Color;
import android.net.http.SslError;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.view.View;
import android.view.ViewGroup;
import android.view.MotionEvent;
import android.webkit.ConsoleMessage;
import android.webkit.PermissionRequest;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.SslErrorHandler;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import org.json.JSONObject;
import org.json.JSONTokener;
import java.io.IOException;
import java.io.InputStream;
import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;

/** The official site owns authorization/DRM. No JavaScript bridge, license or key extraction. */
final class OfficialPlaybackAdapter implements PlaybackAdapter {
    private final WebView web;
    private final FrameLayout host;
    private final Listener listener;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final PlaybackIntentState intent = new PlaybackIntentState();
    private final String scriptTemplate;
    private PlaybackSource source;
    private long generation, deadline;
    private double position, duration, rate = 1;
    private State lastState;
    private boolean closed, failed, pageReady, rendererGone;
    private String script;
    private final Runnable tick = this::sample;

    @SuppressLint("SetJavaScriptEnabled")
    OfficialPlaybackAdapter(Context context, Listener listener) {
        this.listener = listener;
        try (InputStream stream = context.getAssets().open("official-player.js"); ByteArrayOutputStream buffer = new ByteArrayOutputStream()) {
            byte[] bytes = new byte[4096]; int count;
            while ((count = stream.read(bytes)) >= 0) buffer.write(bytes, 0, count);
            scriptTemplate = new String(buffer.toByteArray(), StandardCharsets.UTF_8);
        } catch (IOException error) { throw new IllegalStateException("Official player integration unavailable", error); }
        web = new WebView(context) {
            @Override public boolean onTouchEvent(MotionEvent event) {
                if (event.getActionMasked() == MotionEvent.ACTION_UP) performClick();
                return true;
            }
            @Override public boolean performClick() { super.performClick(); return true; }
        };
        web.setBackgroundColor(Color.BLACK); web.setAlpha(0);
        host = new FrameLayout(context); host.setBackgroundColor(Color.BLACK);
        host.addView(web, new FrameLayout.LayoutParams(-1, -1));
        web.setContentDescription("影片画面");
        web.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS);
        // Native controls own all user actions, including the chat gesture surface above this view.
        WebSettings settings = web.getSettings();
        settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(true);
        // Honor the video-only page viewport across orientation changes. All
        // interaction and accessibility controls live in the native overlay.
        settings.setUseWideViewPort(true); settings.setLoadWithOverviewMode(false); settings.setSupportZoom(false);
        settings.setMediaPlaybackRequiresUserGesture(false); settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setUserAgentString(BilibiliMediaResolver.USER_AGENT);
        web.setWebChromeClient(new WebChromeClient() {
            @Override public boolean onConsoleMessage(ConsoleMessage message) { return true; }
            @Override public void onPermissionRequest(PermissionRequest request) {
                if (!closed && !failed && "https://www.bilibili.com".equals(request.getOrigin().toString().replaceAll("/$", ""))) {
                    for (String resource : request.getResources()) if (PermissionRequest.RESOURCE_PROTECTED_MEDIA_ID.equals(resource)) {
                        request.grant(new String[]{resource}); return;
                    }
                }
                request.deny();
            }
        });
        web.setWebViewClient(new WebViewClient() {
            @Override public void onPageCommitVisible(WebView view, String url) {
                if (!closed && !failed && allowedPage(url)) { pageReady = true; startSampling(); }
            }
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                return !allowedPage(request.getUrl().toString());
            }
            @Override public void onPageFinished(WebView view, String url) {
                if (closed || failed) return;
                if (!allowedPage(url)) { fail(PlaybackFailure.Reason.INVALID_MEDIA); return; }
                pageReady = true; startSampling();
            }
            @Override public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
                handler.cancel(); fail(PlaybackFailure.Reason.NETWORK_RETRYABLE);
            }
            @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) fail(PlaybackFailure.Reason.NETWORK_RETRYABLE);
            }
            @Override public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
                rendererGone = true;
                if (view.getParent() instanceof ViewGroup) ((ViewGroup) view.getParent()).removeView(view);
                view.destroy(); fail(PlaybackFailure.Reason.DRM_PROTECTED); return true;
            }
        });
    }

    private boolean allowedPage(String url) {
        if (source == null) return false;
        try {
            java.net.URI candidate = new java.net.URI(url);
            return "https".equals(candidate.getScheme()) && "www.bilibili.com".equals(candidate.getHost())
                    && candidate.getPort() == -1 && candidate.getRawUserInfo() == null
                    && candidate.getPath().replaceAll("/$", "").equals(source.videoUri.getPath().replaceAll("/$", ""));
        } catch (java.net.URISyntaxException error) { return false; }
    }
    @Override public View view() { return host; }
    @Override public void prepare(PlaybackSource value, long token) {
        if (closed) return;
        source = value; generation = token; duration = value.durationSeconds; intent.prepare();
        script = scriptTemplate.replace("EXPECTED_CID", Long.toString(value.media().optLong("cid")))
                .replace("EXPECTED_EPISODE", Long.toString(value.media().optLong("episodeId")))
                .replace("EXPECTED_DURATION", Double.toString(duration)).replace("REQUESTED_QUALITY", Integer.toString(value.quality));
        deadline = SystemClock.elapsedRealtime() + 60_000;
        web.loadUrl(value.videoUri.toString()); startSampling();
    }
    private void startSampling() {
        main.removeCallbacks(tick);
        if (!closed && !failed && intent.foreground()) main.postDelayed(tick, 250);
    }
    private void sample() {
        if (closed || failed || !intent.foreground()) return;
        if (SystemClock.elapsedRealtime() > deadline && lastState != State.READY && lastState != State.ENDED) {
            fail(PlaybackFailure.Reason.DRM_PROTECTED); return;
        }
        if (!pageReady) { startSampling(); return; }
        // The official page can recreate its JS state while the room snapshot is unchanged.
        // Reassert retained device intent before observing playback, without repeating seeks.
        command(null, false);
        web.evaluateJavascript(script, encoded -> {
            if (closed || failed || !intent.foreground()) return;
            try {
                Object decoded = new JSONTokener(encoded).nextValue();
                if (!(decoded instanceof String)) { startSampling(); return; }
                OfficialPlaybackSnapshot observation = new OfficialPlaybackSnapshot(new JSONObject((String) decoded), source);
                position = observation.position; if (observation.duration > 0) duration = observation.duration;
                if (observation.videoAspectRatio > 0) listener.onVideoAspectRatio(generation, observation.videoAspectRatio);
                if (observation.blocked) { intent.systemPause(); reportBlock(); }
                if (observation.ready) {
                    web.setAlpha(1);
                    if (observation.quality > 0) listener.onQuality(generation, observation.quality);
                }
                listener.onProgress(generation, position, duration, observation.playing);
                State next = observation.ended ? State.ENDED : observation.ready ? State.READY : State.BUFFERING;
                if (next == State.ENDED) intent.ended();
                if ((next != State.READY || intent.canPublishReady()) && next != lastState) {
                    lastState = next; deadline = SystemClock.elapsedRealtime() + 60_000;
                    listener.onState(generation, next);
                }
            } catch (PlaybackFailure error) { fail(error.reason); }
            catch (org.json.JSONException error) { fail(PlaybackFailure.Reason.DRM_PROTECTED); }
            startSampling();
        });
    }
    private void command(Double seek, boolean clearBlock) {
        if (closed || failed || !pageReady) return;
        String positionCommand = seek == null ? "" : "s.seek=" + Math.max(0, Math.min(duration, seek)) + ";";
        web.evaluateJavascript("(()=>{const s=window.__cinemaControl||={playing:false,rate:1,seek:null};s.playing="
                + intent.playWhenReady() + ";s.rate=" + rate + ";" + positionCommand
                + (clearBlock ? "s.blocked=false;" : "") + "if(!s.playing)document.querySelectorAll('video').forEach(v=>v.pause());})()", null);
    }
    @Override public void requestPlayback(boolean playing) {
        intent.remote(playing);
        command(null, false);
    }
    @Override public void apply(double target, double speed, boolean playing, boolean force) {
        intent.remote(playing); rate = PlaybackIntentState.boundedRate(speed);
        command(force || Math.abs(target - position) > 1.25 || lastState == State.ENDED ? target : null, false);
    }
    @Override public void play() {
        boolean recovered = intent.localPlayback(true);
        command(lastState == State.ENDED ? 0.0 : null, true);
        if (recovered) lastState = null;
        listener.onUserIntent(generation, position, rate, true);
    }
    @Override public void pause() {
        boolean recovered = intent.localPlayback(false); command(null, true);
        if (recovered) lastState = null;
        listener.onUserIntent(generation, position, rate, false);
    }
    @Override public void seek(double target) { command(target, false); }
    @Override public void setRate(double value) { rate = PlaybackIntentState.boundedRate(value); command(null, false); }
    @Override public void setForeground(boolean active) {
        if (closed || failed) return;
        intent.setForeground(active); command(null, false); main.removeCallbacks(tick);
        if (active) { web.onResume(); lastState = null; deadline = SystemClock.elapsedRealtime() + 60_000; startSampling(); }
        else web.onPause();
    }
    private void reportBlock() {
        if (intent.reportBlocked()) listener.onError(generation, new PlaybackFailure(PlaybackFailure.Reason.PLAYBACK_BLOCKED));
    }
    private void fail(PlaybackFailure.Reason reason) {
        if (closed || failed) return;
        failed = true; main.removeCallbacks(tick);
        if (!rendererGone) { web.setAlpha(0); web.stopLoading(); web.loadUrl("about:blank"); }
        listener.onError(generation, new PlaybackFailure(reason));
    }
    @Override public void close() {
        if (closed) return;
        closed = true; main.removeCallbacks(tick);
        if (!rendererGone) { host.removeView(web); web.stopLoading(); web.destroy(); }
    }
}
