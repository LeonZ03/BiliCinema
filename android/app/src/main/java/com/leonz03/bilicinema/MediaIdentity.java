package com.leonz03.bilicinema;

import org.json.JSONObject;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Only public identifiers cross the room boundary. URLs and cookies stay on the device. */
public final class MediaIdentity {
    private static final Pattern BV = Pattern.compile("BV[0-9A-Za-z]{10}");
    private static final Pattern EP = Pattern.compile("(?:^|[/\\s])ep(\\d+)", Pattern.CASE_INSENSITIVE);
    private static final Pattern AV = Pattern.compile("(?:^|[/\\s])av(\\d+)", Pattern.CASE_INSENSITIVE);
    private MediaIdentity() { }
    public static JSONObject parse(String input) throws Exception {
        String value = input.trim();
        Matcher bv = BV.matcher(value), ep = EP.matcher(value), av = AV.matcher(value);
        if (bv.find()) return new JSONObject().put("bvid", bv.group());
        if (ep.find()) return positive("episodeId", ep.group(1));
        if (av.find()) return positive("aid", av.group(1));
        throw new IllegalArgumentException("请输入 BV 号、av 号或包含 ep 号的番剧链接");
    }
    private static JSONObject positive(String key, String value) throws Exception {
        long number = Long.parseLong(value);
        if (number <= 0) throw new IllegalArgumentException("影片编号无效");
        return new JSONObject().put(key, number);
    }
    public static String key(JSONObject media) {
        return media == null ? "" : media.optLong("episodeId") + ":" + media.optLong("aid") + ":" + media.optString("bvid") + ":" + media.optLong("cid");
    }
    public static double targetPosition(JSONObject snapshot, long nowMs, long serverOffsetMs) {
        double base = snapshot.optDouble("positionSeconds", 0);
        double elapsed = Math.max(0, (nowMs + serverOffsetMs - snapshot.optLong("serverTimeUnixMs", nowMs)) / 1000.0);
        return Math.max(0, Math.min(86400, base + (snapshot.optBoolean("playing") ? elapsed * snapshot.optDouble("rate", 1) : 0)));
    }
}
