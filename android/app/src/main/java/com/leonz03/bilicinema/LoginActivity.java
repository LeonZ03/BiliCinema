package com.leonz03.bilicinema;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
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
import android.widget.Toast;
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
    private TextView state, status, accountName, appearanceValue, updateValue, loginTabQr, loginTabWeb, heading;
    private ImageView qr, avatar;
    private LinearLayout accountCard;
    private TextView uid, avatarStatus;
    private FrameLayout official;
    private ProgressBar spinner;
    private Button primary, verify;
    private LinearLayout root, content, accountActions, qrActions;
    private ImageView settingsButton;
    private CinemaTheme theme;
    private UpdateManager updateManager;
    private WebView web;
    private Bitmap qrBitmap, avatarBitmap;
    private Mode mode = Mode.CHECKING;
    private boolean destroyed, paused, working, committing, clearingCookies, hideOfficialPage;
    private int generation;
    private String accountUid = "", accountId = "", accountNickname = "B站账号", accountStatus = "", lastStatus = "";
    private long challengeStarted;
    private Runnable poll, saveTimeout;
    private LoginCookieCommit.Ticket cookieTicket;

    @Override public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setTheme(CinemaTheme.materialActivityTheme(this));
        setResult(RESULT_CANCELED);
        getWindow().setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        theme = CinemaTheme.of(this, false);
        CinemaTheme.applySystemBars(this, false);
        buildUi();
        message("正在等待本机登录信息保存完成…");
        cookieTicket = COOKIE_COMMIT.whenIdle(() -> { if (!destroyed) checkSaved(); });
    }

    private void buildUi() {
        if (web != null && web.getParent() instanceof android.view.ViewGroup) ((android.view.ViewGroup) web.getParent()).removeView(web);
        root = column(); root.setBackgroundColor(theme.background);
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
        header.addView(iconButton(R.drawable.ic_back, "返回", this::goBack));
        heading = text("", 20, theme.text, true); heading.setPadding(dp(8), 0, 0, 0);
        header.addView(heading, new LinearLayout.LayoutParams(0, dp(56), 1));
        settingsButton = iconButton(R.drawable.ic_settings, "设置", this::showSettings);
        header.addView(settingsButton);
        root.addView(header, new LinearLayout.LayoutParams(-1, dp(56)));
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true); root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        content = column(); content.setPadding(0, dp(12), 0, dp(24)); scroll.addView(content);

        state = text("", 20, theme.text, true); state.setPadding(0, dp(4), 0, dp(20)); content.addView(state);
        accountCard = new LinearLayout(this); accountCard.setGravity(Gravity.CENTER_VERTICAL); accountCard.setPadding(0, dp(12), 0, dp(20));
        avatar = new ImageView(this); avatar.setScaleType(ImageView.ScaleType.CENTER_CROP);
        GradientDrawable circle = new GradientDrawable(); circle.setShape(GradientDrawable.OVAL); circle.setColor(Color.TRANSPARENT);
        avatar.setBackground(circle); avatar.setClipToOutline(true);
        accountCard.addView(avatar, new LinearLayout.LayoutParams(dp(56), dp(56)));
        LinearLayout profile = column(); profile.setPadding(dp(16), 0, dp(4), 0);
        accountName = text("B站账号", 17, theme.text, true); accountName.setMaxLines(1);
        accountName.setEllipsize(android.text.TextUtils.TruncateAt.END); profile.addView(accountName);
        LinearLayout idRow = new LinearLayout(this); idRow.setGravity(Gravity.CENTER_VERTICAL);
        uid = text("", 14, theme.muted, false); uid.setTextIsSelectable(true); idRow.addView(uid);
        idRow.addView(iconButton(R.drawable.ic_copy, "复制UID", this::copyUid)); profile.addView(idRow);
        avatarStatus = text("", 13, theme.success, false); profile.addView(avatarStatus);
        accountCard.addView(profile, new LinearLayout.LayoutParams(0, -2, 1));
        content.addView(accountCard);
        accountName.setText(accountNickname);
        if (avatarBitmap != null) avatar.setImageBitmap(avatarBitmap);
        else avatar.setImageResource(R.drawable.ic_account);
        uid.setText(accountUid);
        avatarStatus.setText(accountStatus);

        spinner = new ProgressBar(this); spinner.setContentDescription("正在处理登录");
        content.addView(spinner, centered(dp(40), dp(40)));
        LinearLayout loginTabs = new LinearLayout(this); loginTabs.setGravity(Gravity.CENTER_VERTICAL);
        loginTabQr = text("扫码登录", 15, theme.accent, true); loginTabQr.setGravity(Gravity.CENTER);
        loginTabWeb = text("网页登录", 15, theme.muted, false); loginTabWeb.setGravity(Gravity.CENTER);
        loginTabs.addView(loginTabQr, new LinearLayout.LayoutParams(0, dp(48), 1));
        loginTabs.addView(loginTabWeb, new LinearLayout.LayoutParams(0, dp(48), 1));
        loginTabQr.setOnClickListener(v -> { if (mode != Mode.QR) startQr(); });
        loginTabWeb.setOnClickListener(v -> { if (mode != Mode.WEB) startWeb(); });
        content.addView(loginTabs);
        qr = new ImageView(this); qr.setContentDescription("B站登录二维码，请用哔哩哔哩App扫码确认");
        qr.setScaleType(ImageView.ScaleType.FIT_CENTER); qr.setBackgroundColor(0xFFFFFFFF); qr.setPadding(dp(12), dp(12), dp(12), dp(12));
        if (qrBitmap != null) qr.setImageBitmap(qrBitmap);
        content.addView(qr, centered(dp(216), dp(216)));
        official = new FrameLayout(this); official.setBackgroundColor(theme.surface);
        if (web != null) official.addView(web, new FrameLayout.LayoutParams(-1, -1));
        content.addView(official, new LinearLayout.LayoutParams(-1, dp(380)));
        status = text("", 14, theme.muted, false);
        status.setText(lastStatus);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); status.setPadding(0, dp(12), 0, dp(12)); content.addView(status);
        qrActions = new LinearLayout(this); qrActions.setGravity(Gravity.CENTER); qrActions.setOrientation(LinearLayout.HORIZONTAL);
        qrActions.addView(compactAction(R.drawable.ic_copy, "复制二维码", "复制二维码图片", this::primaryAction),
                new LinearLayout.LayoutParams(0, dp(48), 1));
        qrActions.addView(compactAction(R.drawable.ic_refresh, "刷新", "刷新二维码", this::startQr),
                new LinearLayout.LayoutParams(0, dp(48), 1));
        LinearLayout.LayoutParams qrActionParams = new LinearLayout.LayoutParams(-1, dp(48));
        qrActionParams.topMargin = dp(8); content.addView(qrActions, qrActionParams);
        primary = button("重新检查登录状态", true, this::primaryAction); addButton(content, primary);
        verify = button("检查登录状态", false, () -> validateWeb(true)); addButton(content, verify);

        View divider = divider(); content.addView(divider, new LinearLayout.LayoutParams(-1, dp(1)));
        accountActions = column(); content.addView(accountActions);
        LinearLayout switchRow = actionRow("切换账号", null, R.drawable.ic_chevron_right, this::confirmSwitch);
        LinearLayout updateRow = actionRow("检查更新", BuildConfig.VERSION_NAME, R.drawable.ic_chevron_right, this::checkForUpdates);
        updateValue = (TextView) updateRow.getChildAt(1);
        LinearLayout appearanceRow = actionRow("外观", CinemaTheme.appearanceLabel(this), R.drawable.ic_chevron_right,
                () -> CinemaTheme.showAppearanceDialog(this, this::appearanceChanged));
        appearanceValue = (TextView) appearanceRow.getChildAt(1);
        accountActions.addView(switchRow); accountActions.addView(divider()); accountActions.addView(updateRow);
        accountActions.addView(divider()); accountActions.addView(appearanceRow); accountActions.addView(divider());
        int logoutColor = CinemaTheme.isDark(this) ? 0xFFFF8A80 : 0xFFB3261E;
        TextView logout = text("退出登录", 16, logoutColor, false); logout.setGravity(Gravity.CENTER_VERTICAL);
        logout.setMinHeight(dp(52)); logout.setOnClickListener(v -> confirmLogout()); accountActions.addView(logout);
        content.addOnLayoutChangeListener((view, l, t, r, b, oldL, oldT, oldR, oldB) -> {
            int size = Math.max(dp(120), Math.min(dp(216), r - l));
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
            if (account.loggedIn()) showLoggedIn(account);
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
            message("等待扫码"); schedulePoll();
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
                message(result.state == BilibiliLoginClient.ScanState.SCANNED ? "已扫码，请确认登录" : "等待扫码");
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
        try { LoginQrClipboard.publish(this, copiedImage); message("二维码图片已复制"); }
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
            if (confirmed) showLoggedIn(verified.account); else showError("登录信息未完整通过保存验证，请重新检查登录状态");
        });
    }

    private void showLoggedIn(BilibiliLoginClient.Account account) {
        replaceAttempt(); destroyWeb();
        mode = Mode.LOGGED_IN; setResult(RESULT_OK); message("本机登录已验证");
        accountNickname = account.nickname().isEmpty() ? "B站账号" : account.nickname();
        accountName.setText(accountNickname);
        accountId = account.uid(); accountUid = getString(R.string.account_uid, accountId); uid.setText(accountUid);
        avatar.setImageResource(R.drawable.ic_account); avatar.setContentDescription("B站账号头像，暂未加载");
        accountStatus = "已登录"; avatarStatus.setText(accountStatus);
        if (account.hasAvatar()) run(owner -> {
            byte[] bytes = owner.loadAvatar(account);
            BitmapFactory.Options bounds = new BitmapFactory.Options(); bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeByteArray(bytes, 0, bytes.length, bounds);
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0 || bounds.outWidth > 8192 || bounds.outHeight > 8192)
                throw new BilibiliLoginClient.Failure("头像暂未加载，可重新检查");
            BitmapFactory.Options options = new BitmapFactory.Options(); options.inSampleSize = 1;
            while (bounds.outWidth / options.inSampleSize > 256 || bounds.outHeight / options.inSampleSize > 256) options.inSampleSize *= 2;
            Bitmap image = BitmapFactory.decodeByteArray(bytes, 0, bytes.length, options);
            if (image == null) throw new BilibiliLoginClient.Failure("头像暂未加载，可重新检查");
            return image;
        }, image -> {
            avatarBitmap = image; avatar.setImageBitmap(image); avatar.setContentDescription("B站账号头像");
        }, ignored -> { avatar.setContentDescription("B站账号头像，暂未加载"); });
    }
    private void showError(String value) { stopPoll(); mode = Mode.ERROR; destroyWeb(); message(value); }
    private void message(String value) { lastStatus = value; status.setText(value); render(); }
    private void render() {
        if (state == null) return;
        boolean logged = mode == Mode.LOGGED_IN;
        heading.setText(logged ? "账号" : "登录");
        settingsButton.setVisibility(View.VISIBLE);
        accountCard.setVisibility(mode == Mode.LOGGED_IN ? View.VISIBLE : View.GONE);
        state.setVisibility(logged || mode == Mode.QR || mode == Mode.WEB ? View.GONE : View.VISIBLE);
        state.setText(mode == Mode.CHECKING ? "正在检查登录状态…" : mode == Mode.WEB ? "网页登录" : mode == Mode.ERROR ? "暂时无法确认登录" : "扫码登录");
        spinner.setVisibility((working && mode != Mode.LOGGED_IN) || committing ? View.VISIBLE : mode == Mode.QR ? View.INVISIBLE : View.GONE);
        loginTabQr.setVisibility(mode == Mode.QR || mode == Mode.WEB ? View.VISIBLE : View.GONE);
        loginTabWeb.setVisibility(mode == Mode.QR || mode == Mode.WEB ? View.VISIBLE : View.GONE);
        loginTabQr.setTextColor(mode == Mode.QR ? theme.accent : theme.muted);
        loginTabWeb.setTextColor(mode == Mode.WEB ? theme.accent : theme.muted);
        qr.setVisibility(mode == Mode.QR && qrBitmap != null && challenge != null ? View.VISIBLE : View.GONE);
        official.setVisibility(mode == Mode.WEB && web != null && !hideOfficialPage ? View.VISIBLE : View.GONE);
        status.setVisibility(logged ? View.GONE : View.VISIBLE);
        qrActions.setVisibility(mode == Mode.QR ? View.VISIBLE : View.GONE);
        primary.setVisibility(mode == Mode.ERROR ? View.VISIBLE : View.GONE);
        primary.setEnabled(!committing);
        verify.setVisibility(mode == Mode.WEB ? View.VISIBLE : View.GONE); verify.setEnabled(!working && !committing);
        accountActions.setVisibility(logged ? View.VISIBLE : View.GONE);
        if (appearanceValue != null) appearanceValue.setText(CinemaTheme.appearanceLabel(this));
    }

    private void appearanceChanged() {
        theme = CinemaTheme.of(this, false);
        setTheme(CinemaTheme.materialActivityTheme(this));
        CinemaTheme.applySystemBars(this, false);
        buildUi();
    }

    private void showSettings() {
        android.widget.PopupMenu menu = new android.widget.PopupMenu(this, settingsButton);
        menu.getMenu().add("外观").setOnMenuItemClickListener(item -> { CinemaTheme.showAppearanceDialog(this, this::appearanceChanged); return true; });
        menu.getMenu().add("检查更新").setOnMenuItemClickListener(item -> { checkForUpdates(); return true; });
        menu.show();
    }

    private void copyUid() {
        String value = accountId;
        if (value.isEmpty()) return;
        ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        clipboard.setPrimaryClip(ClipData.newPlainText("UID", value));
        Toast.makeText(this, "UID已复制", Toast.LENGTH_SHORT).show();
    }

    private void checkForUpdates() {
        if (updateManager != null) updateManager.close();
        updateManager = new UpdateManager(this, value -> {
            Toast.makeText(this, value, Toast.LENGTH_SHORT).show();
            message(value);
        });
        updateManager.check(true);
    }

    private void confirmSwitch() {
        new AlertDialog.Builder(this, CinemaTheme.materialDialogTheme(this)).setTitle("切换账号")
                .setMessage("将退出当前B站账号，然后重新登录。")
                .setNegativeButton("取消", null)
                .setPositiveButton("继续", (dialog, which) -> clearAccountCookies(false))
                .show();
    }

    private void confirmLogout() {
        new AlertDialog.Builder(this, CinemaTheme.materialDialogTheme(this)).setTitle("退出登录")
                .setMessage("退出后需要重新扫码或网页登录。")
                .setNegativeButton("取消", null)
                .setPositiveButton("退出登录", (dialog, which) -> clearAccountCookies(true))
                .show();
    }

    private void clearAccountCookies(boolean finishAfter) {
        if (committing || clearingCookies || destroyed) return;
        setResult(RESULT_CANCELED);
        clearingCookies = true;
        stopPoll(); destroyWeb(); replaceAttempt();
        mode = Mode.CHECKING; message("正在退出当前账号…");
        try {
            CookieManager manager = cookies == null ? CookieManager.getInstance() : cookies;
            cookies = manager;
            cookieTicket = COOKIE_COMMIT.clear(new CookieClearDestination(manager), confirmed -> {
                clearingCookies = false;
                if (destroyed) return;
                if (!confirmed) { showError("退出登录未完成，请重试"); return; }
                setResult(RESULT_LOGGED_OUT);
                if (finishAfter) finish();
                else startQr();
            });
        } catch (RuntimeException unavailable) {
            clearingCookies = false; showError("本机登录状态未能清除，请重试");
        }
    }

    private static final class CookieClearDestination implements LoginCookieCommit.Destination {
        private final CookieManager destination;
        CookieClearDestination(CookieManager destination) { this.destination = destination; }
        @Override public void write(String cookie, Consumer<Boolean> acknowledged) {
            throw new IllegalStateException("clear operation cannot write cookies");
        }
        @Override public void clear(Consumer<Boolean> acknowledged) {
            destination.removeAllCookies(acknowledged::accept);
        }
        @Override public boolean flushAndVerify() {
            destination.flush();
            String remaining = destination.getCookie(COOKIE_ORIGIN);
            return remaining == null || remaining.trim().isEmpty();
        }
        @Override public void close() { }
    }

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
            if (account.loggedIn()) showLoggedIn(account);
            else { setResult(RESULT_LOGGED_OUT); message(explicit ? "尚未登录，请完成官方登录后再检查" : "等待完成官方登录…"); }
        });
    }

    private interface Operation<T> { T run(BilibiliLoginClient owner) throws Exception; }
    private <T> void run(Operation<T> operation, Consumer<T> completed) {
        run(operation, completed, safe -> {
            if (mode == Mode.QR && challenge != null) invalidateQr(safe); else showError(safe);
        });
    }
    private <T> void run(Operation<T> operation, Consumer<T> completed, Consumer<String> failed) {
        if (destroyed || working || committing) return;
        final int current = generation;
        final BilibiliLoginClient owner = client;
        working = true; render();
        task = work.submit(() -> {
            try {
                T result = operation.run(owner);
                main.post(() -> {
                    if (destroyed || current != generation) {
                        if (result instanceof QrImage) ((QrImage) result).bitmap.recycle();
                        else if (result instanceof Bitmap) ((Bitmap) result).recycle();
                        return;
                    }
                    working = false; completed.accept(result); render();
                });
            } catch (CancellationException cancelled) {
                main.post(() -> { if (!destroyed && current == generation) { working = false; render(); } });
            } catch (Exception failure) {
                String safe = failure instanceof BilibiliLoginClient.Failure ? failure.getMessage() : "登录操作未完成，请检查网络后重试";
                main.post(() -> {
                    if (destroyed || current != generation) return;
                    working = false;
                    failed.accept(safe);
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
        clearAvatar();
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
        if (updateManager != null) updateManager.close();
        if (saveTimeout != null) main.removeCallbacks(saveTimeout);
        if (cookieTicket != null) cookieTicket.detach();
        release();
        clearAvatar();
        if (qrBitmap != null) { qr.setImageDrawable(null); qrBitmap.recycle(); qrBitmap = null; }
        super.onDestroy();
    }
    private void release() { if (task != null) task.cancel(true); if (client != null) client.close(); work.shutdown(); }
    private void clearAvatar() {
        avatar.setImageDrawable(null); uid.setText("");
        accountUid = ""; accountId = ""; accountNickname = "B站账号"; accountStatus = "";
        if (avatarBitmap != null) { avatarBitmap.recycle(); avatarBitmap = null; }
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private LinearLayout column() { LinearLayout value = new LinearLayout(this); value.setOrientation(LinearLayout.VERTICAL); return value; }
    private LinearLayout.LayoutParams centered(int width, int height) { LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(width, height); params.gravity = Gravity.CENTER_HORIZONTAL; return params; }
    private void addButton(LinearLayout parent, Button button) { LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2); params.topMargin = dp(8); parent.addView(button, params); }
    private TextView text(String value, int size, int color, boolean bold) { TextView label = new TextView(this); label.setText(value); label.setTextSize(size); label.setTextColor(color); if (bold) label.setTypeface(Typeface.DEFAULT, Typeface.BOLD); return label; }
    private ImageView iconButton(int resource, String description, Runnable action) {
        ImageView icon = new ImageView(this); icon.setImageResource(resource); icon.setColorFilter(theme.text);
        icon.setContentDescription(description); icon.setScaleType(ImageView.ScaleType.CENTER_INSIDE);
        icon.setPadding(dp(12), dp(12), dp(12), dp(12)); icon.setBackground(ripple(Color.TRANSPARENT));
        icon.setOnClickListener(view -> action.run());
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(dp(48), dp(48));
        icon.setLayoutParams(params); return icon;
    }
    private LinearLayout compactAction(int iconResource, String label, String description, Runnable action) {
        LinearLayout item = new LinearLayout(this); item.setGravity(Gravity.CENTER); item.setOrientation(LinearLayout.HORIZONTAL);
        item.setMinimumHeight(dp(48)); item.setBackground(ripple(theme.background)); item.setContentDescription(description);
        ImageView icon = new ImageView(this); icon.setImageResource(iconResource); icon.setColorFilter(theme.text);
        icon.setContentDescription(null); item.addView(icon, new LinearLayout.LayoutParams(dp(24), dp(24)));
        TextView caption = text(label, 14, theme.text, false); caption.setPadding(dp(8), 0, 0, 0); item.addView(caption);
        item.setOnClickListener(view -> action.run());
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, dp(48), 1);
        params.setMargins(dp(4), 0, dp(4), 0); item.setLayoutParams(params);
        return item;
    }
    private LinearLayout actionRow(String label, String value, int icon, Runnable action) {
        LinearLayout row = new LinearLayout(this); row.setGravity(Gravity.CENTER_VERTICAL); row.setMinimumHeight(dp(52));
        row.setBackground(ripple(theme.background)); row.setOnClickListener(view -> action.run());
        TextView title = text(label, 16, theme.text, false); title.setGravity(Gravity.CENTER_VERTICAL);
        row.addView(title, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        TextView trailing = text(value == null ? "" : value, 14, theme.muted, false); trailing.setGravity(Gravity.CENTER_VERTICAL);
        row.addView(trailing, new LinearLayout.LayoutParams(-2, LinearLayout.LayoutParams.WRAP_CONTENT));
        ImageView chevron = new ImageView(this); chevron.setImageResource(icon); chevron.setColorFilter(theme.muted);
        chevron.setContentDescription(null); chevron.setPadding(dp(12), dp(12), 0, dp(12));
        row.addView(chevron, new LinearLayout.LayoutParams(dp(36), dp(48)));
        return row;
    }
    private View divider() {
        View line = new View(this); line.setBackgroundColor(theme.border);
        line.setLayoutParams(new LinearLayout.LayoutParams(-1, dp(1)));
        return line;
    }
    private android.graphics.drawable.Drawable ripple(int color) {
        GradientDrawable fill = new GradientDrawable(); fill.setColor(color); fill.setCornerRadius(dp(8));
        return new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x22000000), fill, null);
    }
    private Button button(String label, boolean primary, Runnable action) {
        Button value = new Button(this); value.setText(label); value.setTextSize(16); value.setAllCaps(false);
        value.setMinHeight(dp(48)); value.setPadding(dp(12), dp(8), dp(12), dp(8));
        value.setTextColor(primary ? Color.WHITE : theme.accent);
        GradientDrawable fill = new GradientDrawable(); fill.setColor(primary ? theme.accent : theme.surface); fill.setCornerRadius(dp(10));
        fill.setStroke(dp(1), primary ? theme.accent : theme.border);
        value.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x22000000), fill, null));
        value.setOnClickListener(view -> action.run()); return value;
    }
}
