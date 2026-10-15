package com.leonz03.bilicinema;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.res.Configuration;
import android.graphics.Color;
import android.net.Uri;
import android.net.http.SslError;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.webkit.CookieManager;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.SslErrorHandler;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import org.json.JSONArray;
import org.json.JSONObject;
import androidx.webkit.WebViewCompat;
import androidx.webkit.WebViewFeature;
import java.util.HashSet;
import java.util.Arrays;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Native shell. Bilibili login and playback run in the device's isolated WebView cookie store. */
public final class MainActivity extends Activity implements RoomClient.Listener {
    private static final int BG = Color.rgb(15, 15, 35), TEXT = Color.rgb(248, 250, 252), MUTED = Color.rgb(173, 184, 201);
    private final Handler main = new Handler(Looper.getMainLooper());
    private RoomClient rooms;
    private UpdateManager updates;
    private final BilibiliMediaResolver mediaResolver = new BilibiliMediaResolver();
    private final ExecutorService mediaWork = Executors.newSingleThreadExecutor();
    private final MediaSession mediaSession = new MediaSession();
    private WebView player;
    private LinearLayout root, content, controls;
    private FrameLayout playerContainer, fullscreen;
    private TextView status, roomLabel, members, chatLog;
    private EditText mediaInput, roomInput, chatInput, nickname;
    private Button play, pause, select, rate, copy;
    private JSONObject lastSnapshot;
    private String roomCode = "", role = "", memberId = "", lastUrl = "https://www.bilibili.com";
    private long version = -1, clockOffset, syncRevision;
    private boolean destroyed, inBackground;
    private boolean secureBridge;
    private View customView;
    private WebChromeClient.CustomViewCallback customCallback;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        getWindow().setStatusBarColor(BG); getWindow().setNavigationBarColor(BG);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        rooms = new RoomClient(this);
        updates = new UpdateManager(this, this::status);
        buildUi();
        createPlayer();
        if (player != null) player.loadUrl(lastUrl);
        if (saved != null) {
            lastUrl = saved.getString("url", lastUrl);
            if (player != null) player.loadUrl(lastUrl);
        }
        updates.recoverAndCheck();
    }

    private void buildUi() {
        root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL); root.setBackgroundColor(BG); root.setFitsSystemWindows(true);
        setContentView(root);
        TextView title = label("BiliCinema", 24, TEXT); title.setPadding(dp(16), dp(8), dp(16), dp(4)); root.addView(title);
        status = label("视频由本机播放，房间仅同步控制与聊天", 14, MUTED); status.setPadding(dp(16), 0, dp(16), dp(8)); status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); root.addView(status);
        playerContainer = new FrameLayout(this); playerContainer.setBackgroundColor(Color.BLACK); root.addView(playerContainer, new LinearLayout.LayoutParams(-1, playerHeight()));
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true); root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        content = new LinearLayout(this); content.setOrientation(LinearLayout.VERTICAL); content.setPadding(dp(16), dp(8), dp(16), dp(24)); scroll.addView(content);
        LinearLayout account = row(); account.addView(button("B站登录", this::login), weighted()); account.addView(button("刷新播放页", this::refreshPlayer), weighted()); account.addView(button("检查更新", () -> updates.check(true)), weighted()); content.addView(account);
        content.addView(label("影片 / 番剧链接或编号", 16, TEXT));
        mediaInput = input("例如 BV…、av… 或 https://…/ep…", "影片链接或编号"); content.addView(mediaInput);
        select = button("解析并打开影片", this::selectMedia); content.addView(select);
        controls = row(); play = button("播放", () -> playbackCommand("play")); pause = button("暂停", () -> playbackCommand("pause")); rate = button("倍速", this::chooseRate);
        controls.addView(play, weighted()); controls.addView(pause, weighted()); controls.addView(rate, weighted()); content.addView(controls);
        content.addView(button("跳转到指定进度", this::seekDialog));
        content.addView(button("恢复房间影片", this::restoreSelectedMedia));
        content.addView(label("一起看 · 最多 5 人", 20, TEXT));
        roomInput = input("房间码或邀请链接", "房间码或邀请链接"); content.addView(roomInput);
        LinearLayout roomButtons = row(); roomButtons.addView(button("创建房间", () -> connectRoom("", "")), weighted()); roomButtons.addView(button("加入房间", this::joinRoom), weighted()); content.addView(roomButtons);
        roomLabel = label("尚未加入房间", 16, TEXT); roomLabel.setTextIsSelectable(true); content.addView(roomLabel);
        members = label("房主选择影片，成员准备好后一起播放", 14, MUTED); content.addView(members);
        LinearLayout actions = row(); copy = button("复制邀请", this::copyInvite); copy.setEnabled(false); actions.addView(copy, weighted()); actions.addView(button("退出房间", this::leaveRoom), weighted()); actions.addView(button("恢复上次房间", this::restoreRoom), weighted()); content.addView(actions);
        content.addView(label("房间聊天", 20, TEXT));
        nickname = input("你的昵称（1–24字）", "聊天昵称"); nickname.setText(getPreferences(0).getString("nickname", "影友")); content.addView(nickname);
        chatLog = label("进入房间后即可聊天", 15, MUTED); chatLog.setTextIsSelectable(true); chatLog.setPadding(0, dp(8), 0, dp(8)); content.addView(chatLog);
        chatInput = input("发送给房间成员（最多500字）", "聊天内容"); chatInput.setSingleLine(false); chatInput.setMaxLines(4); content.addView(chatInput);
        content.addView(button("发送消息", this::sendChat));
        setHostControls();
    }

    @SuppressLint("SetJavaScriptEnabled")
    private void createPlayer() {
        try {
            player = new WebView(this);
            WebSettings settings = player.getSettings(); settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(true); settings.setMediaPlaybackRequiresUserGesture(false);
            settings.setAllowFileAccess(false); settings.setAllowContentAccess(false); settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
            settings.setUserAgentString("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            settings.setUseWideViewPort(true); settings.setLoadWithOverviewMode(true); settings.setSupportZoom(true); settings.setBuiltInZoomControls(true); settings.setDisplayZoomControls(false);
            CookieManager.getInstance().setAcceptCookie(true); CookieManager.getInstance().setAcceptThirdPartyCookies(player, true);
            secureBridge = false;
            if (WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) {
                secureBridge = true;
                WebViewCompat.addWebMessageListener(player, "BiliCinemaNative", new HashSet<>(Arrays.asList("https://www.bilibili.com", "https://m.bilibili.com")), (view, message, sourceOrigin, isMainFrame, replyProxy) -> {
                    if (!isMainFrame || !bridgeOrigin(sourceOrigin)) return;
                    try {
                        JSONObject event = new JSONObject(message.getData());
                        double position = event.optDouble("position", -1), speed = event.optDouble("rate", -1);
                        if (!Double.isFinite(position) || !Double.isFinite(speed) || position < 0 || position > 86400 || speed < .25 || speed > 3) return;
                        long token = event.optLong("generation", -1);
                        String kind = event.optString("event");
                        if (token != mediaSession.generation()) return;
                        if ("error".equals(kind) || "blocked".equals(kind)) { playerError("播放器未能开始播放，请在页面内点击播放或刷新后恢复房间影片"); return; }
                        boolean wasReady = mediaSession.ready(), wasBuffering = mediaSession.buffering();
                        MediaSession.Decision decision = mediaSession.observe(token, event.optString("url"), event.optLong("cid"), kind, event.optInt("readyState", -1), !inBackground);
                        publishReadiness(wasReady);
                        if (!roomCode.isEmpty() && wasBuffering != mediaSession.buffering()) commandFlag("buffering", "buffering", mediaSession.buffering());
                        if (decision == MediaSession.Decision.WAITING) { pauseLocal(); return; }
                        if (decision == MediaSession.Decision.CHANGED) { mediaChanged(event.optString("url")); return; }
                        if (decision == MediaSession.Decision.ACCEPT) handlePlayerEvent(kind, position, speed, event.optBoolean("playing"));
                    } catch (Exception error) { status("播放器状态无法识别，请刷新播放页"); }
                });
            } else status("请更新 Android System WebView 后使用房间同步；当前可单独观看");
            player.setWebViewClient(new WebViewClient() {
                @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                    if (!request.isForMainFrame()) return false;
                    Uri uri = request.getUrl();
                    if (trustedPage(uri)) { lastUrl = uri.toString(); return false; }
                    status("页面尝试打开外部应用。请在当前页面完成登录或播放"); return true;
                }
                @Override public void onPageStarted(WebView view, String url, android.graphics.Bitmap favicon) {
                    boolean wasReady = mediaSession.ready();
                    MediaSession.Decision change = mediaSession.navigating(url);
                    publishReadiness(wasReady);
                    if (change == MediaSession.Decision.CHANGED) mediaChanged(url);
                }
                @Override public void onPageFinished(WebView view, String url) {
                    if (!trustedPage(Uri.parse(url)) || !url.equals(view.getUrl())) return;
                    CookieManager.getInstance().flush(); installPlayerBridge();
                    if (!secureBridge && !roomCode.isEmpty()) { playerError("请更新 Android System WebView 后重连房间，当前播放器不支持安全同步"); return; }
                    status("页面已加载；若尚未播放，请在播放器内点击播放");
                    if (lastSnapshot != null) applySnapshot(lastSnapshot, true);
                }
                @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                    if (request.isForMainFrame()) playerError("页面连接失败，请检查网络、代理与系统 WebView，然后点击刷新播放页");
                }
                @Override public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse response) {
                    if (request.isForMainFrame()) playerError("页面服务返回 " + response.getStatusCode() + "，可以稍后刷新重试");
                }
                @Override public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
                    handler.cancel(); playerError("安全连接失败，请检查系统时间、网络代理或证书后重试");
                }
                @Override public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
                    playerContainer.removeView(view); view.destroy(); player = null;
                    playerError("播放器进程已退出，点击刷新播放页即可恢复"); return true;
                }
            });
            player.setWebChromeClient(new WebChromeClient() {
                @Override public void onShowCustomView(View view, CustomViewCallback callback) {
                    if (customView != null) { callback.onCustomViewHidden(); return; }
                    customView = view; customCallback = callback;
                    fullscreen = new FrameLayout(MainActivity.this); fullscreen.setBackgroundColor(Color.BLACK); fullscreen.addView(view, new FrameLayout.LayoutParams(-1, -1));
                    addContentView(fullscreen, new ViewGroup.LayoutParams(-1, -1));
                    fullscreen.setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
                }
                @Override public void onHideCustomView() { hideFullscreen(); }
            });
            playerContainer.removeAllViews(); playerContainer.addView(player, new FrameLayout.LayoutParams(-1, -1));
        } catch (RuntimeException error) { playerError("系统 WebView 不可用，请安装或更新 Android System WebView 后重试"); }
    }

    private static boolean trustedPage(Uri uri) {
        String host = uri.getHost();
        return "https".equals(uri.getScheme()) && host != null && (host.equals("bilibili.com") || host.endsWith(".bilibili.com"));
    }
    private static boolean bridgeOrigin(Uri uri) { return MediaSession.trustedOrigin(uri.getScheme(), uri.getHost(), uri.getPort()); }
    private void login() {
        boolean wasReady = mediaSession.ready(); mediaSession.fail(mediaSession.generation()); publishReadiness(wasReady); pauseLocal();
        if (player == null) createPlayer();
        if (player == null) return;
        lastUrl = "https://passport.bilibili.com/login"; player.loadUrl(lastUrl);
        status("在本机完成B站登录，可选择扫码或手机号；登录信息仅保存在本机");
    }
    private void refreshPlayer() { if (mediaSession.selected() != null) restoreSelectedMedia(); else { if (player == null) createPlayer(); if (player != null) player.loadUrl(lastUrl); } }
    private void selectMedia() {
        if (!roomCode.isEmpty() && !"host".equals(role)) { status("请由房主选择影片"); return; }
        String input = mediaInput.getText().toString();
        long request = mediaSession.requestSelection();
        String cookie = apiCookie();
        select.setEnabled(false); status("正在确认影片与分P…");
        mediaWork.execute(() -> {
            try {
                BilibiliMediaResolver.Resolved resolved = mediaResolver.input(input, cookie);
                main.post(() -> {
                    if (destroyed || !mediaSession.currentSelectionRequest(request)) return;
                    setHostControls();
                    try { if (roomCode.isEmpty()) loadResolved(mediaSession.begin(resolved.media), resolved); else if ("host".equals(role)) send(new JSONObject().put("type", "select").put("media", resolved.media)); }
                    catch (Exception error) { playerError("影片选择未完成，请重试"); }
                });
            } catch (Exception error) { main.post(() -> { if (!destroyed && mediaSession.currentSelectionRequest(request)) { setHostControls(); mediaInput.setError("解析失败，请检查网络、登录状态及影片链接后重试"); status("未能确认影片身份，尚未选择影片"); } }); }
        });
    }
    private void openMedia(JSONObject media) {
        boolean wasReady = mediaSession.ready(); long generation = mediaSession.begin(media); publishReadiness(wasReady);
        if (!roomCode.isEmpty()) commandFlag("ready", "ready", false);
        pauseLocal(); String cookie = apiCookie();
        status("正在核验房间影片与分P…");
        mediaWork.execute(() -> {
            try { BilibiliMediaResolver.Resolved resolved = mediaResolver.room(media, cookie); main.post(() -> { if (!destroyed) loadResolved(generation, resolved); }); }
            catch (Exception error) { main.post(() -> { if (!destroyed && mediaSession.fail(generation)) playerError("无法确认房间影片或分P，请检查网络及登录后恢复房间影片"); }); }
        });
    }
    private String apiCookie() { try { return CookieManager.getInstance().getCookie("https://api.bilibili.com/"); } catch (RuntimeException error) { status("本机登录暂不可用，请刷新登录"); return ""; } }
    private void loadResolved(long token, BilibiliMediaResolver.Resolved resolved) {
        if (!mediaSession.resolved(token, resolved)) return;
        if (player == null) createPlayer();
        if (player == null) return;
        lastUrl = mediaSession.pageUrl(); player.loadUrl(lastUrl);
        mediaInput.setText(lastUrl); status("正在加载影片，登录和会员权限由本机B站页面验证…");
        main.postDelayed(() -> { if (!destroyed && token == mediaSession.generation() && mediaSession.awaitingPreparation()) playerError("未能确认播放器身份或缓冲尚未就绪，请更新系统WebView或恢复房间影片后重试"); }, 30_000);
    }
    private void installPlayerBridge() {
        if (!secureBridge || mediaSession.pageUrl().isEmpty()) return;
        try (java.io.InputStream input = getAssets().open("player.js"); java.io.ByteArrayOutputStream data = new java.io.ByteArrayOutputStream()) {
            byte[] bytes = new byte[8192]; int count;
            while ((count = input.read(bytes)) != -1) data.write(bytes, 0, count);
            evaluate(data.toString("UTF-8").replace("__BC_GENERATION__", Long.toString(mediaSession.generation())));
        } catch (java.io.IOException error) { playerError("播放器脚本无法加载，请重新安装应用"); }
    }
    private void handlePlayerEvent(String event, double position, double speed, boolean playing) {
        if (destroyed) return;
        if ("error".equals(event) || "blocked".equals(event)) { playerError("播放器未能开始播放。请点击页面内播放；必要时登录B站或刷新后重试"); return; }
        if (roomCode.isEmpty()) return;
        try {
            switch (event) {
                case "ready": case "probe": case "waiting": case "stalled": case "playing": break;
                case "play": case "pause": case "seeked": case "ratechange":
                    if ("host".equals(role) && mediaSession.ready()) send(new JSONObject().put("type", "sync").put("media", mediaSession.selected()).put("positionSeconds", position).put("rate", speed).put("playing", playing));
                    else if (lastSnapshot != null) applySnapshot(lastSnapshot, true);
                    break;
                default: break;
            }
        } catch (Exception error) { status("播放器状态同步失败，请重新连接房间"); }
    }
    private void playerError(String message) {
        boolean wasReady = mediaSession.ready(); mediaSession.fail(mediaSession.generation()); publishReadiness(wasReady); pauseLocal();
        status(message);
        if (!roomCode.isEmpty()) commandFlag("ready", "ready", false);
    }
    private void publishReadiness(boolean previouslyReady) { if (!roomCode.isEmpty() && previouslyReady != mediaSession.ready()) { commandFlag("ready", "ready", mediaSession.ready()); if (mediaSession.ready()) { commandFlag("buffering", "buffering", false); if (lastSnapshot != null) applySnapshot(lastSnapshot, true); } } }
    private void pauseLocal() { evaluate("if(window.__bcPause)window.__bcPause();else{var v=document.querySelector('video');if(v)v.pause();}"); }
    private void mediaChanged(String actualUrl) {
        pauseLocal(); status("影片已变化，已暂停同步；请重新选择或恢复房间影片");
        if (actualUrl != null && trustedPage(Uri.parse(actualUrl))) mediaInput.setText(actualUrl);
        if (!roomCode.isEmpty() && "guest".equals(role)) restoreSelectedMedia();
    }
    private void restoreSelectedMedia() { JSONObject media = lastSnapshot == null ? mediaSession.selected() : lastSnapshot.optJSONObject("media"); if (media != null) openMedia(media); else status("房间尚未选择影片"); }
    private void playbackCommand(String type) {
        if (!roomCode.isEmpty()) { if ("host".equals(role)) command(type); else status("播放操作由房主控制"); return; }
        evaluate("(function(){var v=document.querySelector('video');if(v){" + ("play".equals(type) ? "var p=v.play();if(p&&p.catch)p.catch(function(){if(window.__bcEmit)window.__bcEmit('blocked',0,1,false);});" : "v.pause();") + "}})();");
    }
    private void seekDialog() {
        EditText seconds = input("输入秒数，0–86400", "播放进度秒数"); seconds.setInputType(android.text.InputType.TYPE_CLASS_NUMBER | android.text.InputType.TYPE_NUMBER_FLAG_DECIMAL);
        new AlertDialog.Builder(this).setTitle("跳转进度").setView(seconds).setNegativeButton("取消", null).setPositiveButton("跳转", (d, w) -> {
            try { double value = Double.parseDouble(seconds.getText().toString()); if (!Double.isFinite(value) || value < 0 || value > 86400) throw new IllegalArgumentException();
                if (roomCode.isEmpty()) evaluate("(function(){var v=document.querySelector('video');if(v)v.currentTime=" + value + ";})();");
                else if ("host".equals(role)) send(new JSONObject().put("type", "seek").put("positionSeconds", value));
                else status("进度由房主控制");
            } catch (Exception error) { status("请输入0–86400之间的有效秒数"); }
        }).show();
    }
    private void chooseRate() {
        String[] labels = {"0.5×", "0.75×", "1×", "1.25×", "1.5×", "2×"}; double[] values = {.5, .75, 1, 1.25, 1.5, 2};
        new AlertDialog.Builder(this).setTitle("播放倍速").setItems(labels, (d, which) -> {
            try { if (roomCode.isEmpty()) evaluate("(function(){var v=document.querySelector('video');if(v)v.playbackRate=" + values[which] + ";})();");
                else if ("host".equals(role)) send(new JSONObject().put("type", "rate").put("rate", values[which]));
            } catch (Exception error) { status("倍速设置失败"); }
        }).show();
    }
    private void joinRoom() {
        String input = roomInput.getText().toString().trim();
        if (input.contains("?")) input = Uri.parse(input).getQueryParameter("room");
        if (input == null || !input.matches("[A-Za-z0-9_-]{22}")) { roomInput.setError("请输入22位房间码或完整邀请链接"); return; }
        String saved = getPreferences(0).getString("room", "");
        connectRoom(input, input.equals(saved) ? getPreferences(0).getString("client", "") : "");
    }
    private void connectRoom(String room, String credential) {
        if (!roomCode.isEmpty()) { status("请先退出当前房间，再创建或加入其他房间"); return; }
        version = -1; role = ""; memberId = ""; lastSnapshot = null; rooms.connect(room, credential);
    }
    private void restoreRoom() {
        String saved = getPreferences(0).getString("room", ""), credential = getPreferences(0).getString("client", "");
        if (saved.isEmpty() || credential.isEmpty()) { status("没有可恢复的房间"); return; }
        connectRoom(saved, credential);
    }
    private void leaveRoom() {
        rooms.stop(true); clearRoom(); getPreferences(0).edit().remove("room").remove("client").apply(); status("已退出房间");
    }
    private void clearRoom() { roomCode = ""; role = ""; memberId = ""; version = -1; lastSnapshot = null; mediaSession.suspend(); roomLabel.setText("尚未加入房间"); members.setText("房间已断开，可重新创建或加入"); copy.setEnabled(false); setHostControls(); }
    private void copyInvite() {
        if (roomCode.isEmpty()) return;
        ((ClipboardManager) getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("BiliCinema邀请", RoomClient.ENDPOINT + "?room=" + roomCode)); status("邀请链接已复制");
    }
    private void sendChat() {
        String name = nickname.getText().toString().trim(), text = chatInput.getText().toString().trim();
        if (name.isEmpty() || name.length() > 24) { nickname.setError("昵称需要1–24字"); return; }
        if (text.isEmpty() || text.length() > 500) { chatInput.setError("消息需要1–500字"); return; }
        try { if (rooms.send(new JSONObject().put("type", "chat").put("nickname", name).put("text", text))) { chatInput.setText(""); getPreferences(0).edit().putString("nickname", name).apply(); } }
        catch (Exception error) { status("消息未发送，请重试"); }
    }
    @Override public void credentials(String room, String client) {
        roomCode = room; copy.setEnabled(true); roomLabel.setText(getString(R.string.room_label, room));
        getPreferences(0).edit().putString("room", room).putString("client", client).apply();
    }
    @Override public void message(JSONObject message) {
        switch (message.optString("type")) {
            case "welcome": role = message.optString("role"); memberId = message.optString("memberId"); mediaSession.suspend(); setHostControls(); version = -1; applySnapshot(message.optJSONObject("snapshot"), true); installPlayerBridge(); evaluate("if(window.__bcProbe)window.__bcProbe();"); break;
            case "snapshot": applySnapshot(message.optJSONObject("snapshot"), false); break;
            case "pong": long now = System.currentTimeMillis(), sent = message.optLong("clientTimeUnixMs", now); clockOffset = message.optLong("serverTimeUnixMs", now) - sent - (now - sent) / 2; break;
            case "chat": String line = message.optString("nickname") + ("host".equals(message.optString("role")) ? " · 房主" : "") + "：" + message.optString("text"); String old = chatLog.getText().toString(); chatLog.setText(getString(R.string.chat_append, old.length() > 12000 ? old.substring(old.length() - 8000) : old, line)); break;
            case "closed": clearRoom(); status("房间已结束"); getPreferences(0).edit().remove("room").remove("client").apply(); break;
            case "left": clearRoom(); status("已退出房间"); break;
            case "error": String code = message.optString("code"); if (RoomClient.terminal(code)) { clearRoom(); if (!"room_full".equals(code)) getPreferences(0).edit().remove("room").remove("client").apply(); } status(roomError(code)); break;
            case "transportLost": mediaSession.suspend(); pauseLocal(); break;
            case "reconnectEnded": clearRoom(); break;
            default: break;
        }
    }
    private void applySnapshot(JSONObject snapshot, boolean force) {
        if (snapshot == null) return;
        long revision = snapshot.optLong("version"); if (!force && revision <= version) return; version = Math.max(version, revision); lastSnapshot = snapshot;
        boolean synchronize = force || snapshot.optLong("syncRevision") != syncRevision;
        syncRevision = snapshot.optLong("syncRevision");
        JSONObject media = snapshot.optJSONObject("media"); boolean changed = media != null && !MediaIdentity.key(media).equals(mediaSession.key());
        if (media == null && mediaSession.selected() != null) { mediaSession.reset(); pauseLocal(); }
        if (changed) { openMedia(media); return; }
        StringBuilder participants = new StringBuilder("在线 ").append(snapshot.optInt("memberCount")).append(" / ").append(snapshot.optInt("capacity", 5)).append(" · ").append("host".equals(role) ? "你是房主" : "你是访客");
        if (snapshot.optBoolean("waitingForReady")) participants.append("\n正在等待所有成员准备 / 缓冲完成");
        JSONObject host = snapshot.optJSONObject("host"); if (host != null) appendMember(participants, host, "房主");
        JSONArray guests = snapshot.optJSONArray("guests"); if (guests != null) for (int i = 0; i < guests.length(); i++) appendMember(participants, guests.optJSONObject(i), "访客" + (i + 1)); members.setText(participants.toString());
        if (media == null || player == null || !mediaSession.ready() || !mediaSession.matchesUrl(player.getUrl())) return;
        double target = MediaIdentity.targetPosition(snapshot, System.currentTimeMillis(), clockOffset); double speed = snapshot.optDouble("rate", 1);
        if (!Double.isFinite(speed) || speed < .25 || speed > 3) return;
        evaluate("if(window.__bcApply)window.__bcApply(" + target + "," + speed + "," + snapshot.optBoolean("playing") + "," + synchronize + ");");
    }
    private void appendMember(StringBuilder text, JSONObject member, String label) {
        if (member == null) return;
        text.append("\n").append(label).append(memberId.equals(member.optString("memberId")) ? "（你）" : "").append(" · ").append(!member.optBoolean("online") ? "离线" : member.optBoolean("buffering") ? "缓冲中" : member.optBoolean("ready") ? "已准备" : "准备中");
    }
    private static String roomError(String code) {
        switch (code) { case "room_full": return "房间已满（最多5人）"; case "room_unavailable": case "room_not_found": case "room_expired": return "房间已结束或重连凭据失效，请输入房间码重新加入"; case "host_only": return "该操作仅房主可以执行"; case "rate_limited": return "操作过于频繁，请稍后重试"; case "invalid_client": case "invalid_client_id": case "client_expired": return "重连凭据失效，请输入房间码重新加入"; default: return "房间操作失败（" + code.replaceAll("[^a-z_]", "") + "）"; }
    }
    private void setHostControls() { boolean enabled = roomCode.isEmpty() || "host".equals(role); select.setEnabled(enabled); play.setEnabled(enabled); pause.setEnabled(enabled); rate.setEnabled(enabled); }
    private void send(JSONObject json) { rooms.send(json); }
    private void command(String type) { try { send(new JSONObject().put("type", type)); } catch (Exception error) { status("房间操作未完成"); } }
    private void commandFlag(String type, String key, boolean value) { try { send(new JSONObject().put("type", type).put(key, value)); } catch (Exception error) { status("房间状态未发送"); } }
    private void evaluate(String js) { if (player != null) try { player.evaluateJavascript(js, null); } catch (RuntimeException error) { status("播放器暂不可用，请刷新播放页"); } }
    @Override public void status(String value) { if (!destroyed) main.post(() -> { if (!destroyed) status.setText(value); }); }
    private void hideFullscreen() { if (customView == null) return; ((ViewGroup) fullscreen.getParent()).removeView(fullscreen); customView = null; if (customCallback != null) customCallback.onCustomViewHidden(); customCallback = null; }
    @Override public void onBackPressed() { if (customView != null) hideFullscreen(); else if (player != null && player.canGoBack()) player.goBack(); else new AlertDialog.Builder(this).setMessage("退出 BiliCinema？当前房间会断开。").setNegativeButton("继续观看", null).setPositiveButton("退出", (d, w) -> { rooms.stop(true); finish(); }).show(); }
    @Override public void onConfigurationChanged(Configuration config) { super.onConfigurationChanged(config); playerContainer.getLayoutParams().height = playerHeight(); playerContainer.requestLayout(); }
    @Override protected void onPause() { super.onPause(); inBackground = true; boolean wasReady = mediaSession.ready(); mediaSession.suspend(); publishReadiness(wasReady); if (player != null) { try { CookieManager.getInstance().flush(); player.onPause(); } catch (RuntimeException error) { status("播放器暂不可用，返回后可刷新恢复"); } } if (!roomCode.isEmpty()) { commandFlag("buffering", "buffering", true); pauseLocal(); } }
    @Override protected void onResume() { super.onResume(); inBackground = false; if (player != null) { player.onResume(); installPlayerBridge(); } if (updates != null) updates.installPending(); if (lastSnapshot != null) applySnapshot(lastSnapshot, true); if (!roomCode.isEmpty()) evaluate("if(window.__bcProbe)window.__bcProbe();"); }
    @Override protected void onSaveInstanceState(Bundle state) { super.onSaveInstanceState(state); state.putString("url", lastUrl); }
    @Override protected void onDestroy() { destroyed = true; main.removeCallbacksAndMessages(null); mediaWork.shutdownNow(); mediaResolver.close(); if (updates != null) updates.close(); if (rooms != null) rooms.destroy(); if (player != null) player.destroy(); super.onDestroy(); }
    private int playerHeight() { int width = getResources().getDisplayMetrics().widthPixels; int height = getResources().getDisplayMetrics().heightPixels; return Math.max(dp(180), Math.min(width * 9 / 16, height / 2)); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private TextView label(String text, int size, int color) { TextView view = new TextView(this); view.setText(text); view.setTextSize(size); view.setTextColor(color); view.setPadding(0, dp(8), 0, dp(8)); return view; }
    private EditText input(String hint, String description) { EditText view = new EditText(this); view.setTextColor(TEXT); view.setHintTextColor(MUTED); view.setTextSize(16); view.setHint(hint); view.setContentDescription(description); view.setSingleLine(true); view.setMinimumHeight(dp(48)); view.setPadding(dp(8), dp(8), dp(8), dp(8)); return view; }
    private Button button(String text, Runnable action) { Button view = new Button(this); view.setText(text); view.setTextSize(14); view.setAllCaps(false); view.setTextColor(TEXT); view.setMinimumHeight(dp(48)); view.setOnClickListener(v -> { try { action.run(); } catch (RuntimeException error) { status("操作未完成，请检查网络后重试"); } }); return view; }
    private LinearLayout row() { LinearLayout row = new LinearLayout(this); row.setOrientation(LinearLayout.HORIZONTAL); row.setPadding(0, dp(4), 0, dp(4)); return row; }
    private LinearLayout.LayoutParams weighted() { LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1); lp.setMargins(dp(2), 0, dp(2), 0); return lp; }
}
