package com.leonz03.bilicinema;

import org.json.JSONObject;
import java.net.URI;

/** The single owner of expected identity, resolution generation and published readiness. UI thread only. */
final class MediaSession {
    enum State { EMPTY, RESOLVING, LOADING, VERIFIED, CHANGED, ERROR }
    enum Decision { IGNORE, WAITING, ACCEPT, CHANGED }
    private State state = State.EMPTY;
    private JSONObject selected;
    private String pageUrl = "";
    private long cid, generation, selectionRequest;
    private boolean ready, preparedOnce, buffering = true;

    long requestSelection() { return ++selectionRequest; }
    boolean currentSelectionRequest(long request) { return request == selectionRequest; }
    long begin(JSONObject media) {
        selected = copy(media);
        generation++; selectionRequest++;
        pageUrl = ""; cid = 0; ready = false; preparedOnce = false; buffering = true; state = State.RESOLVING;
        return generation;
    }
    boolean resolved(long token, BilibiliMediaResolver.Resolved result) {
        if (token != generation || state != State.RESOLVING) return false;
        if (result.media.optLong("cid") <= 0 || !sameExpected(result.media)) { fail(token); return false; }
        pageUrl = result.pageUrl; cid = result.media.optLong("cid"); state = State.LOADING;
        return true;
    }
    boolean fail(long token) {
        if (token != generation) return false;
        ready = false; buffering = true; state = State.ERROR;
        return true;
    }
    void suspend() { ready = false; buffering = true; }
    void reset() { generation++; selectionRequest++; selected = null; pageUrl = ""; cid = 0; ready = false; state = State.EMPTY; }
    Decision observe(long token, String url, long actualCid, String event, int readyState, boolean foreground) {
        if (token != generation || state == State.EMPTY || state == State.RESOLVING || state == State.ERROR || state == State.CHANGED) return Decision.IGNORE;
        if (!matchesUrl(url) || actualCid <= 0 || actualCid != cid) {
            if (state == State.LOADING) return Decision.WAITING;
            ready = false; state = State.CHANGED;
            return Decision.CHANGED;
        }
        state = State.VERIFIED;
        boolean futureData = readyState >= 3 && readyState <= 4;
        if (!foreground || !futureData || "metadata".equals(event)) { ready = false; buffering = true; }
        else if ("ready".equals(event) || "probe".equals(event) || "playing".equals(event)) { ready = true; preparedOnce = true; buffering = false; }
        if ("waiting".equals(event) || "stalled".equals(event)) buffering = true;
        return Decision.ACCEPT;
    }
    Decision navigating(String url) {
        if (state != State.VERIFIED) return Decision.IGNORE;
        if (matchesUrl(url)) { generation++; ready = false; state = State.LOADING; return Decision.IGNORE; }
        ready = false; state = State.CHANGED;
        return Decision.CHANGED;
    }
    boolean matchesUrl(String candidate) {
        if (candidate == null || pageUrl.isEmpty()) return false;
        try {
            URI actual = new URI(candidate), expected = new URI(pageUrl);
            if (!trustedOrigin(actual.getScheme(), actual.getHost(), actual.getPort()) || actual.getPath() == null || !actual.getPath().replaceFirst("/$", "").equals(expected.getPath())) return false;
            String page = queryPage(actual.getQuery());
            return queryPage(expected.getQuery()).equals(page);
        } catch (Exception error) { return false; }
    }
    static boolean trustedOrigin(String scheme, String host, int port) { return "https".equals(scheme) && ("www.bilibili.com".equals(host) || "m.bilibili.com".equals(host)) && (port == -1 || port == 443); }
    private static String queryPage(String query) {
        if (query != null) for (String part : query.split("&")) if (part.startsWith("p=")) return part.substring(2);
        return "1";
    }
    private boolean sameExpected(JSONObject actual) {
        for (String field : new String[]{"episodeId", "aid", "bvid", "cid"}) if (selected.has(field) && !selected.opt(field).toString().equals(String.valueOf(actual.opt(field)))) return false;
        return true;
    }
    private static JSONObject copy(JSONObject value) { try { return value == null ? null : new JSONObject(value.toString()); } catch (Exception error) { throw new IllegalArgumentException("Invalid public media identity", error); } }
    JSONObject selected() { return copy(selected); }
    String key() { return MediaIdentity.key(selected); }
    String pageUrl() { return pageUrl; }
    long generation() { return generation; }
    State state() { return state; }
    boolean ready() { return state == State.VERIFIED && ready; }
    boolean buffering() { return buffering; }
    boolean awaitingPreparation() { return !preparedOnce && (state == State.LOADING || state == State.VERIFIED); }
}
