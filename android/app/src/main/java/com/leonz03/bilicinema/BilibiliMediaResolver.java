package com.leonz03.bilicinema;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.IOException;
import java.net.URI;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import okhttp3.OkHttpClient;
import okhttp3.Call;
import okhttp3.CookieJar;
import okhttp3.Request;
import okhttp3.Response;

/** Resolves the exact public episode/CID locally before any room selection or readiness. */
final class BilibiliMediaResolver {
    static final String USER_AGENT = "Mozilla/5.0 (Linux; Android) BiliCinema/" + BuildConfig.VERSION_NAME;
    static final class Resolved {
        final JSONObject media;
        final String pageUrl;
        final String title;
        Resolved(JSONObject media, String pageUrl) { this(media, pageUrl, ""); }
        Resolved(JSONObject media, String pageUrl, String title) {
            this.media = media; this.pageUrl = pageUrl; this.title = title;
        }
    }
    private static final class Operation { boolean cancelled; Call call; }
    private final Object gate = new Object();
    private Operation current;
    private boolean closed;
    private final OkHttpClient http;
    private final HttpResourceLifecycle network;
    BilibiliMediaResolver() { this(new OkHttpClient()); }
    BilibiliMediaResolver(OkHttpClient transport) {
        http = transport.newBuilder().cookieJar(CookieJar.NO_COOKIES).connectTimeout(15, TimeUnit.SECONDS)
                .readTimeout(20, TimeUnit.SECONDS).callTimeout(30, TimeUnit.SECONDS)
                .followRedirects(false).followSslRedirects(false).build();
        network = new HttpResourceLifecycle(http);
    }
    Resolved input(String input, String cookie) throws Exception {
        Operation operation = begin();
        try {
            JSONObject identity = MediaIdentity.parse(input);
            Matcher page = Pattern.compile("[?&]p=(\\d+)").matcher(input);
            int number = page.find() ? Integer.parseInt(page.group(1)) : 1;
            if (number < 1) throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
            Resolved result = resolve(identity, number, cookie, operation);
            check(operation);
            return result;
        } catch (PlaybackFailure | CancellationException failure) { throw failure; }
        catch (Exception exception) { throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
        finally { end(operation); }
    }
    Resolved room(JSONObject identity, String cookie) throws Exception {
        Operation operation = begin();
        try {
            if (!identity.has("episodeId") && identity.optLong("cid") <= 0) throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
            Resolved result = resolve(new JSONObject(identity.toString()), 0, cookie, operation);
            check(operation);
            return result;
        } catch (PlaybackFailure | CancellationException failure) { throw failure; }
        catch (Exception exception) { throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
        finally { end(operation); }
    }
    private Resolved resolve(JSONObject identity, int page, String cookie, Operation operation) throws Exception {
        if (identity.optLong("episodeId") > 0) {
            JSONObject result = fetch("https://api.bilibili.com/pgc/view/web/season?ep_id=" + identity.getLong("episodeId"), cookie, operation);
            PlaybackSourceParser.requireSuccess(result);
            return episode(result, identity);
        }
        String query;
        if (identity.optString("bvid").matches("BV[0-9A-Za-z]{10}")) query = "bvid=" + identity.getString("bvid");
        else if (identity.optLong("aid") > 0) query = "aid=" + identity.getLong("aid");
        else throw new IOException("缺少有效影片编号");
        JSONObject result = fetch("https://api.bilibili.com/x/web-interface/view?" + query, cookie, operation);
        PlaybackSourceParser.requireSuccess(result);
        return video(result, identity, page);
    }
    PlaybackSource playable(Resolved resolved, String cookie) throws Exception {
        Operation operation = begin();
        try {
            JSONObject media = new JSONObject(resolved.media.toString());
            // The complete, previously resolved identity is the only input to playurl.
            identity(media.optLong("aid"), media.optString("bvid"), media.optLong("cid"));
            Resolved exact = new Resolved(media, resolved.pageUrl, resolved.title);
            JSONObject response;
            if (media.optLong("episodeId") > 0) {
                response = fetch("https://api.bilibili.com/pgc/player/web/v2/playurl?cid=" + media.getLong("cid")
                        + "&ep_id=" + media.getLong("episodeId") + "&bvid=" + media.getString("bvid")
                        + "&qn=125&fourk=1&fnver=0&fnval=4048", cookie, operation);
            } else {
                Map<String, String> parameters = new LinkedHashMap<>();
                parameters.put("bvid", media.getString("bvid")); parameters.put("cid", Long.toString(media.getLong("cid")));
                parameters.put("qn", "125"); parameters.put("fourk", "1"); parameters.put("fnver", "0"); parameters.put("fnval", "4048");
                response = signedPlayUrl(parameters, cookie, operation);
                // Match Core's single WBI refresh on -403; do not retry entitlement failures indefinitely.
                if (response.optInt("code") == -403) response = signedPlayUrl(parameters, cookie, operation);
            }
            check(operation);
            PlaybackSource source = PlaybackSourceParser.parse(response, exact);
            check(operation);
            return source;
        } catch (PlaybackFailure | CancellationException failure) { throw failure; }
        catch (Exception exception) { throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
        finally { end(operation); }
    }

    private JSONObject signedPlayUrl(Map<String, String> parameters, String cookie, Operation operation) throws Exception {
        JSONObject nav = fetch("https://api.bilibili.com/x/web-interface/nav", cookie, operation);
        int code = nav.optInt("code", Integer.MIN_VALUE);
        // This endpoint alone exposes public keys in a valid anonymous -101 response.
        if (code != 0 && code != -101) PlaybackSourceParser.requireSuccess(nav);
        JSONObject keys = nav.optJSONObject("data") == null ? null : nav.getJSONObject("data").optJSONObject("wbi_img");
        if (keys == null) throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        String query = WbiSigner.sign(parameters, WbiSigner.extractKey(keys.optString("img_url")),
                WbiSigner.extractKey(keys.optString("sub_url")), System.currentTimeMillis() / 1000);
        return fetch("https://api.bilibili.com/x/player/wbi/playurl?" + query, cookie, operation);
    }

    private JSONObject fetch(String url, String cookie, Operation operation) throws Exception {
        URI target = new URI(url);
        if (!"https".equals(target.getScheme()) || !"api.bilibili.com".equals(target.getHost()) || target.getPort() != -1) {
            throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        }
        if (cookie != null) for (int i = 0; i < cookie.length(); i++) {
            if (cookie.charAt(i) < 0x20 || cookie.charAt(i) > 0x7e) throw new PlaybackFailure(PlaybackFailure.Reason.NEEDS_LOGIN);
        }
        Request.Builder builder = new Request.Builder().url(url).header("Referer", "https://www.bilibili.com/").header("User-Agent", USER_AGENT);
        if (cookie != null && !cookie.isEmpty()) builder.header("Cookie", cookie);
        Call call = http.newCall(builder.build());
        synchronized (gate) { checkLocked(operation); operation.call = call; }
        try (Response response = call.execute()) {
            check(operation);
            if (!response.isSuccessful() || response.body() == null) {
                if (response.code() == 401) throw new PlaybackFailure(PlaybackFailure.Reason.NEEDS_LOGIN);
                if (response.code() == 403) throw new PlaybackFailure(PlaybackFailure.Reason.PERMISSION_DENIED);
                throw new PlaybackFailure(PlaybackFailure.Reason.NETWORK_RETRYABLE);
            }
            try (java.io.InputStream stream = response.body().byteStream(); java.io.ByteArrayOutputStream data = new java.io.ByteArrayOutputStream()) {
                byte[] buffer = new byte[8192]; int count;
                while ((count = stream.read(buffer)) != -1) { check(operation); if (data.size() + count > 4 * 1024 * 1024) throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); data.write(buffer, 0, count); }
                JSONObject root = new JSONObject(data.toString("UTF-8"));
                check(operation);
                return root;
            }
        } catch (PlaybackFailure failure) { check(operation); throw failure; }
        catch (IOException exception) { check(operation); throw new PlaybackFailure(PlaybackFailure.Reason.NETWORK_RETRYABLE); }
        catch (org.json.JSONException exception) { check(operation); throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
        finally { synchronized (gate) { if (operation.call == call) operation.call = null; } }
    }

    private Operation begin() {
        synchronized (gate) {
            if (closed) throw new CancellationException("Media resolver closed");
            cancelLocked();
            current = new Operation();
            return current;
        }
    }
    private void check(Operation operation) { synchronized (gate) { checkLocked(operation); } }
    private void checkLocked(Operation operation) {
        if (closed || operation.cancelled || current != operation || Thread.currentThread().isInterrupted()) throw new CancellationException("Media resolution cancelled");
    }
    private void end(Operation operation) { synchronized (gate) { if (current == operation) current = null; } }
    private void cancelLocked() {
        if (current != null) {
            current.cancelled = true;
            Call call = current.call;
            current = null;
            if (call != null) network.cancel(call::cancel);
        }
    }
    void cancel() { synchronized (gate) { cancelLocked(); } }
    static Resolved video(JSONObject response, JSONObject expected, int pageNumber) throws Exception {
        JSONObject data = response.getJSONObject("data");
        JSONArray pages = data.getJSONArray("pages"); JSONObject chosen = null;
        for (int i = 0; i < pages.length(); i++) {
            JSONObject page = pages.getJSONObject(i);
            if (expected.optLong("cid") > 0 ? page.optLong("cid") == expected.optLong("cid") : page.optInt("page") == pageNumber) { chosen = page; break; }
        }
        if (chosen == null || chosen.optLong("cid") <= 0 || chosen.optInt("page") <= 0) throw new IOException("无法确认该分P，请房主重新解析影片");
        JSONObject media = identity(data.optLong("aid"), data.optString("bvid"), chosen.getLong("cid"));
        validate(expected, media);
        String title = data.optString("title");
        if (pages.length() > 1 && !chosen.optString("part").isEmpty()) title += " · " + chosen.optString("part");
        return new Resolved(media, "https://www.bilibili.com/video/" + media.getString("bvid") + "?p=" + chosen.getInt("page"), title);
    }
    static Resolved episode(JSONObject response, JSONObject expected) throws Exception {
        JSONObject result = response.getJSONObject("result");
        long id = expected.getLong("episodeId");
        JSONObject episode = findEpisode(result.optJSONArray("episodes"), id);
        JSONArray sections = result.optJSONArray("section");
        if (episode == null && sections != null) for (int i = 0; i < sections.length() && episode == null; i++) episode = findEpisode(sections.getJSONObject(i).optJSONArray("episodes"), id);
        if (episode == null) throw new IOException("无法确认该番剧集数，请重新选择");
        JSONObject media = identity(episode.optLong("aid"), episode.optString("bvid"), episode.optLong("cid")).put("episodeId", id);
        validate(expected, media);
        return new Resolved(media, "https://www.bilibili.com/bangumi/play/ep" + id, result.optString("title") + " " + episode.optString("long_title", episode.optString("title")));
    }
    private static JSONObject findEpisode(JSONArray episodes, long expected) throws Exception {
        if (episodes != null) for (int i = 0; i < episodes.length(); i++) { JSONObject ep = episodes.getJSONObject(i); if (ep.optLong("id", ep.optLong("ep_id")) == expected) return ep; }
        return null;
    }
    private static JSONObject identity(long aid, String bvid, long cid) throws Exception {
        if (aid <= 0 || cid <= 0 || !bvid.matches("BV[0-9A-Za-z]{10}")) throw new IOException("影片信息不完整，请稍后重试");
        return new JSONObject().put("aid", aid).put("bvid", bvid).put("cid", cid);
    }
    private static void validate(JSONObject expected, JSONObject actual) throws Exception {
        for (String key : new String[]{"episodeId", "aid", "bvid", "cid"}) if (expected.has(key) && (actual.opt(key) == null || !expected.get(key).toString().equals(actual.opt(key).toString()))) throw new IOException("房间影片身份与B站返回不一致，已停止准备");
    }
    void close() {
        synchronized (gate) { if (closed) return; closed = true; cancelLocked(); }
        network.close();
    }
}
