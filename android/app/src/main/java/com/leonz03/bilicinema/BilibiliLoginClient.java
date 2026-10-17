package com.leonz03.bilicinema;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.TimeUnit;
import okhttp3.Call;
import okhttp3.Cookie;
import okhttp3.CookieJar;
import okhttp3.HttpUrl;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import org.json.JSONObject;

/** One isolated QR attempt. Only a verified parent-domain session can enter the device cookie jar. */
final class BilibiliLoginClient implements AutoCloseable {
    private static final String GENERATE = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    private static final String POLL = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";
    private static final String NAV = "https://api.bilibili.com/x/web-interface/nav";
    private static final String USER_AGENT = "Mozilla/5.0 (Linux; Android) BiliCinema/" + BuildConfig.VERSION_NAME;
    enum ScanState { WAITING, SCANNED, EXPIRED, VERIFIED }
    static final class Account {
        private final long id;
        private Account(long id) { this.id = id; }
        boolean loggedIn() { return id > 0; }
        boolean sameAs(Account other) { return other != null && loggedIn() && id == other.id; }
    }
    static final class Challenge {
        final String address;
        private final String key;
        private Challenge(String address, String key) { this.address = address; this.key = key; }
    }
    static final class ScanResult {
        final ScanState state;
        final List<Cookie> cookies;
        final Account account;
        private ScanResult(ScanState state, List<Cookie> cookies, Account account) {
            this.state = state; this.cookies = cookies; this.account = account;
        }
        private static ScanResult pending(ScanState state) { return new ScanResult(state, Collections.emptyList(), null); }
    }
    static final class Failure extends Exception {
        Failure(String safeMessage) { super(safeMessage); }
    }
    private static final class Reply {
        final String body, location;
        final int status;
        Reply(String body, String location, int status) { this.body = body; this.location = location; this.status = status; }
    }
    private final Object gate = new Object();
    private final Call.Factory calls;
    private final HttpResourceLifecycle network;
    private final Map<String, Cookie> cookies = new LinkedHashMap<>();
    private boolean closed;
    private Challenge challenge;
    private Call activeCall;

    BilibiliLoginClient() { this(new OkHttpClient()); }
    BilibiliLoginClient(OkHttpClient transport) {
        OkHttpClient http = transport.newBuilder().cookieJar(CookieJar.NO_COOKIES).connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(15, TimeUnit.SECONDS).callTimeout(25, TimeUnit.SECONDS)
            .followRedirects(false).followSslRedirects(false).build();
        calls = http;
        network = new HttpResourceLifecycle(http);
    }

    /** Keeps deterministic transport/cancellation fixtures outside the production cookie jar. */
    BilibiliLoginClient(Call.Factory calls, HttpResourceLifecycle network) {
        this.calls = java.util.Objects.requireNonNull(calls);
        this.network = java.util.Objects.requireNonNull(network);
    }

    Challenge generate() throws Failure {
        JSONObject data = payload(fetch(HttpUrl.get(GENERATE), true, null, true));
        String address = data.optString("url"), key = data.optString("qrcode_key");
        HttpUrl url = HttpUrl.parse(address);
        if (!trusted(url) || key.isEmpty() || key.length() > 1024 || key.chars().anyMatch(Character::isWhitespace)
                || key.chars().anyMatch(Character::isISOControl)) throw new Failure("二维码响应无法确认，请刷新重试");
        String embedded = url.queryParameter("qrcode_key");
        if (embedded != null && !embedded.equals(key)) throw new Failure("二维码响应无法确认，请刷新重试");
        synchronized (gate) { checkOpen(); challenge = new Challenge(address, key); return challenge; }
    }

