package com.leonz03.bilicinema;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.http.SslError;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.webkit.CookieManager;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.SslErrorHandler;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.function.Consumer;
import okhttp3.Cookie;
import okhttp3.HttpUrl;

/** Native account status and QR login; the official form exists only during explicit web sign-in. */
public final class LoginActivity extends Activity {
    static final int RESULT_LOGGED_OUT = RESULT_FIRST_USER;
    private static final String LOGIN_URL = "https://passport.bilibili.com/login";
    private static final String COOKIE_ORIGIN = "https://api.bilibili.com/";
    private static final int BG = Color.rgb(16, 18, 22), TEXT = Color.rgb(244, 246, 250);
    private static final int MUTED = Color.rgb(168, 178, 195), CYAN = Color.rgb(100, 201, 223);
    private static final Handler COOKIE_MAIN = new Handler(Looper.getMainLooper());
    private static final LoginCookieCommit COOKIE_COMMIT = new LoginCookieCommit(
        command -> COOKIE_MAIN.post(command), Executors.newSingleThreadExecutor());
    private enum Mode { CHECKING, QR, WEB, LOGGED_IN, ERROR }
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService work = Executors.newSingleThreadExecutor();
    private BilibiliLoginClient client;
    private CookieManager cookies;
    private BilibiliLoginClient.Challenge challenge;
    private LoginQrImageStore.Prepared copiedImage;
    private Future<?> task;
    private TextView state, status;
    private ImageView qr;
    private FrameLayout official;
    private ProgressBar spinner;
    private Button primary, refresh, alternative, verify;
    private WebView web;
    private Bitmap qrBitmap;
    private Mode mode = Mode.CHECKING;
    private boolean destroyed, paused, working, committing, hideOfficialPage;
    private int generation;
    private long challengeStarted;
    private Runnable poll, saveTimeout;
    private LoginCookieCommit.Ticket cookieTicket;

