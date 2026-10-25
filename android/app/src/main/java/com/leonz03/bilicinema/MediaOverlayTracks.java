package com.leonz03.bilicinema;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import okhttp3.Call;
import okhttp3.CookieJar;
import okhttp3.HttpUrl;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

/** One owner for optional device-local tracks; failures never change playback readiness. */
final class MediaOverlayTracks implements AutoCloseable {
    static final int SUBTITLES_READY = 1, SUBTITLE_ERROR = 2, DANMAKU_ERROR = 4, DANMAKU_READY = 8;
    static final class Cue {
        final double from, to; final String text;
        Cue(double from, double to, String text) { this.from = from; this.to = to; this.text = text; }
    }
    static final class Subtitle {
        final String label, url; final List<Cue> cues; final boolean loaded, failed;
        Subtitle(String label, String url, List<Cue> cues, boolean loaded, boolean failed) {
            this.label = label; this.url = url;
            this.cues = Collections.unmodifiableList(new ArrayList<>(cues));
            this.loaded = loaded; this.failed = failed;
        }
    }
    static final class Danmaku {
        final long id; final double at; final int mode, color, lane; final String text;
        Danmaku(long id, int at, int mode, int color, String text) {
            this.id = id; this.at = at / 1000.0; this.mode = mode; this.color = color;
            this.text = text; this.lane = Math.floorMod(Long.hashCode(id), 6);
        }
    }
    interface Listener {
        void loaded(long generation, List<Subtitle> subtitles, List<Danmaku> danmaku, int segment, int status);
    }
    private static final class Scope {
        final long epoch, generation, aid, cid; final String bvid, cookie;
        Scope(long epoch, long generation, JSONObject media, String cookie) {
            this.epoch = epoch; this.generation = generation; this.aid = media.optLong("aid");
            this.cid = media.optLong("cid"); this.bvid = media.optString("bvid"); this.cookie = cookie;
        }
    }
    private final ExecutorService worker = Executors.newFixedThreadPool(2);
    private final OkHttpClient http = new OkHttpClient.Builder().cookieJar(CookieJar.NO_COOKIES)
            .connectTimeout(12, TimeUnit.SECONDS).readTimeout(15, TimeUnit.SECONDS).callTimeout(25, TimeUnit.SECONDS)
            .followRedirects(false).followSslRedirects(false).build();
    private final HttpResourceLifecycle network = new HttpResourceLifecycle(http);
    private final Map<Call, Boolean> active = new LinkedHashMap<>();
    private final List<Subtitle> subtitles = new ArrayList<>();
    private final Set<Integer> pendingSubtitles = new HashSet<>();
    private final LinkedHashMap<Integer, List<Danmaku>> segments = new LinkedHashMap<>();
    private Scope scope;
    private Listener listener;
    private List<Subtitle> publishedSubtitles;
    private long epoch, segmentRevision, retrySegmentAt;
    private int requestedSegment;
    private boolean closed, catalogReady, subtitleError, danmakuError, segmentPending;

    synchronized void load(JSONObject media, String cookie, long token, Listener listener) {
        if (closed) return;
        cancelLocked();
        scope = new Scope(epoch, token, media, cookie);
        this.listener = listener;
        Scope request = scope;
        emit();
        worker.execute(() -> loadCatalog(request));
        loadSegment(media, cookie, token, 1, listener);
    }