    ScanResult poll(Challenge expected) throws Failure {
        synchronized (gate) { checkOpen(); if (expected == null || expected != challenge) throw new CancellationException(); }
        HttpUrl url = HttpUrl.get(POLL).newBuilder().addQueryParameter("qrcode_key", expected.key).build();
        JSONObject data = payload(fetch(url, true, null, true));
        int code = data.optInt("code", -1);
        if (code == 86101) return ScanResult.pending(ScanState.WAITING);
        if (code == 86090) return ScanResult.pending(ScanState.SCANNED);
        if (code == 86038) return ScanResult.pending(ScanState.EXPIRED);
        if (code != 0) throw new Failure("扫码状态无法确认，请刷新二维码重试");
        if (!hasParentSession()) callback(data.optString("url"));
        List<Cookie> parent = parentCookies();
        if (!hasParentSession()) throw new Failure("未收到有效登录信息，请重新扫码");
        Account account = validateSaved(header(HttpUrl.get(NAV), parent));
        if (!account.loggedIn()) throw new Failure("登录尚未通过验证，请重新扫码");
        return new ScanResult(ScanState.VERIFIED, parent, account);
    }

    Account validateSaved(String cookie) throws Failure {
        synchronized (gate) { checkOpen(); }
        if (cookie == null || cookie.trim().isEmpty()) return new Account(0);
        if (cookie.indexOf('\r') >= 0 || cookie.indexOf('\n') >= 0) throw new Failure("本机登录信息无法读取，请重新登录");
        Reply reply = fetch(HttpUrl.get(NAV), false, cookie, true);
        if (reply.status < 200 || reply.status >= 300) throw new Failure("暂时无法验证登录，请检查网络后重试");
        try {
            JSONObject envelope = new JSONObject(reply.body);
            int code = envelope.optInt("code", -1);
            if (code == -101) return new Account(0);
            JSONObject data = envelope.optJSONObject("data");
            if (code != 0 || data == null || !data.has("isLogin")) throw new Failure("登录状态响应无法确认，请稍后重试");
            if (!data.optBoolean("isLogin", false)) return new Account(0);
            long id = data.optLong("mid", 0);
            if (id <= 0) throw new Failure("登录状态响应无法确认，请稍后重试");
            return new Account(id);
        } catch (Failure failure) { throw failure; }
        catch (Exception invalid) { throw new Failure("登录状态响应无法确认，请稍后重试"); }
    }

    private void callback(String address) throws Failure {
        HttpUrl landing = HttpUrl.parse(address);
        if (!trusted(landing) && !terminal(landing)) throw new Failure("登录回调地址无法确认，请重新扫码");
        HttpUrl next = landing;
        for (int redirect = 0; !terminal(next) && redirect < 5; redirect++) {
            Reply response = fetch(next, true, null, false);
            if (hasParentSession()) return;
            if (response.status < 300 || response.status >= 400 || response.location == null) break;
            HttpUrl target = next.resolve(response.location);
            // The unrelated biligame cross-domain terminal does not receive this session.
            if (terminal(target)) break;
            if (!trusted(target)) throw new Failure("登录回调地址无法确认，请重新扫码");
            next = target;
        }
        if (hasParentSession()) return;
        // Response entries include expired/deleted cookies: a legacy query must never resurrect
        // one. The same parent-domain/root key owns both response precedence and query fallback.
        for (String name : new String[]{"SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5"}) {
            try {
                synchronized (gate) {
                    checkOpen();
                    if (cookies.containsKey(name + "|bilibili.com|/")) continue;
                    String value = landing.queryParameter(name);
                    if (value == null || value.isEmpty()) continue;
                    Cookie.Builder builder = new Cookie.Builder().name(name).value(value).domain("bilibili.com").path("/").secure();
                    if (name.equals("SESSDATA")) builder.httpOnly();
                    Cookie cookie = builder.build();
                    cookies.put(cookieKey(cookie), cookie);
                }
            } catch (IllegalArgumentException invalid) { throw new Failure("登录信息格式无法确认，请重新扫码"); }
        }
    }