    @Override public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setResult(RESULT_CANCELED);
        getWindow().setStatusBarColor(BG); getWindow().setNavigationBarColor(BG);
        getWindow().setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        buildUi();
        message("正在等待本机登录信息保存完成…");
        cookieTicket = COOKIE_COMMIT.whenIdle(() -> { if (!destroyed) checkSaved(); });
    }

    private void buildUi() {
        LinearLayout root = column(); root.setBackgroundColor(BG);
        root.setPadding(dp(20), 0, dp(20), 0);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                view.setPadding(bars.left + dp(20), bars.top, bars.right + dp(20), Math.max(bars.bottom, insets.getInsets(WindowInsets.Type.ime()).bottom));
            } else view.setPadding(insets.getSystemWindowInsetLeft() + dp(20), insets.getSystemWindowInsetTop(),
                insets.getSystemWindowInsetRight() + dp(20), insets.getSystemWindowInsetBottom());
            return insets;
        });
        setContentView(root);
        LinearLayout header = new LinearLayout(this); header.setGravity(Gravity.CENTER_VERTICAL);
        header.addView(button("返回", false, this::goBack));
        TextView heading = text("B站登录", 20, TEXT, true); heading.setPadding(dp(12), 0, 0, 0); header.addView(heading);
        root.addView(header);
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true); root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout content = column(); content.setPadding(0, dp(16), 0, dp(20)); scroll.addView(content);
        state = text("正在检查登录状态…", 23, TEXT, true); content.addView(state);
        TextView local = text("登录信息仅保存在这台设备", 14, MUTED, false); local.setPadding(0, dp(8), 0, dp(16)); content.addView(local);
        spinner = new ProgressBar(this); spinner.setContentDescription("正在处理登录");
        content.addView(spinner, centered(dp(40), dp(40)));
        qr = new ImageView(this); qr.setContentDescription("B站登录二维码，请用哔哩哔哩App扫码确认");
        qr.setScaleType(ImageView.ScaleType.FIT_CENTER); qr.setBackgroundColor(Color.WHITE); qr.setPadding(dp(12), dp(12), dp(12), dp(12));
        content.addView(qr, centered(dp(248), dp(248)));
        official = new FrameLayout(this); official.setBackgroundColor(Color.WHITE);
        content.addView(official, new LinearLayout.LayoutParams(-1, dp(380)));
        status = text("正在验证本机登录状态…", 15, MUTED, false);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); status.setPadding(0, dp(16), 0, dp(16)); content.addView(status);
        primary = button("复制二维码图片", true, this::primaryAction); addButton(content, primary);
        refresh = button("刷新二维码", false, this::startQr); addButton(content, refresh);
        verify = button("检查登录状态", true, () -> validateWeb(true)); addButton(content, verify);
        alternative = button("使用网页登录", false, this::alternativeAction); addButton(content, alternative);
        content.addOnLayoutChangeListener((view, l, t, r, b, oldL, oldT, oldR, oldB) -> {
            int size = Math.max(dp(120), Math.min(dp(248), r - l));
            if (qr.getLayoutParams().width != size) qr.setLayoutParams(centered(size, size));
        });
        render();
    }

    private void checkSaved() {
        if (committing || destroyed) return;
        try { if (cookies == null) { cookies = CookieManager.getInstance(); cookies.setAcceptCookie(true); } }
        catch (RuntimeException unavailable) { showError("系统登录组件暂不可用，请更新 Android System WebView 后重试"); return; }
        replaceAttempt(); mode = Mode.CHECKING; message("正在验证本机登录状态…");
        run(owner -> { cookies.flush(); return owner.validateSaved(cookies.getCookie(COOKIE_ORIGIN)); }, account -> {
            if (account.loggedIn()) showLoggedIn();
            else { setResult(RESULT_LOGGED_OUT); startQr(); }
        });
    }
    private void startQr() {
        if (committing || destroyed || cookies == null) return;
        replaceAttempt(); destroyWeb(); mode = Mode.QR; message("正在生成登录二维码…");
        run(owner -> {
            BilibiliLoginClient.Challenge next = owner.generate();
            Bitmap image = Bitmap.createBitmap(LoginQrCode.pixels(next.address, 512), 512, 512, Bitmap.Config.ARGB_8888);
            try {
                ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                if (!image.compress(Bitmap.CompressFormat.PNG, 100, bytes)) throw new BilibiliLoginClient.Failure("二维码图片生成失败，请重试");
                LoginQrImageStore.Prepared prepared = new LoginQrImageStore(this).prepare(bytes.toByteArray(), System.currentTimeMillis() + 300_000);
                return new QrImage(next, image, prepared);
            } catch (Exception failure) { image.recycle(); throw failure; }
        }, result -> {
            challenge = result.challenge; qrBitmap = result.bitmap; copiedImage = result.prepared; qr.setImageBitmap(qrBitmap);
            challengeStarted = SystemClock.elapsedRealtime();
            message("用哔哩哔哩App扫码并确认，也可以复制图片后发送"); schedulePoll();
        });
    }
    private static final class QrImage {
        final BilibiliLoginClient.Challenge challenge; final Bitmap bitmap; final LoginQrImageStore.Prepared prepared;
        QrImage(BilibiliLoginClient.Challenge challenge, Bitmap bitmap, LoginQrImageStore.Prepared prepared) {
            this.challenge = challenge; this.bitmap = bitmap; this.prepared = prepared;
        }
    }
    private void schedulePoll() {
        if (paused || destroyed || working || committing || mode != Mode.QR || challenge == null) return;
        if (poll != null) main.removeCallbacks(poll);
        poll = () -> {
            if (SystemClock.elapsedRealtime() - challengeStarted >= 180_000) { invalidateQr("等待时间较长，请刷新二维码后再试"); return; }
            BilibiliLoginClient.Challenge expected = challenge;
            run(owner -> owner.poll(expected), result -> {
                if (result.state == BilibiliLoginClient.ScanState.EXPIRED) { invalidateQr("二维码已过期，请刷新后扫码"); return; }
                if (result.state == BilibiliLoginClient.ScanState.VERIFIED) { saveLogin(result); return; }
                message(result.state == BilibiliLoginClient.ScanState.SCANNED ? "已扫码，请在哔哩哔哩App中确认登录" : "等待扫码，请用哔哩哔哩App确认");
                schedulePoll();
            });
        };
        main.postDelayed(poll, 1500);
    }
    private void invalidateQr(String value) {
        stopPoll(); challenge = null; copiedImage = null; qr.setImageDrawable(null);
        if (qrBitmap != null) { qrBitmap.recycle(); qrBitmap = null; }
        message(value);
    }
    private void primaryAction() {
        if (mode == Mode.LOGGED_IN) { finish(); return; }
        if (mode == Mode.ERROR) { checkSaved(); return; }
        if (mode != Mode.QR || challenge == null || copiedImage == null || committing) return;
        if (SystemClock.elapsedRealtime() - challengeStarted >= 180_000 || copiedImage.expiresAt <= System.currentTimeMillis()) {
            invalidateQr("等待时间较长，请刷新二维码后再试"); return;
        }
        try { LoginQrClipboard.publish(this, copiedImage); message("二维码图片已复制，可切换到聊天应用粘贴"); }
        catch (Exception failure) { message("复制二维码图片失败，请重试"); }
    }

    private void saveLogin(BilibiliLoginClient.ScanResult verified) {
        stopPoll(); committing = true; mode = Mode.CHECKING; message("正在保存并验证登录状态…");
        final int current = generation;
        final CookieManager destination = cookies;
        final BilibiliLoginClient owner = client;
        client = null; // The process-owned batch now owns this verified attempt, including teardown.
        List<String> values = new ArrayList<>();
        for (Cookie cookie : verified.cookies) values.add(cookie.toString());
        saveTimeout = () -> {
            if (!destroyed && committing && current == generation)
                message("保存登录状态用时较长，仍在等待系统完成；可以返回后稍后检查");
        };
        main.postDelayed(saveTimeout, 30_000);
        cookieTicket = COOKIE_COMMIT.submit(values, new LoginCookieCommit.Destination() {
            @Override public void write(String value, Consumer<Boolean> acknowledged) {
                destination.setCookie("https://www.bilibili.com/", value, acknowledged::accept);
            }
            @Override public boolean flushAndVerify() throws Exception {
                destination.flush();
                return verified.account.sameAs(owner.validateSaved(destination.getCookie(COOKIE_ORIGIN)));
            }
            @Override public void close() { owner.close(); }
        }, confirmed -> {
            if (saveTimeout != null) main.removeCallbacks(saveTimeout);
            if (destroyed || current != generation) return;
            committing = false;
            if (confirmed) showLoggedIn(); else showError("登录信息未完整通过保存验证，请重新检查登录状态");
        });
    }

    private void showLoggedIn() {
        stopPoll(); destroyWeb(); challenge = null; copiedImage = null;
        if (qrBitmap != null) { qr.setImageDrawable(null); qrBitmap.recycle(); qrBitmap = null; }
        mode = Mode.LOGGED_IN; setResult(RESULT_OK); message("本机登录已验证");
    }
    private void showError(String value) { stopPoll(); mode = Mode.ERROR; destroyWeb(); message(value); }
    private void message(String value) { status.setText(value); render(); }
    private void render() {
        if (state == null) return;
        state.setText(mode == Mode.LOGGED_IN ? "已登录B站" : mode == Mode.CHECKING ? "正在检查登录状态…" : mode == Mode.WEB ? "网页登录" : mode == Mode.ERROR ? "暂时无法确认登录" : "扫码登录");
        spinner.setVisibility(working || committing ? View.VISIBLE : mode == Mode.QR ? View.INVISIBLE : View.GONE);
        qr.setVisibility(mode == Mode.QR && qrBitmap != null && challenge != null ? View.VISIBLE : View.GONE);
        official.setVisibility(mode == Mode.WEB && web != null && !hideOfficialPage ? View.VISIBLE : View.GONE);
        primary.setVisibility(mode == Mode.WEB || mode == Mode.CHECKING ? View.GONE : View.VISIBLE);
        primary.setText(mode == Mode.LOGGED_IN ? "返回观影" : mode == Mode.ERROR ? "重新检查登录状态" : "复制二维码图片");
        primary.setEnabled(!committing && (mode != Mode.QR || challenge != null));
        refresh.setVisibility(mode == Mode.QR ? View.VISIBLE : View.GONE); refresh.setEnabled(!committing);
        verify.setVisibility(mode == Mode.WEB || mode == Mode.LOGGED_IN ? View.VISIBLE : View.GONE); verify.setEnabled(!working && !committing);
        alternative.setVisibility(mode == Mode.QR || mode == Mode.WEB ? View.VISIBLE : View.GONE);
        alternative.setText(mode == Mode.WEB ? "返回扫码登录" : "使用网页登录"); alternative.setEnabled(!committing);
    }

    private void alternativeAction() { if (mode == Mode.WEB) startQr(); else startWeb(); }
    private void startWeb() {
        if (committing || destroyed) return;
        replaceAttempt(); mode = Mode.WEB; hideOfficialPage = false;
        message("在B站官方页面完成登录，成功后会自动收起网页"); createWeb(); render();
    }
    private static boolean loginForm(HttpUrl url) {
        return BilibiliLoginClient.trusted(url) && (url.host().equals("passport.bilibili.com") || url.host().equals("account.bilibili.com"));
    }
    private boolean currentWeb(WebView view) { return !destroyed && mode == Mode.WEB && web == view; }
    @SuppressLint("SetJavaScriptEnabled")
    private void createWeb() {
        try {
            web = new WebView(this);
            WebSettings settings = web.getSettings();
            settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(true);
            settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
            settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
            settings.setSupportZoom(true); settings.setBuiltInZoomControls(true); settings.setDisplayZoomControls(false);
            cookies.setAcceptThirdPartyCookies(web, true);
            web.setWebViewClient(new WebViewClient() {
                @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                    if (!currentWeb(view)) return true;
                    if (!request.isForMainFrame()) return false;
                    HttpUrl target = HttpUrl.parse(request.getUrl().toString());
                    if (!BilibiliLoginClient.trusted(target)) { message("请在B站官方页面完成登录"); return true; }
                    if (!loginForm(target)) { hideOfficialPage = true; official.setVisibility(View.GONE); validateWeb(false); return true; }
                    return false;
                }
                @Override public void onPageStarted(WebView view, String url, Bitmap favicon) {
                    if (!currentWeb(view)) { view.stopLoading(); return; }
                    if (!loginForm(HttpUrl.parse(url))) {
                        view.stopLoading(); hideOfficialPage = true; official.setVisibility(View.GONE); validateWeb(false);
                    }
                }
                @Override public void onPageFinished(WebView view, String url) {
                    if (!currentWeb(view)) return;
                    validateWeb(false);
                }
                @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                    if (currentWeb(view) && request.isForMainFrame()) message("官方登录页连接失败，可重新进入网页登录或改用扫码");
                }
                @Override public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse response) {
                    if (currentWeb(view) && request.isForMainFrame()) message("官方登录页暂不可用，可重新进入或改用扫码");
                }
                @Override public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
                    handler.cancel(); if (currentWeb(view)) message("安全连接失败，请检查设备时间或网络后重试");
                }
                @Override public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
                    boolean current = currentWeb(view);
                    if (web == view) web = null;
                    official.removeView(view); view.destroy();
                    if (current) showError("官方登录页已退出，请重新检查或改用扫码"); return true;
                }
            });
            official.removeAllViews(); official.addView(web, new FrameLayout.LayoutParams(-1, -1)); web.loadUrl(LOGIN_URL);
        } catch (RuntimeException unavailable) { destroyWeb(); showError("系统 WebView 暂不可用，请更新后重试"); }
    }
    private void validateWeb(boolean explicit) {
        if (committing || working || destroyed || cookies == null) return;
        if (mode == Mode.LOGGED_IN) { checkSaved(); return; }
        if (mode != Mode.WEB) return;
        run(owner -> { cookies.flush(); return owner.validateSaved(cookies.getCookie(COOKIE_ORIGIN)); }, account -> {
            if (account.loggedIn()) showLoggedIn();
            else { setResult(RESULT_LOGGED_OUT); message(explicit ? "尚未登录，请完成官方登录后再检查" : "等待完成官方登录…"); }
        });
    }

    private interface Operation<T> { T run(BilibiliLoginClient owner) throws Exception; }
    private <T> void run(Operation<T> operation, Consumer<T> completed) {
        if (destroyed || working || committing) return;
        final int current = generation;
        final BilibiliLoginClient owner = client;
        working = true; render();
        task = work.submit(() -> {
            try {
                T result = operation.run(owner);
                main.post(() -> {
                    if (destroyed || current != generation) { if (result instanceof QrImage) ((QrImage) result).bitmap.recycle(); return; }
                    working = false; completed.accept(result); render();
                });
            } catch (CancellationException cancelled) {
                main.post(() -> { if (!destroyed && current == generation) { working = false; render(); } });
            } catch (Exception failure) {
                String safe = failure instanceof BilibiliLoginClient.Failure ? failure.getMessage() : "登录操作未完成，请检查网络后重试";
                main.post(() -> {
                    if (destroyed || current != generation) return;
                    working = false;
                    if (mode == Mode.QR && challenge != null) invalidateQr(safe); else showError(safe);
                });
            }
        });
    }
    private void replaceAttempt() {
        generation++; stopPoll(); working = false;
        if (task != null) task.cancel(true);
        if (client != null) client.close(); client = new BilibiliLoginClient();
        challenge = null; copiedImage = null;
        qr.setImageDrawable(null); if (qrBitmap != null) { qrBitmap.recycle(); qrBitmap = null; }
    }
    private void stopPoll() { if (poll != null) { main.removeCallbacks(poll); poll = null; } }
    private void destroyWeb() {
        if (web == null) return;
        WebView previous = web; web = null; official.removeView(previous); previous.stopLoading(); previous.destroy();
    }
    private void goBack() { finish(); }
    @Override public void onBackPressed() { goBack(); }
    @Override protected void onPause() { paused = true; stopPoll(); super.onPause(); }
    @Override protected void onResume() { super.onResume(); paused = false; if (mode == Mode.QR) schedulePoll(); else if (mode == Mode.WEB) validateWeb(false); }
    @Override protected void onDestroy() {
        destroyed = true; stopPoll(); destroyWeb();
        if (saveTimeout != null) main.removeCallbacks(saveTimeout);
        if (cookieTicket != null) cookieTicket.detach();
        release();
        if (qrBitmap != null) { qr.setImageDrawable(null); qrBitmap.recycle(); qrBitmap = null; }
        super.onDestroy();
    }
    private void release() { if (task != null) task.cancel(true); if (client != null) client.close(); work.shutdown(); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private LinearLayout column() { LinearLayout value = new LinearLayout(this); value.setOrientation(LinearLayout.VERTICAL); return value; }
    private LinearLayout.LayoutParams centered(int width, int height) { LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(width, height); params.gravity = Gravity.CENTER_HORIZONTAL; return params; }
    private void addButton(LinearLayout parent, Button button) { LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2); params.topMargin = dp(8); parent.addView(button, params); }
    private TextView text(String value, int size, int color, boolean bold) { TextView label = new TextView(this); label.setText(value); label.setTextSize(size); label.setTextColor(color); if (bold) label.setTypeface(Typeface.DEFAULT, Typeface.BOLD); return label; }
    private Button button(String label, boolean primary, Runnable action) {
        Button value = new Button(this); value.setText(label); value.setTextSize(16); value.setAllCaps(false);
        value.setMinHeight(dp(48)); value.setPadding(dp(12), dp(8), dp(12), dp(8)); value.setTextColor(primary ? BG : CYAN);
        GradientDrawable fill = new GradientDrawable(); fill.setColor(primary ? CYAN : BG); fill.setCornerRadius(dp(12));
        value.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x4464C9DF), fill, null));
        value.setOnClickListener(view -> action.run()); return value;
    }
}