    private void loadCatalog(Scope request) {
        try {
            HttpUrl url = HttpUrl.get("https://api.bilibili.com/x/player/v2").newBuilder()
                    .addQueryParameter("cid", Long.toString(request.cid))
                    .addQueryParameter("aid", Long.toString(request.aid))
                    .addQueryParameter("bvid", request.bvid).build();
            JSONObject reply = new JSONObject(new String(getBytes(url.toString(), true, request, -1), StandardCharsets.UTF_8));
            if (reply.optInt("code", -1) != 0) throw new IOException("Subtitle directory unavailable");
            JSONObject metadata = reply.getJSONObject("data").optJSONObject("subtitle");
            JSONArray tracks = metadata == null ? null : metadata.optJSONArray("subtitles");
            List<Subtitle> parsed = new ArrayList<>();
            if (tracks != null) for (int i = 0; i < tracks.length() && parsed.size() < 16; i++) {
                JSONObject track = tracks.optJSONObject(i); if (track == null) continue;
                String address = track.optString("subtitle_url"); if (address.startsWith("//")) address = "https:" + address;
                if (!trustedSubtitle(address)) continue;
                parsed.add(new Subtitle(track.optString("lan_doc", track.optString("lan", "字幕")), address,
                        Collections.emptyList(), false, false));
            }
            synchronized (this) {
                if (!current(request)) return;
                subtitles.clear(); subtitles.addAll(parsed); catalogReady = true; subtitleError = false; emit();
            }
        } catch (CancellationException cancelled) { /* The superseded media owns no UI result. */ }
        catch (IOException | JSONException | IllegalArgumentException failure) {
            synchronized (this) { if (current(request)) { subtitleError = true; emit(); } }
        }
    }

    synchronized void loadSubtitle(JSONObject media, String cookie, long token, int index, Listener listener) {
        if (scope == null || scope.generation != token || closed || index < 0 || index >= subtitles.size()) return;
        Subtitle track = subtitles.get(index);
        if (track.loaded || !pendingSubtitles.add(index)) return;
        Scope request = scope; subtitleError = false; emit();
        worker.execute(() -> {
            try {
                JSONObject body = new JSONObject(new String(getBytes(track.url, false, request, -1), StandardCharsets.UTF_8));
                JSONArray lines = body.getJSONArray("body"); List<Cue> cues = new ArrayList<>();
                for (int i = 0; i < lines.length() && cues.size() < 5000; i++) {
                    JSONObject line = lines.optJSONObject(i); if (line == null) continue;
                    double from = line.optDouble("from", -1), to = line.optDouble("to", -1);
                    String text = line.optString("content").trim();
                    if (Double.isFinite(from) && Double.isFinite(to) && from >= 0 && to > from && !text.isEmpty() && text.length() <= 2000)
                        cues.add(new Cue(from, to, text));
                }
                cues.sort(Comparator.comparingDouble(item -> item.from));
                synchronized (this) {
                    if (!current(request)) return;
                    subtitles.set(index, new Subtitle(track.label, track.url, cues, true, false));
                    pendingSubtitles.remove(index); subtitleError = false; emit();
                }
            } catch (CancellationException cancelled) { /* No publication after media cancellation. */ }
            catch (IOException | JSONException | IllegalArgumentException failure) {
                synchronized (this) {
                    if (!current(request)) return;
                    pendingSubtitles.remove(index); subtitleError = true;
                    subtitles.set(index, new Subtitle(track.label, track.url, Collections.emptyList(), false, true)); emit();
                }
            }
        });
    }

    synchronized void loadSegment(JSONObject media, String cookie, long token, int segment, Listener listener) {
        if (closed || scope == null || scope.generation != token || segment < 1) return;
        if (requestedSegment == segment && (segmentPending || segments.containsKey(segment)
                || android.os.SystemClock.elapsedRealtime() < retrySegmentAt)) return;
        requestedSegment = segment; segmentRevision++; segmentPending = false; danmakuError = false; retrySegmentAt = 0;
        // A seek invalidates only danmaku work. It must not cancel a subtitle language request.
        cancelCalls(true);
        if (segments.containsKey(segment)) { emit(); return; }
        Scope request = scope; long revision = segmentRevision;
        segmentPending = true; emit();
        worker.execute(() -> {
            try {
                String url = "https://api.bilibili.com/x/v2/dm/web/seg.so?type=1&oid=" + request.cid
                        + "&pid=" + request.aid + "&segment_index=" + segment;
                List<Danmaku> parsed = parseDanmaku(getBytes(url, true, request, revision));
                synchronized (this) {
                    if (!current(request) || revision != segmentRevision) return;
                    segments.put(segment, parsed);
                    while (segments.size() > 6) segments.remove(segments.keySet().iterator().next());
                    segmentPending = false; danmakuError = false; emit();
                }
            } catch (CancellationException cancelled) { /* Replaced by a newer seek or media. */ }
            catch (IOException | IllegalArgumentException failure) {
                synchronized (this) {
                    if (!current(request) || revision != segmentRevision) return;
                    segmentPending = false; danmakuError = true;
                    retrySegmentAt = android.os.SystemClock.elapsedRealtime() + 5000; emit();
                }
            }
        });
    }

