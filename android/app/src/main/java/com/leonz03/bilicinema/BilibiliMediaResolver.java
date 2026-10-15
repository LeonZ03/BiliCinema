package com.leonz03.bilicinema;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.IOException;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

/** Resolves the exact public episode/CID locally before any room selection or readiness. */
final class BilibiliMediaResolver {
    static final class Resolved {
        final JSONObject media;
        final String pageUrl;
        Resolved(JSONObject media, String pageUrl) { this.media = media; this.pageUrl = pageUrl; }
    }
    private final OkHttpClient http = new OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS).readTimeout(20, TimeUnit.SECONDS).callTimeout(30, TimeUnit.SECONDS).followRedirects(false).followSslRedirects(false).build();
    Resolved input(String input, String cookie) throws Exception {
        JSONObject identity = MediaIdentity.parse(input);
        Matcher page = Pattern.compile("[?&]p=(\\d+)").matcher(input);
        int number = page.find() ? Integer.parseInt(page.group(1)) : 1;
        if (number < 1) throw new IOException("分P编号无效");
        return resolve(identity, number, cookie);
    }
    Resolved room(JSONObject identity, String cookie) throws Exception {
        if (!identity.has("episodeId") && identity.optLong("cid") <= 0) throw new IOException("房间影片缺少精确分P编号，请房主重新解析选片");
        return resolve(identity, 0, cookie);
    }
    private Resolved resolve(JSONObject identity, int page, String cookie) throws Exception {
        if (identity.optLong("episodeId") > 0) {
            JSONObject result = fetch("https://api.bilibili.com/pgc/view/web/season?ep_id=" + identity.getLong("episodeId"), cookie);
            return episode(result, identity);
        }
        String query;
        if (identity.optString("bvid").matches("BV[0-9A-Za-z]{10}")) query = "bvid=" + identity.getString("bvid");
        else if (identity.optLong("aid") > 0) query = "aid=" + identity.getLong("aid");
        else throw new IOException("缺少有效影片编号");
        return video(fetch("https://api.bilibili.com/x/web-interface/view?" + query, cookie), identity, page);
    }
    private JSONObject fetch(String url, String cookie) throws Exception {
        Request.Builder builder = new Request.Builder().url(url).header("Referer", "https://www.bilibili.com/").header("User-Agent", "Mozilla/5.0 BiliCinema/" + BuildConfig.VERSION_NAME);
        if (cookie != null && !cookie.isEmpty()) builder.header("Cookie", cookie);
        try (Response response = http.newCall(builder.build()).execute()) {
            if (!response.isSuccessful() || response.body() == null) throw new IOException("B站影片信息连接失败，请检查网络后重试");
            try (java.io.InputStream stream = response.body().byteStream(); java.io.ByteArrayOutputStream data = new java.io.ByteArrayOutputStream()) {
                byte[] buffer = new byte[8192]; int count;
                while ((count = stream.read(buffer)) != -1) { if (data.size() + count > 4 * 1024 * 1024) throw new IOException("影片信息过大"); data.write(buffer, 0, count); }
                JSONObject root = new JSONObject(data.toString("UTF-8"));
                if (root.optInt("code", -1) != 0) throw new IOException("B站暂未允许读取此影片，请登录后重试");
                return root;
            }
        }
    }
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
        return new Resolved(media, "https://www.bilibili.com/video/" + media.getString("bvid") + "?p=" + chosen.getInt("page"));
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
        return new Resolved(media, "https://www.bilibili.com/bangumi/play/ep" + id);
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
        for (String key : new String[]{"episodeId", "aid", "bvid", "cid"}) if (expected.has(key) && !expected.get(key).toString().equals(actual.opt(key).toString())) throw new IOException("房间影片身份与B站返回不一致，已停止准备");
    }
    void close() { http.dispatcher().cancelAll(); http.connectionPool().evictAll(); }
}