    static boolean trusted(HttpUrl url) {
        return url != null && url.isHttps() && url.port() == 443 && url.username().isEmpty() && url.password().isEmpty()
            && (url.host().equals("bilibili.com") || url.host().endsWith(".bilibili.com"));
    }
    private static boolean terminal(HttpUrl url) {
        return url != null && url.isHttps() && url.port() == 443 && url.username().isEmpty() && url.password().isEmpty()
            && url.host().equals("passport.biligame.com") && url.encodedPath().equals("/x/passport-login/web/crossDomain");
    }
    private static String cookieKey(Cookie cookie) { return cookie.name() + "|" + cookie.domain() + "|" + cookie.path(); }
    private JSONObject payload(Reply reply) throws Failure {
        try {
            JSONObject envelope = new JSONObject(reply.body);
            JSONObject data = envelope.optJSONObject("data");
            if (reply.status < 200 || reply.status >= 300 || envelope.optInt("code", -1) != 0 || data == null)
                throw new Failure("B站登录服务暂不可用，请稍后重试");
            return data;
        } catch (Failure failure) { throw failure; }
        catch (Exception invalid) { throw new Failure("登录服务响应无法确认，请稍后重试"); }
    }
    private Reply fetch(HttpUrl url, boolean collect, String suppliedCookie, boolean readBody) throws Failure {
        final Call call;
        synchronized (gate) {
            checkOpen();
            if (activeCall != null) throw new Failure("登录操作正在进行，请稍后重试");
            Request.Builder request = new Request.Builder().url(url).header("User-Agent", USER_AGENT).header("Referer", "https://www.bilibili.com/");
            String cookie = collect ? header(url, new ArrayList<>(cookies.values())) : suppliedCookie;
            if (cookie != null && !cookie.isEmpty()) request.header("Cookie", cookie);
            call = calls.newCall(request.build());
            activeCall = call;
        }
        try (Response response = call.execute()) {
            synchronized (gate) {
                checkOpen();
                if (collect) for (Cookie cookie : Cookie.parseAll(url, response.headers()))
                    cookies.put(cookieKey(cookie), cookie);
            }
            String body = "";
            if (readBody && response.body() != null) {
                okio.BufferedSource source = response.body().source();
                source.request(1_048_577);
                if (source.getBuffer().size() > 1_048_576) throw new Failure("登录服务响应无法确认，请稍后重试");
                body = source.readString(StandardCharsets.UTF_8);
            }
            synchronized (gate) { checkOpen(); }
            return new Reply(body, response.header("Location"), response.code());
        } catch (IOException error) {
            synchronized (gate) { checkOpen(); }
            throw new Failure("登录连接失败，请检查网络后重试");
        } finally {
            synchronized (gate) { if (activeCall == call) activeCall = null; }
        }
    }
    private static String header(HttpUrl url, List<Cookie> values) {
        StringBuilder result = new StringBuilder();
        for (Cookie cookie : values) if (cookie.expiresAt() > System.currentTimeMillis() && cookie.matches(url)) {
            if (result.length() > 0) result.append("; ");
            result.append(cookie.name()).append('=').append(cookie.value());
        }
        return result.toString();
    }
    private List<Cookie> parentCookies() {
        synchronized (gate) {
            checkOpen(); List<Cookie> result = new ArrayList<>();
            for (Cookie cookie : cookies.values()) if (!cookie.hostOnly() && cookie.domain().equals("bilibili.com")
                    && cookie.path().equals("/") && cookie.expiresAt() > System.currentTimeMillis()) result.add(cookie);
            return result;
        }
    }
    private boolean hasParentSession() { return parentCookies().stream().anyMatch(cookie -> cookie.name().equals("SESSDATA") && !cookie.value().isEmpty()); }
    private void checkOpen() { if (closed || Thread.currentThread().isInterrupted()) throw new CancellationException(); }
    @Override public void close() {
        final Call cancelled;
        synchronized (gate) {
            if (closed) return;
            closed = true; challenge = null; cookies.clear(); cancelled = activeCall; activeCall = null;
        }
        // Dispatcher.cancelAll is a snapshot; this concrete Call may not have entered execute yet.
        if (cancelled != null) network.cancel(cancelled::cancel);
        network.close();
    }
}