    synchronized void seek(JSONObject media, String cookie, long token, double position, Listener listener) {
        if (!Double.isFinite(position)) return;
        loadSegment(media, cookie, token, (int) (Math.max(0, position) / 360.0) + 1, listener);
    }

    // Each completion updates only its own state. Segment completions cannot overwrite subtitle data.
    private void emit() {
        if (scope == null || closed || listener == null) return;
        List<Danmaku> visible = new ArrayList<>();
        if (segments.containsKey(requestedSegment - 1)) visible.addAll(segments.get(requestedSegment - 1));
        if (segments.containsKey(requestedSegment)) visible.addAll(segments.get(requestedSegment));
        visible.sort(Comparator.comparingDouble(item -> item.at));
        int status = (catalogReady ? SUBTITLES_READY : 0) | (subtitleError ? SUBTITLE_ERROR : 0)
                | (danmakuError ? DANMAKU_ERROR : 0) | (segments.containsKey(requestedSegment) ? DANMAKU_READY : 0);
        publishedSubtitles = Collections.unmodifiableList(new ArrayList<>(subtitles));
        listener.loaded(scope.generation, publishedSubtitles, visible, requestedSegment, status);
    }

    /** A UI callback already queued before cancellation must not restore the cancelled snapshot. */
    synchronized boolean isCurrent(long token, List<Subtitle> snapshot) {
        return !closed && scope != null && scope.generation == token && publishedSubtitles == snapshot;
    }

    private byte[] getBytes(String address, boolean api, Scope request, long revision) throws IOException {
        check(request, revision);
        HttpUrl url = HttpUrl.parse(address);
        if (url == null || !url.isHttps() || !url.username().isEmpty() || !url.password().isEmpty() || url.port() != 443
                || (api ? !url.host().equals("api.bilibili.com") : !trustedSubtitle(address))) throw new IOException("Invalid track origin");
        Request.Builder builder = new Request.Builder().url(url).header("Referer", "https://www.bilibili.com/")
                .header("User-Agent", BilibiliMediaResolver.USER_AGENT);
        if (api && request.cookie != null && !request.cookie.isEmpty()) builder.header("Cookie", request.cookie);
        Call call = http.newCall(builder.build());
        synchronized (this) { check(request, revision); active.put(call, revision >= 0); }
        try (Response response = call.execute()) {
            check(request, revision);
            if (!response.isSuccessful() || response.body() == null) throw new IOException("Track unavailable");
            if (response.body().contentLength() > 8 * 1024 * 1024) throw new IOException("Track too large");
            try (InputStream input = response.body().byteStream(); ByteArrayOutputStream output = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[8192]; int count;
                while ((count = input.read(buffer)) != -1) {
                    check(request, revision);
                    if (output.size() + count > 8 * 1024 * 1024) throw new IOException("Track too large");
                    output.write(buffer, 0, count);
                }
                check(request, revision); return output.toByteArray();
            }
        } finally { synchronized (this) { active.remove(call); } }
    }
    private synchronized void check(Scope request, long revision) {
        if (!current(request) || (revision >= 0 && revision != segmentRevision) || Thread.currentThread().isInterrupted())
            throw new CancellationException("Track request superseded");
    }
    private boolean current(Scope request) { return !closed && scope == request && epoch == request.epoch; }
    private static boolean trustedSubtitle(String address) {
        HttpUrl uri = HttpUrl.parse(address);
        if (uri == null || !uri.isHttps() || uri.port() != 443 || !uri.username().isEmpty() || !uri.password().isEmpty()) return false;
        String host = uri.host(); return host.equals("bilibili.com") || host.endsWith(".bilibili.com")
                || host.equals("hdslb.com") || host.endsWith(".hdslb.com");
    }
    private void cancelCalls(boolean segmentsOnly) {
        for (Map.Entry<Call, Boolean> entry : active.entrySet())
            if (!segmentsOnly || entry.getValue()) { Call call = entry.getKey(); network.cancel(call::cancel); }
    }
    private void cancelLocked() {
        epoch++; segmentRevision++; scope = null; publishedSubtitles = null; cancelCalls(false); active.clear();
        subtitles.clear(); segments.clear(); pendingSubtitles.clear(); requestedSegment = 0;
        catalogReady = false; subtitleError = false; danmakuError = false; segmentPending = false; retrySegmentAt = 0;
    }
    synchronized void cancel() { cancelLocked(); }
    @Override public synchronized void close() {
        if (closed) return;
        cancelLocked(); closed = true; listener = null; worker.shutdownNow(); network.close();
    }

