package com.leonz03.bilicinema;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.Rect;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.net.http.SslError;
import android.os.Build;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
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
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.IOException;
import java.util.concurrent.TimeUnit;
import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import org.json.JSONObject;

/** Official sign-in is isolated from the native home/player and uses the device WebView cookie jar. */
public final class LoginActivity extends Activity {
    private static final String LOGIN_URL = "https://passport.bilibili.com/login";
    private static final String NAV_URL = "https://api.bilibili.com/x/web-interface/nav";
    private static final int BG = Color.rgb(16, 18, 22), TEXT = Color.rgb(244, 246, 250);
    private static final int MUTED = Color.rgb(168, 178, 195), CYAN = Color.rgb(100, 201, 223);
    private final OkHttpClient http = new OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(15, TimeUnit.SECONDS).callTimeout(25, TimeUnit.SECONDS)
            .followRedirects(false).followSslRedirects(false).build();
    private final HttpResourceLifecycle network = new HttpResourceLifecycle(http);
    private FrameLayout official;
    private LinearLayout introduction, footer;
    private TextView status;
    private Button verify, reload;
    private WebView web;
    private Call validation;
    private boolean destroyed;
    private int validationGeneration;

    @Override public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setResult(RESULT_CANCELED);
        getWindow().setStatusBarColor(BG); getWindow().setNavigationBarColor(BG);
        getWindow().setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        LinearLayout root = column(); root.setBackgroundColor(BG);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                int bottom = Math.max(bars.bottom, insets.getInsets(WindowInsets.Type.ime()).bottom);
                view.setPadding(bars.left + dp(20), bars.top, bars.right + dp(20), bottom);
                keyboardLayout(insets.isVisible(WindowInsets.Type.ime()));
            } else view.setPadding(insets.getSystemWindowInsetLeft() + dp(20), insets.getSystemWindowInsetTop(),
                    insets.getSystemWindowInsetRight() + dp(20), insets.getSystemWindowInsetBottom());
            return insets;
        });
        root.getViewTreeObserver().addOnGlobalLayoutListener(() -> {
            if (Build.VERSION.SDK_INT >= 30) return;
            Rect visible = new Rect(); root.getWindowVisibleDisplayFrame(visible);
            keyboardLayout(root.getRootView().getHeight() - visible.bottom > dp(160));
        });
        setContentView(root);
        LinearLayout header = new LinearLayout(this); header.setGravity(Gravity.CENTER_VERTICAL);
        Button back = button("返回", false, this::finish); header.addView(back);
        TextView heading = text("登录 B 站", 20, TEXT, true); heading.setPadding(dp(12), 0, 0, 0); header.addView(heading);
        root.addView(header);
        introduction = column(); root.addView(introduction);
        TextView title = text("使用自己的播放权限", 22, TEXT, true); title.setPadding(0, dp(12), 0, dp(8)); introduction.addView(title);
        TextView subtitle = text("登录信息仅保存在这台设备", 15, MUTED, false); subtitle.setPadding(0, 0, 0, dp(12)); introduction.addView(subtitle);
        TextView label = text("B 站官方登录", 15, TEXT, true); label.setPadding(0, dp(8), 0, dp(8)); introduction.addView(label);
        official = new FrameLayout(this); official.setBackgroundColor(Color.WHITE);
        root.addView(official, new LinearLayout.LayoutParams(-1, 0, 1));
        footer = column(); root.addView(footer);
        status = text("在官方网页完成登录，再点击下方按钮验证", 14, MUTED, false);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); status.setPadding(0, dp(8), 0, dp(8)); footer.addView(status);
        reload = button("重新加载官方登录页", false, this::retry); reload.setVisibility(View.GONE); footer.addView(reload);
        verify = button("我已完成登录", true, this::validateLogin); footer.addView(verify);
        footer.addView(button("稍后登录", false, this::finish));
        createWeb();
    }

    @SuppressLint("SetJavaScriptEnabled")
    private void createWeb() {
        if (destroyed) return;
        try {
            web = new WebView(this);
            WebSettings settings = web.getSettings();
            settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(true);
            settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
            settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
            settings.setSupportZoom(true); settings.setBuiltInZoomControls(true); settings.setDisplayZoomControls(false);
            // Keep the platform mobile user agent so the official form fits a phone.
            CookieManager cookies = CookieManager.getInstance(); cookies.setAcceptCookie(true);
            cookies.setAcceptThirdPartyCookies(web, true);
            web.setWebViewClient(new WebViewClient() {
                @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                    if (!request.isForMainFrame() || officialOrigin(request.getUrl())) return false;
                    message("请在 B 站官方网页完成登录；外部应用链接未打开", false); return true;
                }
                @Override public void onPageFinished(WebView view, String url) {
                    if (destroyed || web != view) return;
                    if (!officialOrigin(Uri.parse(url))) {
                        view.stopLoading(); message("登录页地址无法确认，请重新加载官方登录页", true); return;
                    }
                    CookieManager.getInstance().flush();
                }
                @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                    if (request.isForMainFrame()) message("官方登录页连接失败，请检查网络后重试", true);
                }
                @Override public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse response) {
                    if (request.isForMainFrame()) message("官方登录页暂时不可用，请稍后重试", true);
                }
                @Override public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
                    handler.cancel(); message("安全连接失败，请检查设备时间或网络后重试", true);
                }
                @Override public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
                    if (web == view) web = null;
                    official.removeView(view); view.destroy();
                    message("官方登录页进程已退出，可重新加载继续登录", true); return true;
                }
            });
            official.removeAllViews(); official.addView(web, new FrameLayout.LayoutParams(-1, -1));
            web.loadUrl(LOGIN_URL);
        } catch (RuntimeException exception) {
            if (web != null) { official.removeView(web); web.destroy(); web = null; }
            message("系统 WebView 暂不可用，请更新后重新加载", true);
        }
    }

    private static boolean officialOrigin(Uri uri) {
        String host = uri.getHost();
        return "https".equalsIgnoreCase(uri.getScheme()) && (uri.getPort() == -1 || uri.getPort() == 443)
                && uri.getUserInfo() == null && host != null
                && (host.equalsIgnoreCase("bilibili.com") || host.toLowerCase(java.util.Locale.ROOT).endsWith(".bilibili.com"));
    }

    private void retry() {
        if (destroyed) return;
        reload.setVisibility(View.GONE); message("正在加载 B 站官方登录页…", false);
        if (web == null) createWeb(); else web.loadUrl(LOGIN_URL);
    }

    private void validateLogin() {
        if (validation != null || destroyed) return;
        final String cookie;
        try {
            CookieManager.getInstance().flush();
            cookie = CookieManager.getInstance().getCookie("https://api.bilibili.com/");
        } catch (RuntimeException exception) {
            message("无法读取本机登录状态，请重新加载后重试", true); return;
        }
        if (cookie == null || cookie.trim().isEmpty()) {
            message("尚未检测到登录，请先在官方网页完成登录", false); return;
        }
        verify.setEnabled(false); message("正在验证本机登录状态…", false);
        final int generation = ++validationGeneration;
        Request request = new Request.Builder().url(NAV_URL).header("Cookie", cookie)
                .header("Referer", "https://www.bilibili.com/").header("User-Agent", "BiliCinema Android").build();
        validation = http.newCall(request);
        validation.enqueue(new Callback() {
            @Override public void onFailure(Call call, IOException exception) {
                runOnUiThread(() -> validationFinished(generation, false, "登录验证连接失败，请检查网络后重试"));
            }
            @Override public void onResponse(Call call, Response response) {
                boolean loggedIn = false;
                try (Response result = response) {
                    if (result.isSuccessful() && result.body() != null) {
                        JSONObject data = new JSONObject(result.body().string());
                        JSONObject account = data.optJSONObject("data");
                        loggedIn = data.optInt("code", -1) == 0 && account != null && account.optBoolean("isLogin", false);
                    }
                } catch (Exception exception) {
                    runOnUiThread(() -> validationFinished(generation, false, "无法确认登录结果，请稍后重新验证")); return;
                }
                final boolean confirmed = loggedIn;
                runOnUiThread(() -> validationFinished(generation, confirmed, "登录尚未完成或已过期，请在官方网页登录后再验证"));
            }
        });
    }

    private void validationFinished(int generation, boolean success, String failureMessage) {
        if (destroyed || isFinishing() || generation != validationGeneration) return;
        validation = null; verify.setEnabled(true);
        if (!success) { message(failureMessage, false); return; }
        // MainActivity re-reads the local jar. No credential is attached to the result Intent.
        CookieManager.getInstance().flush(); setResult(RESULT_OK); finish();
    }

    private void message(String value, boolean retry) {
        if (destroyed) return;
        status.setText(value); if (retry) reload.setVisibility(View.VISIBLE);
    }

    private void keyboardLayout(boolean visible) {
        int visibility = visible ? View.GONE : View.VISIBLE;
        if (introduction != null && introduction.getVisibility() != visibility) introduction.setVisibility(visibility);
        if (footer != null && footer.getVisibility() != visibility) footer.setVisibility(visibility);
    }

    @Override public void onBackPressed() {
        // Back always cancels this dedicated flow. Existing local cookies are deliberately retained.
        setResult(RESULT_CANCELED); finish();
    }

    @Override protected void onDestroy() {
        destroyed = true; validationGeneration++;
        validation = null;
        network.close();
        if (web != null) { ((ViewGroup) web.getParent()).removeView(web); web.stopLoading(); web.destroy(); web = null; }
        super.onDestroy();
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private LinearLayout column() { LinearLayout value = new LinearLayout(this); value.setOrientation(LinearLayout.VERTICAL); return value; }
    private TextView text(String value, int size, int color, boolean bold) {
        TextView label = new TextView(this); label.setText(value); label.setTextSize(size); label.setTextColor(color);
        if (bold) label.setTypeface(Typeface.DEFAULT, Typeface.BOLD); return label;
    }
    private Button button(String label, boolean primary, Runnable action) {
        Button value = new Button(this); value.setText(label); value.setTextSize(16); value.setAllCaps(false);
        value.setMinHeight(dp(48)); value.setPadding(dp(12), dp(8), dp(12), dp(8)); value.setTextColor(primary ? BG : CYAN);
        GradientDrawable fill = new GradientDrawable(); fill.setColor(primary ? CYAN : BG); fill.setCornerRadius(dp(12));
        value.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x4464C9DF), fill, null));
        value.setOnClickListener(view -> action.run()); return value;
    }
}
