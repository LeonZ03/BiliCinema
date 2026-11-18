package com.leonz03.bilicinema;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Intent;
import android.content.ComponentName;
import android.content.ServiceConnection;
import android.content.pm.ActivityInfo;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.Color;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.IBinder;
import android.os.SystemClock;
import android.view.View;
import android.view.WindowManager;
import android.webkit.CookieManager;
import androidx.media3.common.util.UnstableApi;
import org.json.JSONArray;
import org.json.JSONObject;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.CancellationException;

/** Composition and lifecycle for the native cinema; media and room state retain their existing owners. */
@UnstableApi
public final class MainActivity extends Activity implements RoomClient.Listener, CinemaUi.Actions, PlaybackAdapter.Listener {
    private static final int LOGIN_REQUEST = 41;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService mediaWork = Executors.newSingleThreadExecutor();
    private final BilibiliMediaResolver resolver = new BilibiliMediaResolver();
    private final MediaSession media = new MediaSession();
    private final MediaOverlayTracks overlayTracks = new MediaOverlayTracks();
    private final Map<String, String> nicknames = new LinkedHashMap<>();
    private CinemaUi ui;
    private RoomClient rooms;
    private RoomSessionService roomService;
    private boolean roomServiceBound;
    private Runnable pendingRoomConnect;
    private final ServiceConnection roomServiceConnection = new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName name, IBinder binder) {
            if (destroyed) return;
            roomService = ((RoomSessionService.LocalBinder) binder).service();
            rooms = roomService.client();
            roomService.attach(MainActivity.this);
            Runnable pending = pendingRoomConnect; pendingRoomConnect = null;
            if (pending != null && !inRoom()) pending.run();
            render();
        }
        @Override public void onServiceDisconnected(ComponentName name) {
            rooms = null; roomService = null;
            connected = false; connecting = false;
            if (!destroyed) status("房间后台连接已停止，请重新加入");
        }
    };
    private PlaybackAdapter playback;
    private UpdateManager updates;
    private Future<?> pendingWork;
    private JSONObject snapshot, pendingRoomMedia;
    private java.util.List<BilibiliMediaResolver.Resolved.CatalogItem> mediaCatalog = java.util.Collections.emptyList();
    private PlaybackFailure failure;
    private String room = "", role = "", memberId = "", title = "尚未选择影片", pendingInput = "", notice = "";
    private long version = -1, syncRevision = -1, roomEpoch, clockOffset;
    private long lastRoomCorrectionAt = -1;
    private boolean correctingRoomPlayback;
    private double position, duration, currentRate = 1;
    private int preferredQuality;
    private int subtitleIndex = -1;
    private boolean danmakuEnabled = true;
    private boolean danmakuAvailable;
    private String danmakuStatus = "弹幕加载中";
    private String overlayCookie = "";
    private java.util.List<MediaOverlayTracks.Subtitle> subtitles = java.util.Collections.emptyList();
    private boolean subtitleCatalogLoaded;
    private String subtitleLoadError = "";
    private float videoAspectRatio;
    private double pendingRoomPosition, pendingRoomRate = 1;
    private boolean hostRestorePending, pendingRoomPlaying;
    private boolean connected, connecting, busy, foreground = true, actuallyPlaying, soloIntent, loggedIn;
    private Boolean publishedReady, publishedBuffering;
    private volatile boolean destroyed;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        CinemaTheme.applySystemBars(this, false);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING);
        loggedIn = preferences().getBoolean("loggedIn", false);
        preferredQuality = Math.max(0, preferences().getInt("playbackQuality", 0));
        danmakuEnabled = preferences().getBoolean("danmakuEnabled", true);
        ui = new CinemaUi(this, this);
        setContentView(ui.root());
        createPlayback();
        roomServiceBound = bindService(new Intent(this, RoomSessionService.class), roomServiceConnection, BIND_AUTO_CREATE);
        if (!roomServiceBound) status("房间服务暂不可用，请重启应用后重试");
        updates = new UpdateManager(this, this::status);
        render();
        if (saved != null) {
            String identity = saved.getString("soloMedia", "");
            if (!identity.isEmpty()) {
                try {
                    position = saved.getDouble("position", 0);
                    currentRate = saved.getDouble("rate", 1);
                    openMedia(new JSONObject(identity), false);
                } catch (Exception error) { status("上次影片无法恢复，请重新选片"); }
            }
            if (media.selected() != null && saved.getBoolean("fullscreen", false)) ui.setFullscreen(true);
        }
        updates.recoverAndCheck();
        if (saved == null) receiveShare(getIntent());
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent); setIntent(intent); receiveShare(intent);
    }

    private void receiveShare(Intent intent) {
        if (intent == null || !Intent.ACTION_SEND.equals(intent.getAction()) || !"text/plain".equals(intent.getType())) return;
        CharSequence shared = intent.getCharSequenceExtra(Intent.EXTRA_TEXT);
        if (shared == null || shared.length() == 0 || shared.length() > 16_384) { status("分享内容为空或过长，请重新复制影片链接"); return; }
        if (!canControl() || connectionUnresolved()) { status("请先完成房间连接；房间内由房主选片"); return; }
        ui.receiveSharedMedia(shared.toString());
        status("已填入分享内容，选择打开影片或创建房间即可");
    }

    private SharedPreferences preferences() { return getPreferences(MODE_PRIVATE); }
    private boolean inRoom() { return !room.isEmpty(); }
    private boolean host() { return inRoom() && "host".equals(role); }
    private boolean canControl() { return !inRoom() || (host() && connected); }

    private void createPlayback() { createPlayback(false); }
    private void createPlayback(boolean official) {
        if (destroyed) return;
        try {
            playback = official ? new OfficialPlaybackAdapter(this, this) : new NativePlaybackAdapter(this, this);
            ui.setPlayerView(playback.view());
        } catch (RuntimeException error) {
            playback = null;
            failure = new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
        }
    }

    private void releasePlayback() {
        PlaybackAdapter previous = playback; playback = null;
        if (ui != null) ui.setPlayerView(null);
        if (previous != null) previous.close();
    }
    @Override public void createRoom(String input) {
        if (inRoom() || connectionUnresolved()) { status("请先完成或取消当前连接"); return; }
        pendingInput = input == null ? "" : input.trim();
        pendingRoomMedia = pendingInput.isEmpty() ? media.selected() : null;
        hostRestorePending = pendingRoomMedia != null;
        pendingRoomPosition = position; pendingRoomRate = currentRate; pendingRoomPlaying = soloIntent;
        connect("", "", RoomConnectionProgress.Operation.CREATE);
    }
    @Override public void joinRoom(String input) {
        if (inRoom() || connectionUnresolved()) { status("请先完成或取消当前连接"); return; }
        String value = RoomInvitation.code(input);
        try {
            if (value == null) { status("请输入完整邀请链接或22位房间码"); return; }
            pendingInput = ""; pendingRoomMedia = null;
            String credential = value.equals(preferences().getString("room", "")) ? preferences().getString("client", "") : "";
            connect(value, credential, RoomConnectionProgress.Operation.JOIN);
        } catch (RuntimeException error) { status("邀请链接无法识别，请重新复制"); }
    }
    @Override public void restoreRoom() {
        if (inRoom() || connectionUnresolved()) { status("请先完成或取消当前连接"); return; }
        String code = preferences().getString("room", ""), credential = preferences().getString("client", "");
        if (code.isEmpty() || credential.isEmpty()) { status("没有可恢复的房间"); return; }
        pendingInput = ""; pendingRoomMedia = null;
        connect(code, credential, RoomConnectionProgress.Operation.RESTORE);
    }
    private boolean connectionUnresolved() {
        return rooms != null && rooms.progress() != null && rooms.progress().needsResolution();
    }
    private void connect(String code, String credential, RoomConnectionProgress.Operation operation) {
        roomEpoch++; version = -1; syncRevision = -1; connected = false; connecting = true;
        publishedReady = null; publishedBuffering = null;
        notice = "正在连接云端房间…";
        if (android.os.Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)
                != android.content.pm.PackageManager.PERMISSION_GRANTED
                && !preferences().getBoolean("roomNotificationRequested", false)) {
            preferences().edit().putBoolean("roomNotificationRequested", true).apply();
            requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS}, 42);
        }
        if (roomService == null) {
            pendingRoomConnect = () -> startRoomConnection(code, credential, operation);
        } else startRoomConnection(code, credential, operation);
        render();
    }
    private void startRoomConnection(String code, String credential, RoomConnectionProgress.Operation operation) {
        if (roomService == null || !roomService.connect(code, credential, operation)) {
            connecting = false; connected = false;
            status("房间后台连接无法启动，请重试");
        }
    }
    @Override public void connectionProgress(RoomConnectionProgress progress) {
        if (destroyed) return;
        if (progress.outcome == RoomConnectionProgress.Outcome.RUNNING) { connected = false; connecting = true; }
        else if (progress.outcome == RoomConnectionProgress.Outcome.FAILED) {
            connected = false; connecting = false; notice = progress.failure;
        }
        render();
    }
    @Override public void retryRoomConnection() {
        if (rooms == null || rooms.progress() == null || rooms.progress().outcome != RoomConnectionProgress.Outcome.FAILED) return;
        roomEpoch++; version = -1; syncRevision = -1; connected = false; connecting = true;
        publishedReady = null; publishedBuffering = null; notice = "正在重试房间连接…";
        if (roomService == null || !roomService.retry()) connecting = false;
        render();
    }
    @Override public void cancelRoomConnection() {
        boolean established = !memberId.isEmpty();
        pendingRoomConnect = null;
        if (roomService != null) roomService.stopRoom(true);
        if (established) { clearRoom("已取消连接，可在本机继续观看", false); return; }
        roomEpoch++; room = ""; role = ""; memberId = ""; connected = false; connecting = false;
        snapshot = null; version = -1; syncRevision = -1;
        pendingInput = ""; pendingRoomMedia = null; hostRestorePending = false;
        publishedReady = null; publishedBuffering = null; notice = "已取消房间连接";
        render();
    }

    @Override public void selectMedia(String input) {
        if (!canControl() || connecting) { status("选片由房主操作"); return; }
        String value = input == null ? "" : input.trim();
        if (value.isEmpty()) { status("请粘贴 B 站影片链接或 BV 号"); return; }
        hostRestorePending = false;
        long request = media.requestSelection(), epoch = roomEpoch;
        cancelWork(); busy = true; notice = "正在确认影片与分P…"; render();
        String cookie = apiCookie();
        pendingWork = mediaWork.submit(() -> {
            try {
                BilibiliMediaResolver.Resolved resolved = resolver.input(value, cookie);
                main.post(() -> {
                    if (destroyed || !media.currentSelectionRequest(request) || epoch != roomEpoch) return;
                    busy = false;
                    if (inRoom()) {
                        if (host() && connected) sendMediaSelection(resolved.media);
                        else status("房间已变化，请重新选片");
                    } else {
                        soloIntent = true; position = 0;
                        openResolved(resolved);
                    }
                });
            } catch (Exception error) {
                if (error instanceof CancellationException || Thread.currentThread().isInterrupted()) return;
                main.post(() -> {
                    if (destroyed || !media.currentSelectionRequest(request) || epoch != roomEpoch) return;
                    busy = false;
                    if (error instanceof PlaybackFailure) notice = ((PlaybackFailure) error).safeMessage();
                    else notice = "影片解析未完成，请检查链接、网络和登录状态后重试";
                    render();
                });
            }
        });
    }
    @Override public void previewMedia(String input) {
        if (destroyed || inRoom() || connecting || busy || media.selected() != null) return;
        long request = media.requestSelection(), epoch = roomEpoch;
        cancelWork();
        if (input.trim().isEmpty()) return;
        String cookie = apiCookie();
        pendingWork = mediaWork.submit(() -> {
            try {
                BilibiliMediaResolver.Resolved resolved = resolver.input(input, cookie);
                main.post(() -> {
                    if (!destroyed && media.currentSelectionRequest(request) && epoch == roomEpoch) ui.showMediaPreview(input, resolved.title, resolved.duration, null);
                });
                if (resolved.cover.isEmpty()) return;
                byte[] bytes = resolver.cover(resolved);
                android.graphics.BitmapFactory.Options bounds = new android.graphics.BitmapFactory.Options(); bounds.inJustDecodeBounds = true;
                android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.length, bounds);
                android.graphics.BitmapFactory.Options options = new android.graphics.BitmapFactory.Options(); options.inSampleSize = 1;
                while (bounds.outWidth / options.inSampleSize > 320 || bounds.outHeight / options.inSampleSize > 320) options.inSampleSize *= 2;
                android.graphics.Bitmap image = android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.length, options);
                main.post(() -> {
                    if (!destroyed && media.currentSelectionRequest(request) && epoch == roomEpoch) ui.showMediaPreview(input, resolved.title, resolved.duration, image);
                });
            } catch (Exception error) {
                if (error instanceof CancellationException || Thread.currentThread().isInterrupted()) return;
                // Preview is optional; playback performs the authoritative resolution and reports actionable failures.
                main.post(() -> { if (!destroyed && media.currentSelectionRequest(request) && epoch == roomEpoch) ui.previewUnavailable(input); });
            }
        });
    }
    private void sendMediaSelection(JSONObject identity) {
        try {
            if (!rooms.send(new JSONObject().put("type", "select").put("media", identity))) status("影片尚未选择，请连接房间后重试");
        } catch (Exception error) { status("影片选择未发送，请重试"); }
    }
    private void openResolved(BilibiliMediaResolver.Resolved resolved) {
        long token = beginMedia(resolved.media);
        acceptResolved(token, resolved, apiCookie());
    }
    private void openMedia(JSONObject identity, boolean resetPosition) {
        if (resetPosition) position = 0;
        long token = beginMedia(identity);
        String cookie = apiCookie();
        pendingWork = mediaWork.submit(() -> {
            try {
                BilibiliMediaResolver.Resolved resolved = resolver.room(identity, cookie);
                main.post(() -> acceptResolved(token, resolved, cookie));
            } catch (Exception error) { reportLoadFailure(token, error); }
        });
    }
    private long beginMedia(JSONObject identity) {
        cancelWork();
        boolean sameMedia = media.key().equals(MediaIdentity.key(identity));
        long token = media.begin(identity, position, currentRate, !inRoom());
        syncRevision = -1;
        lastRoomCorrectionAt = -1;
        releasePlayback();
        mediaCatalog = java.util.Collections.emptyList();
        overlayTracks.cancel(); subtitleIndex = -1; subtitles = java.util.Collections.emptyList(); danmakuAvailable = false;
        subtitleCatalogLoaded = false; subtitleLoadError = "";
        if (ui != null) ui.updateTracks(subtitles, java.util.Collections.emptyList());
        busy = true; actuallyPlaying = false; failure = null;
        notice = "";
        if (!sameMedia) { title = "正在加载影片…"; duration = 0; videoAspectRatio = 0; applyOrientation(); }
        publishPreparation(true); render();
        return token;
    }
    private void acceptResolved(long token, BilibiliMediaResolver.Resolved resolved, String cookie) {
        if (destroyed || token != media.generation()) return;
        if (!media.resolved(token, resolved)) { fail(token, new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA)); return; }
        title = resolved.title.isEmpty() ? "房间影片" : resolved.title;
        mediaCatalog = resolved.catalog;
        overlayCookie = cookie;
        overlayTracks.load(resolved.media, cookie, token, this::overlayTracksLoaded);
        render();
        loadSource(token, resolved, cookie);
    }
    private void loadSource(long token, BilibiliMediaResolver.Resolved resolved, String cookie) {
        int quality = preferredQuality;
        pendingWork = mediaWork.submit(() -> {
            try {
                PlaybackSource source = resolver.playable(resolved, cookie, quality);
                main.post(() -> acceptSource(token, resolved, source));
            } catch (Exception error) { reportLoadFailure(token, error); }
        });
    }
    private void acceptSource(long token, BilibiliMediaResolver.Resolved resolved, PlaybackSource source) {
        if (destroyed || token != media.generation()) return;
        if (media.state() == MediaSession.State.RESOLVING && !media.resolved(token, resolved)) {
            fail(token, new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA)); return;
        }
        if (!media.bind(token, source)) { fail(token, new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA)); return; }
        busy = false; title = source.title.isEmpty() ? "房间影片" : source.title;
        duration = source.durationSeconds; failure = null;
        if (source.kind != PlaybackSource.Kind.OFFICIAL && preferredQuality > 0 && source.quality != preferredQuality) notice = "本片当前可用画质为 " + source.qualityLabel();
        if (playback == null) createPlayback(source.kind == PlaybackSource.Kind.OFFICIAL);
        if (playback == null) { fail(token, new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED)); return; }
        try {
            playback.prepare(source, token);
            playback.setForeground(foreground);
            if (inRoom()) applyCurrent(true);
            else playback.requestPlayback(soloIntent);
        } catch (RuntimeException error) { fail(token, new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED)); }
        render();
    }
    private void reportLoadFailure(long token, Exception error) {
        if (error instanceof CancellationException || Thread.currentThread().isInterrupted()) return;
        PlaybackFailure safe = error instanceof PlaybackFailure ? (PlaybackFailure) error
            : new PlaybackFailure(PlaybackFailure.Reason.NETWORK_RETRYABLE);
        main.post(() -> { if (!destroyed) fail(token, safe); });
    }
    private void fail(long token, PlaybackFailure error) {
        if (!media.fail(token)) return;
        overlayTracks.cancel(); subtitles = java.util.Collections.emptyList(); danmakuAvailable = false;
        ui.updateTracks(subtitles, java.util.Collections.emptyList());
        busy = false; actuallyPlaying = false; failure = error;
        if ("正在加载影片…".equals(title)) title = "影片加载未完成";
        if (error.reason == PlaybackFailure.Reason.NEEDS_LOGIN) {
            loggedIn = false; preferences().edit().putBoolean("loggedIn", false).apply();
        }
        publishPreparation(false); render();
    }
    private void cancelWork() {
        if (pendingWork != null) { pendingWork.cancel(true); pendingWork = null; }
        resolver.cancel();
    }
    @Override public void retryPlayback() {
        JSONObject selected = media.selected();
        if (selected == null) { status("请先选择影片"); return; }
        if (failure != null && failure.reason == PlaybackFailure.Reason.PLAYBACK_BLOCKED
                && playback != null && media.bound() && foreground && (!inRoom() || connected)) {
            // An explicit local gesture can release a system block without controlling the room.
            boolean requested = inRoom() ? snapshot != null && snapshot.optBoolean("playing") : soloIntent;
            if (requested) playback.play(); else playback.pause();
            return;
        }
        openMedia(selected, false);
    }

    @Override public void quality(int value) {
        PlaybackSource source = media.source();
        if (busy || source == null || (inRoom() && !connected)) return;
        if (value != 0 && source.qualities.stream().noneMatch(option -> option.id == value)) return;
        if (preferredQuality == value) return;
        preferredQuality = value;
        preferences().edit().putInt("playbackQuality", value).apply();
        // Quality is device-local. Readiness still follows the existing host/guest rules.
        openMedia(media.selected(), false);
    }
    @Override public void toggleDanmaku() {
        if (!danmakuAvailable) { if ("弹幕重试".equals(danmakuStatus)) retryOverlayTracks(); return; }
        danmakuEnabled = !danmakuEnabled;
        preferences().edit().putBoolean("danmakuEnabled", danmakuEnabled).apply();
        render();
    }
    @Override public void subtitle(int index) {
        if (index < -1 || index >= subtitles.size()) return;
        subtitleIndex = index; render();
        JSONObject selected = media.selected();
        if (index >= 0 && selected != null) overlayTracks.loadSubtitle(selected, overlayCookie, media.generation(), index, this::overlayTracksLoaded);
    }
    @Override public void retryOverlayTracks() {
        JSONObject selected = media.selected(); if (selected == null) return;
        subtitleCatalogLoaded = false; subtitleLoadError = ""; danmakuAvailable = false;
        overlayTracks.load(selected, overlayCookie, media.generation(), this::overlayTracksLoaded); render();
    }

    @Override public void togglePlayback() {
        if (!canControl() || media.selected() == null) { status(inRoom() ? "播放由房主控制" : "请先选择影片"); return; }
        if (inRoom()) {
            boolean requested = hostRestorePending ? pendingRoomPlaying
                : snapshot != null && (snapshot.optBoolean("playing") || snapshot.optBoolean("waitingForReady"));
            if (hostRestorePending) pendingRoomPlaying = !requested;
            if (!requested && media.state() == MediaSession.State.ENDED) commandValue("seek", "positionSeconds", 0);
            command(requested ? "pause" : "play");
        } else {
            soloIntent = !soloIntent;
            if (playback != null && media.bound()) {
                if (soloIntent) playback.play(); else playback.pause();
            }
        }
        render();
    }
    @Override public void seek(double seconds) {
        if (!canControl() || !Double.isFinite(seconds) || seconds < 0 || seconds > 86400) return;
        if (inRoom()) { if (hostRestorePending) pendingRoomPosition = seconds; commandValue("seek", "positionSeconds", seconds); }
        else if (media.selected() != null) {
            media.queueInitialSeek(media.generation(), seconds);
            position = seconds;
            overlayTracks.seek(media.selected(), overlayCookie, media.generation(), seconds, this::overlayTracksLoaded);
            if (playback != null && media.bound()) playback.seek(seconds);
            render();
        }
    }
    @Override public void rate(double value) {
        if (!canControl() || !Double.isFinite(value) || value < .25 || value > 3) return;
        if (inRoom()) { if (hostRestorePending) pendingRoomRate = value; commandValue("rate", "rate", value); }
        else if (media.selected() != null) {
            media.queueInitialRate(media.generation(), value);
            currentRate = value;
            if (playback != null && media.bound()) playback.setRate(value);
            render();
        }
    }
    @Override public void onState(long token, PlaybackAdapter.State state) {
        if (destroyed || token != media.generation()) return;
        switch (state) {
            case READY:
                if (!media.prepared(token)) return;
                failure = null; busy = false;
                break;
            case BUFFERING:
                if (!media.buffering(token)) return;
                actuallyPlaying = false;
                break;
            case ENDED:
                if (!media.ended(token)) return;
                actuallyPlaying = false; soloIntent = false;
                if (host() && connected) command("pause");
                break;
            case IDLE:
                return;
            default: return;
        }
        boolean restoringHost = state == PlaybackAdapter.State.READY && hostRestorePending && host() && connected;
        if (restoringHost) {
            hostRestorePending = false;
            try {
                rooms.send(new JSONObject().put("type", "sync").put("media", media.selected())
                    .put("positionSeconds", Math.min(pendingRoomPosition, duration)).put("rate", pendingRoomRate).put("playing", pendingRoomPlaying));
            } catch (Exception error) { status("房间已创建，进度恢复未完成，可手动同步播放"); }
        }
        publishPreparation(false);
        if (state == PlaybackAdapter.State.READY && foreground) {
            // The sync response owns the restored room target; do not seek to the pre-sync snapshot.
            if (inRoom()) { if (!restoringHost) applyCurrent(false); }
            else if (playback != null) {
                MediaSession.InitialAlignment initial = media.takeInitialAlignment(token);
                if (initial != null) {
                    position = initial.position; currentRate = initial.rate;
                    playback.apply(initial.position, initial.rate, soloIntent, true);
                }
            }
        }
        render();
    }
    @Override public void onProgress(long token, double sample, double total, boolean playing) {
        if (destroyed || token != media.generation() || !Double.isFinite(sample) || sample < 0) return;
        double previousPosition = position;
        if (!media.awaitingInitialAlignment() || inRoom()) position = sample;
        if (Double.isFinite(total) && total > 0) duration = total;
        JSONObject selectedMedia = media.selected();
        if (selectedMedia != null) {
            if (Math.abs(sample - previousPosition) > 3) overlayTracks.seek(selectedMedia, overlayCookie, token, sample, this::overlayTracksLoaded);
            else overlayTracks.loadSegment(selectedMedia, overlayCookie, token, (int) (sample / 360.0) + 1, this::overlayTracksLoaded);
        }
        boolean changed = actuallyPlaying != playing;
        actuallyPlaying = playing;
        ui.updatePosition(position, duration);
        ui.setOverlayPlayback(position, currentRate, actuallyPlaying && foreground);
        // Observation drives recovery even when the cloud timeline has not changed. A received
        // command is not proof that the native/site player applied it successfully.
        if (inRoom() && connected && foreground) {
            long now = SystemClock.elapsedRealtime();
            MediaSession.SyncState previous = media.syncState();
            observeRoomPlayback(now);
            if (!correctingRoomPlayback && (lastRoomCorrectionAt < 0 || now - lastRoomCorrectionAt >= 1000))
                applyCurrent(false);
            changed |= previous != media.syncState();
        }
        if (changed) render();
    }
    private void overlayTracksLoaded(long generation, java.util.List<MediaOverlayTracks.Subtitle> tracks,
                                     java.util.List<MediaOverlayTracks.Danmaku> comments, int segment, int state) {
        main.post(() -> {
            if (!destroyed && foreground && generation == media.generation() && overlayTracks.isCurrent(generation, tracks)) {
                subtitles = tracks;
                danmakuAvailable = !comments.isEmpty();
                subtitleCatalogLoaded = (state & MediaOverlayTracks.SUBTITLES_READY) != 0;
                subtitleLoadError = (state & MediaOverlayTracks.SUBTITLE_ERROR) != 0 ? "字幕加载失败，选择语言可重试" : "";
                danmakuStatus = (state & MediaOverlayTracks.DANMAKU_ERROR) != 0 ? "弹幕重试"
                        : (state & MediaOverlayTracks.DANMAKU_READY) != 0 ? "无弹幕" : "弹幕加载中";
                ui.updateTracks(tracks, comments); render();
                if (subtitleIndex >= 0 && subtitleIndex < tracks.size()) {
                    MediaOverlayTracks.Subtitle selected = tracks.get(subtitleIndex);
                    if (!selected.loaded && !selected.failed) overlayTracks.loadSubtitle(media.selected(), overlayCookie, generation, subtitleIndex, this::overlayTracksLoaded);
                }
            }
        });
    }
    @Override public void onQuality(long token, int quality) {
        if (!destroyed && media.updateQuality(token, quality)) render();
    }
    @Override public void onUserIntent(long token, double sample, double speed, boolean playing) {
        if (destroyed || token != media.generation()) return;
        if (!Double.isFinite(sample) || !Double.isFinite(speed)) return;
        if (!media.awaitingInitialAlignment()) { position = sample; currentRate = speed; }
        if (!inRoom()) { soloIntent = playing; render(); }
        // Room controls are sent to the authoritative cloud timeline by Actions; physical callbacks never re-publish them.
    }
    @Override public void onError(long token, PlaybackFailure error) {
        if (!destroyed) fail(token, error);
    }

    private void publishPreparation(boolean force) {
        if (!connected || !inRoom() || snapshot == null || snapshot.optJSONObject("media") == null
                || !MediaIdentity.key(snapshot.optJSONObject("media")).equals(media.key())) return;
        boolean ready = media.ready(), buffering = media.buffering();
        if (!ready && (force || publishedReady == null || publishedReady)) {
            if (commandFlag("ready", "ready", false)) publishedReady = false;
        }
        if (force || publishedBuffering == null || publishedBuffering != buffering) {
            if (commandFlag("buffering", "buffering", buffering)) publishedBuffering = buffering;
        }
        if (ready && (force || publishedReady == null || !publishedReady)) {
            if (commandFlag("ready", "ready", true)) publishedReady = true;
        }
    }
    @Override public void credentials(String code, String client) {
        // Capture at the actual solo-to-room boundary, after any playback during connection setup.
        if (hostRestorePending && pendingRoomMedia != null && !inRoom()
                && MediaIdentity.key(pendingRoomMedia).equals(media.key())) {
            pendingRoomPosition = position; pendingRoomRate = currentRate; pendingRoomPlaying = soloIntent;
        }
        room = code;
    }
    @Override public void message(JSONObject message) {
        if (destroyed) return;
        switch (message.optString("type")) {
            case "welcome":
                connected = true; connecting = false;
                role = message.optString("role"); memberId = message.optString("memberId");
                version = -1; syncRevision = -1; publishedReady = null; publishedBuffering = null;
                applySnapshot(message.optJSONObject("snapshot"), true);
                if (host()) {
                    if (!pendingInput.isEmpty()) { String input = pendingInput; pendingInput = ""; selectMedia(input); }
                    else if (pendingRoomMedia != null) { JSONObject identity = pendingRoomMedia; pendingRoomMedia = null; sendMediaSelection(identity); }
                }
                publishPreparation(true);
                if (playback != null) playback.setForeground(foreground);
                render();
                break;
            case "snapshot": applySnapshot(message.optJSONObject("snapshot"), false); break;
            case "pong":
                long now = System.currentTimeMillis(), sent = message.optLong("clientTimeUnixMs", now);
                if (sent <= now && now - sent < 60_000) clockOffset = message.optLong("serverTimeUnixMs", now) - sent - (now - sent) / 2;
                break;
            case "chat":
                String sender = message.optString("memberId"), name = message.optString("nickname"), text = message.optString("text");
                if (name.isEmpty() || name.length() > 24 || text.isEmpty() || text.length() > 500) return;
                nicknames.put(sender, name);
                if (nicknames.size() > 16) nicknames.remove(nicknames.keySet().iterator().next());
                ui.appendChat(name + ("host".equals(message.optString("role")) ? " · 房主" : ""), text, sender.equals(memberId));
                render();
                break;
            case "transportLost":
                connected = false; connecting = true;
                if (playback != null) playback.apply(position, currentRate, false, false);
                media.suspend(); notice = "连接已断开，正在重连…"; render();
                break;
            case "closed": clearRoom("房间已结束，可在本机继续观看", true); break;
            case "left": clearRoom("已退出房间", true); break;
            case "reconnectEnded": clearRoom("重连窗口已结束，可重新加入房间", false); break;
            case "error":
                String code = message.optString("code");
                if (RoomClient.terminal(code)) clearRoom(roomError(code), !"room_full".equals(code));
                else status(roomError(code));
                break;
            default: break;
        }
    }
    private void applySnapshot(JSONObject next, boolean force) {
        if (next == null) return;
        long revision = next.optLong("version", -1);
        if (!force && revision <= version) return;
        version = Math.max(version, revision); snapshot = next;
        JSONObject selected = next.optJSONObject("media");
        if (selected == null) {
            if (media.selected() != null) clearMedia();
        } else if (!MediaIdentity.key(selected).equals(media.key())) {
            openMedia(selected, true);
        } else {
            applyCurrent(force); publishPreparation(false);
        }
        render();
    }
    private void applyCurrent(boolean force) {
        if (correctingRoomPlayback || snapshot == null || playback == null || !media.bound() || !connected || !foreground) return;
        if (!MediaIdentity.key(snapshot.optJSONObject("media")).equals(media.key())) return;
        double target = MediaIdentity.targetPosition(snapshot, System.currentTimeMillis(), clockOffset);
        double speed = snapshot.optDouble("rate", 1);
        if (!Double.isFinite(target) || !Double.isFinite(speed) || target < 0 || target > 86400 || speed < .25 || speed > 3) return;
        long nextSync = snapshot.optLong("syncRevision", 0);
        boolean align = force || nextSync != syncRevision;
        currentRate = speed;
        lastRoomCorrectionAt = SystemClock.elapsedRealtime();
        observeRoomPlayback(lastRoomCorrectionAt);
        correctingRoomPlayback = true;
        try {
            if (media.followRoom(playback, target, speed, snapshot.optBoolean("playing"), align)) syncRevision = nextSync;
        } finally { correctingRoomPlayback = false; }
    }
    private void observeRoomPlayback(long now) {
        if (snapshot == null || !MediaIdentity.key(snapshot.optJSONObject("media")).equals(media.key())) {
            media.clearRoomSync(); return;
        }
        media.observeRoomPlayback(snapshot.optBoolean("playing"), actuallyPlaying,
            MediaIdentity.targetPosition(snapshot, System.currentTimeMillis(), clockOffset), position, now);
    }

    @Override public void invite() {
        if (!inRoom()) return;
        ClipboardManager clipboard = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
        if (clipboard == null) { status("剪贴板暂不可用"); return; }
        clipboard.setPrimaryClip(ClipData.newPlainText("BiliCinema邀请", RoomClient.ENDPOINT + "?room=" + room));
        status("邀请链接已复制");
    }
    @Override public void leaveRoom() {
        if (inRoom() || connecting) {
            pendingRoomConnect = null;
            if (roomService != null) roomService.stopRoom(true);
            clearRoom("已退出房间，可在本机继续观看", true);
        } else clearMedia();
    }
    private void clearRoom(String message, boolean forget) {
        if (roomService != null) roomService.stopRoom(false);
        media.clearRoomSync(); lastRoomCorrectionAt = -1;
        roomEpoch++; room = ""; role = ""; memberId = ""; connected = false; connecting = false;
        snapshot = null; version = -1; syncRevision = -1;
        pendingInput = ""; pendingRoomMedia = null; publishedReady = null; publishedBuffering = null; nicknames.clear();
        hostRestorePending = false;
        soloIntent = false; notice = message;
        if (forget) {
            if (roomService != null) roomService.forgetSavedRoom();
            else preferences().edit().remove("room").remove("client").apply();
        }
        if (playback != null) playback.apply(position, currentRate, false, false);
        render();
    }
    private void clearMedia() {
        cancelWork(); media.reset(); busy = false; failure = null; actuallyPlaying = false; soloIntent = false;
        overlayTracks.cancel(); subtitles = java.util.Collections.emptyList(); mediaCatalog = java.util.Collections.emptyList();
        subtitleIndex = -1; subtitleCatalogLoaded = false; subtitleLoadError = ""; danmakuAvailable = false;
        ui.updateTracks(subtitles, java.util.Collections.emptyList());
        position = 0; duration = 0; currentRate = 1; videoAspectRatio = 0; title = "尚未选择影片";
        ui.setFullscreen(false);
        releasePlayback();
        createPlayback(); render();
    }
    @Override public void setNickname(String name) {
        String value = name == null ? "" : name.trim();
        if (value.isEmpty() || value.length() > 24 || value.chars().anyMatch(Character::isISOControl)) { status("昵称需要1–24字，不能包含控制字符"); return; }
        preferences().edit().putString("nickname", value).apply(); render();
    }
    @Override public void sendChat(String text) {
        String value = text == null ? "" : text.trim();
        if (value.isEmpty() || value.length() > 500) { status("消息需要1–500字"); return; }
        String nickname = preferences().getString("nickname", "影友");
        try {
            if (rooms.send(new JSONObject().put("type", "chat").put("nickname", nickname).put("text", value))) ui.chatSent();
        } catch (Exception error) { status("消息尚未发送，请重试"); }
    }
    @Override public void login() { startActivityForResult(new Intent(this, LoginActivity.class), LOGIN_REQUEST); }
    @Override public void openOfficial() {
        JSONObject selected = media.selected();
        if (selected == null) return;
        String url = selected.optLong("episodeId") > 0
                ? "https://www.bilibili.com/bangumi/play/ep" + selected.optLong("episodeId")
                : "https://www.bilibili.com/video/" + selected.optString("bvid");
        try { startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url))); }
        catch (android.content.ActivityNotFoundException error) { status("未找到可打开B站的应用，请安装B站或浏览器后重试"); }
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == LOGIN_REQUEST && result == RESULT_OK) {
            loggedIn = true; preferences().edit().putBoolean("loggedIn", true).apply();
            notice = "本机登录已验证";
            if (media.selected() != null) retryPlayback();
            render();
        } else if (request == LOGIN_REQUEST && result == LoginActivity.RESULT_LOGGED_OUT) {
            loggedIn = false; preferences().edit().putBoolean("loggedIn", false).apply();
            notice = "未登录B站"; render();
        }
    }
    private String apiCookie() {
        try {
            String value = CookieManager.getInstance().getCookie("https://api.bilibili.com/");
            return value == null ? "" : value;
        } catch (RuntimeException error) {
            status("本机登录组件暂不可用，正在以未登录状态尝试播放；可更新系统 WebView 后重试登录");
            return "";
        }
    }
    @Override public void checkUpdate() { updates.check(true); }
    @Override public void fullscreenChanged(boolean fullscreen) {
        applyOrientation();
        applyFullscreenWindow(fullscreen);
    }
    @Override public void onVideoAspectRatio(long token, float ratio) {
        if (destroyed || token != media.generation() || !Float.isFinite(ratio) || ratio <= 0) return;
        if (Math.abs(videoAspectRatio - ratio) < .001f) return;
        videoAspectRatio = ratio;
        applyOrientation();
    }
    private void applyOrientation() {
        boolean fullscreen = ui != null && ui.isFullscreen();
        // Keep the current direction while metadata is loading instead of briefly
        // rotating a portrait film into landscape before its dimensions arrive.
        if (fullscreen && videoAspectRatio <= 0) return;
        int orientation = !fullscreen ? ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
                : CinemaInteraction.fullscreenLandscape(true, videoAspectRatio)
                ? ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                : ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT;
        if (getRequestedOrientation() != orientation) setRequestedOrientation(orientation);
    }
    private void applyFullscreenWindow(boolean fullscreen) {
        getWindow().getDecorView().setSystemUiVisibility(fullscreen
            ? View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            : View.SYSTEM_UI_FLAG_VISIBLE);
        if (!fullscreen) CinemaTheme.applySystemBars(this, inRoom() || displayState() != CinemaUi.PlaybackState.EMPTY);
    }
    private void command(String type) {
        try { rooms.send(new JSONObject().put("type", type)); }
        catch (Exception error) { status("房间操作未发送，请重试"); }
    }
    private void commandValue(String type, String key, double value) {
        try { rooms.send(new JSONObject().put("type", type).put(key, value)); }
        catch (Exception error) { status("房间操作未发送，请重试"); }
    }
    private boolean commandFlag(String type, String key, boolean value) {
        try { return rooms.send(new JSONObject().put("type", type).put(key, value)); }
        catch (Exception error) { status("本机准备状态未发送，请重连"); return false; }
    }
    @Override public void status(String value) {
        if (destroyed) return;
        if (Looper.myLooper() != Looper.getMainLooper()) { main.post(() -> status(value)); return; }
        notice = value; render();
    }

    private void render() {
        if (destroyed || ui == null) return;
        CinemaUi.Model model = new CinemaUi.Model();
        model.inRoom = inRoom(); model.isHost = host(); model.connected = !inRoom() || connected;
        model.canControl = canControl(); model.busy = busy || connecting;
        model.roomConnection = rooms == null ? null : rooms.progress();
        model.canRestore = !preferences().getString("room", "").isEmpty();
        model.loggedIn = loggedIn; model.accountLabel = loggedIn ? "本机登录已保存" : "B站登录";
        model.nickname = preferences().getString("nickname", "影友");
        model.roomTitle = "观影房间";
        model.mediaTitle = title; model.rate = currentRate; model.notice = notice;
        model.catalog = mediaCatalog;
        model.subtitles = subtitles;
        model.subtitleStatus = !subtitleLoadError.isEmpty() ? subtitleLoadError
                : subtitleCatalogLoaded && subtitles.isEmpty() ? "此视频没有可选字幕" : "字幕列表加载中，请稍后重试";
        model.subtitleIndex = subtitleIndex; model.danmakuEnabled = danmakuEnabled;
        model.danmakuAvailable = danmakuAvailable;
        model.danmakuStatus = danmakuStatus;
        PlaybackSource source = media.source();
        if (source != null) {
            model.qualities = source.qualities;
            model.qualityLabel = source.qualityLabel();
        }
        model.preferredQuality = preferredQuality;
        model.playing = inRoom() ? hostRestorePending ? pendingRoomPlaying
            : snapshot != null && (snapshot.optBoolean("playing") || snapshot.optBoolean("waitingForReady")) : soloIntent;
        model.playbackState = displayState();
        model.status = failure != null ? failure.safeMessage() : playbackStatus(model.playbackState);
        if (snapshot != null) {
            addMember(model, snapshot.optJSONObject("host"), "房主", true);
            JSONArray guests = snapshot.optJSONArray("guests");
            if (guests != null) for (int i = 0; i < guests.length(); i++) addMember(model, guests.optJSONObject(i), "影友" + (i + 1), false);
        }
        ui.render(model); ui.updatePosition(position, duration);
        ui.setOverlayPlayback(position, currentRate, actuallyPlaying && foreground);
    }
    private CinemaUi.PlaybackState displayState() {
        if (inRoom() && !connected) return CinemaUi.PlaybackState.RECONNECTING;
        if (failure != null) {
            switch (failure.reason) {
                case NEEDS_LOGIN: return CinemaUi.PlaybackState.NEEDS_LOGIN;
                case PERMISSION_DENIED: return CinemaUi.PlaybackState.NO_PERMISSION;
                case DRM_PROTECTED: return CinemaUi.PlaybackState.DRM_PROTECTED;
                case UNSUPPORTED: return CinemaUi.PlaybackState.UNSUPPORTED;
                case PLAYBACK_BLOCKED: return CinemaUi.PlaybackState.NEEDS_GESTURE;
                default: return CinemaUi.PlaybackState.RETRYABLE_ERROR;
            }
        }
        if (inRoom() && media.ready()) {
            if (media.syncState() == MediaSession.SyncState.STALLED) return CinemaUi.PlaybackState.SYNC_FAILED;
            if (media.syncState() == MediaSession.SyncState.CATCHING_UP) return CinemaUi.PlaybackState.SYNCING;
        }
        switch (media.state()) {
            case EMPTY: return CinemaUi.PlaybackState.EMPTY;
            case RESOLVING: case LOADING: return inRoom() && !host() ? CinemaUi.PlaybackState.CATCHING_UP : CinemaUi.PlaybackState.LOADING;
            case BUFFERING: case SUSPENDED: return inRoom() && !host() ? CinemaUi.PlaybackState.CATCHING_UP : CinemaUi.PlaybackState.BUFFERING;
            case READY: return actuallyPlaying ? CinemaUi.PlaybackState.PLAYING : CinemaUi.PlaybackState.PAUSED;
            case ENDED: return CinemaUi.PlaybackState.PAUSED;
            case ERROR: return CinemaUi.PlaybackState.RETRYABLE_ERROR;
            default: return CinemaUi.PlaybackState.EMPTY;
        }
    }
    private String playbackStatus(CinemaUi.PlaybackState state) {
        switch (state) {
            case EMPTY: return "选择影片，和朋友一起看";
            case LOADING: return "正在准备本机影片…";
            case CATCHING_UP: return "本机正在加载，准备后追上房主";
            case SYNCING: return "正在同步房主的播放状态…";
            case SYNC_FAILED: return "暂未跟上房主，正在自动重试，也可点击重新同步";
            case BUFFERING: return "房主正在缓冲，准备后继续";
            case PLAYING: return inRoom() ? (host() ? "已同步 · 房主正在播放" : "已同步 · 跟随房主播放") : "正在播放";
            case RECONNECTING: return "房间连接已断开，正在重连";
            case PAUSED: return inRoom() && !host() ? "房主已暂停" : "已就绪";
            default: return "请重试播放";
        }
    }
    private void addMember(CinemaUi.Model model, JSONObject member, String fallback, boolean owner) {
        if (member == null) return;
        String id = member.optString("memberId");
        boolean self = id.equals(memberId);
        String name = self ? preferences().getString("nickname", "影友") : nicknames.containsKey(id) ? nicknames.get(id) : fallback;
        String state = !member.optBoolean("online") ? "离线" : member.optBoolean("buffering") ? "缓冲中" : member.optBoolean("ready") ? "已就绪" : "准备中";
        model.members.add(new CinemaUi.Member(name, state, owner, self));
    }
    private static String roomError(String code) {
        switch (code) {
            case "room_full": return "房间已满，最多5人";
            case "host_only": return "该操作由房主控制";
            case "rate_limited": return "操作过于频繁，请稍后重试";
            case "room_unavailable": case "room_not_found": case "room_expired": return "房间已结束或邀请已失效，请重新加入";
            case "invalid_client": case "invalid_client_id": case "client_expired": return "重连凭据已失效，请重新加入";
            default: return "房间操作未完成，请检查连接后重试";
        }
    }
    @Override public void onBackPressed() {
        if (ui.handleBack()) return;
        if (connectionUnresolved()) { cancelRoomConnection(); return; }
        if (inRoom()) new AlertDialog.Builder(this).setMessage("退出当前房间？").setNegativeButton("继续观看", null)
            .setPositiveButton("退出房间", (dialog, which) -> leaveRoom()).show();
        else if (media.selected() != null) clearMedia();
        else super.onBackPressed();
    }
    @Override public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration); ui.onConfigurationChanged(configuration);
        applyFullscreenWindow(ui.isFullscreen());
    }
    @Override public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus && ui != null && ui.isFullscreen()) applyFullscreenWindow(true);
    }
    @Override protected void onPause() {
        super.onPause(); foreground = false; media.setForeground(false); publishPreparation(false);
        if (playback != null) playback.setForeground(false);
        if (ui != null) { ui.setOverlayPlayback(position, currentRate, false); ui.setOverlayForeground(false); }
        overlayTracks.cancel(); subtitles = java.util.Collections.emptyList(); danmakuAvailable = false;
        if (ui != null) ui.updateTracks(subtitles, java.util.Collections.emptyList());
    }
    @Override protected void onResume() {
        super.onResume(); foreground = true; media.setForeground(true);
        if (playback != null) playback.setForeground(true);
        if (ui != null) ui.setOverlayForeground(true);
        JSONObject selected = media.selected();
        if (selected != null) {
            long token = media.generation(); overlayTracks.load(selected, overlayCookie, token, this::overlayTracksLoaded);
            subtitleCatalogLoaded = false; subtitleLoadError = "";
        }
        if (updates != null) updates.installPending();
        render();
    }
    @Override protected void onSaveInstanceState(Bundle saved) {
        super.onSaveInstanceState(saved);
        if (!inRoom() && media.selected() != null) saved.putString("soloMedia", media.selected().toString());
        saved.putDouble("position", position); saved.putDouble("rate", currentRate); saved.putBoolean("fullscreen", ui.isFullscreen());
    }
    @Override protected void onDestroy() {
        destroyed = true; cancelWork(); main.removeCallbacksAndMessages(null); mediaWork.shutdownNow(); resolver.close();
        overlayTracks.close();
        pendingRoomConnect = null;
        if (roomService != null) roomService.detach(this);
        if (roomServiceBound) { unbindService(roomServiceConnection); roomServiceBound = false; }
        rooms = null; roomService = null;
        releasePlayback();
        if (updates != null) updates.close();
        if (ui != null) ui.dispose();
        super.onDestroy();
    }
}