    static List<Danmaku> parseDanmaku(byte[] bytes) throws IOException {
        ProtoReader reply = new ProtoReader(bytes); List<Danmaku> result = new ArrayList<>();
        while (reply.has()) {
            int tag = reply.varintInt(), field = tag >>> 3, wire = tag & 7;
            if (field == 1 && wire == 2) {
                Danmaku item = parseElem(reply.bytes());
                if (item != null && item.mode >= 1 && item.mode <= 3 && !item.text.isEmpty() && result.size() < 1000) result.add(item);
            } else reply.skip(wire);
        }
        return result;
    }
    private static Danmaku parseElem(byte[] bytes) throws IOException {
        ProtoReader input = new ProtoReader(bytes); long id = 0; int progress = 0, mode = 1, color = 0xffffff; String text = "";
        while (input.has()) {
            int tag = input.varintInt(), field = tag >>> 3, wire = tag & 7;
            if (wire == 0 && field == 1) id = input.varint64();
            else if (wire == 0 && (field == 2 || field == 3 || field == 5)) {
                int value = input.varintInt(); if (field == 2) progress = value; else if (field == 3) mode = value; else color = value;
            } else if (field == 7 && wire == 2) text = new String(input.bytes(), StandardCharsets.UTF_8);
            else input.skip(wire);
        }
        return id > 0 && progress >= 0 && text.length() <= 100 ? new Danmaku(id, progress, mode, color, text) : null;
    }
    private static final class ProtoReader {
        final byte[] data; int index;
        ProtoReader(byte[] data) { this.data = data; }
        boolean has() { return index < data.length; }
        long varint64() throws IOException {
            long value = 0;
            for (int i = 0; i < 10; i++) {
                if (index >= data.length) throw new IOException("Truncated protobuf");
                int b = data[index++] & 255;
                if (i == 9 && (b & 0xfe) != 0) throw new IOException("Invalid protobuf varint");
                value |= (long) (b & 127) << (i * 7);
                if ((b & 128) == 0) return value;
            }
            throw new IOException("Invalid protobuf varint");
        }
        int varintInt() throws IOException {
            long value = varint64(); if (value < 0 || value > Integer.MAX_VALUE) throw new IOException("Protobuf integer out of range"); return (int) value;
        }
        byte[] bytes() throws IOException {
            int length = varintInt(); require(length);
            byte[] value = java.util.Arrays.copyOfRange(data, index, index + length); index += length; return value;
        }
        void require(int length) throws IOException { if (length < 0 || length > data.length - index) throw new IOException("Truncated protobuf"); }
        void skip(int wire) throws IOException {
            if (wire == 0) { varint64(); return; }
            int size;
            switch (wire) { case 1: size = 8; break; case 2: size = varintInt(); break; case 5: size = 4; break; default: throw new IOException("Invalid protobuf wire type"); }
            require(size); index += size;
        }
    }
}
