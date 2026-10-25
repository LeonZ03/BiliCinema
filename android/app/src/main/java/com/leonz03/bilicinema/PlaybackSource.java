package com.leonz03.bilicinema;

import org.json.JSONObject;
import java.net.URI;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.List;
import java.util.ArrayList;

/** Device-local, immutable playback description. Never serialize this into a room message. */
final class PlaybackSource {
    enum Kind { DASH, PROGRESSIVE, OFFICIAL }
    static final class Quality {
        final int id;
        final String label;
        Quality(int id, String label) { this.id = id; this.label = label; }
    }
    private final String identityJson;
    final String title;
    final double durationSeconds;
    final Kind kind;
    final URI videoUri;
    final URI audioUri;
    final Map<String, String> headers;
    final int quality;
    final List<Quality> qualities;

    PlaybackSource(JSONObject media, String title, double durationSeconds, Kind kind,
                   URI videoUri, URI audioUri, Map<String, String> headers) throws PlaybackFailure {
        this(media, title, durationSeconds, kind, videoUri, audioUri, headers, 0, Collections.emptyList());
    }

    PlaybackSource(JSONObject media, String title, double durationSeconds, Kind kind,
                   URI videoUri, URI audioUri, Map<String, String> headers, int quality, List<Quality> qualities) throws PlaybackFailure {
        if (media == null || media.optLong("aid") <= 0 || media.optLong("cid") <= 0
                || !media.optString("bvid").matches("BV[0-9A-Za-z]{10}")
                || (media.has("episodeId") && media.optLong("episodeId") <= 0)
                || kind == null || !Double.isFinite(durationSeconds) || durationSeconds <= 0
                || !validUri(videoUri) || (audioUri != null && !validUri(audioUri))
                || (kind != Kind.DASH && audioUri != null)
                || (kind == Kind.OFFICIAL && (!"www.bilibili.com".equals(videoUri.getHost())
                    || videoUri.getPort() != -1 || !videoUri.getPath().matches("/(bangumi/play/ep[0-9]+|video/BV[0-9A-Za-z]{10})/?")))) {
            throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        }
        try {
            // Retain only the public identity, even if a caller supplied unrelated fields.
            JSONObject identity = new JSONObject().put("aid", media.getLong("aid"))
                    .put("bvid", media.getString("bvid")).put("cid", media.getLong("cid"));
            if (media.has("episodeId")) identity.put("episodeId", media.getLong("episodeId"));
            identityJson = identity.toString();
        } catch (org.json.JSONException exception) {
            throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        }
        this.title = title == null ? "" : title;
        this.durationSeconds = durationSeconds;
        this.kind = kind;
        this.videoUri = videoUri;
        this.audioUri = audioUri;
        LinkedHashMap<String, String> safeHeaders = new LinkedHashMap<>();
        if (headers != null) for (Map.Entry<String, String> header : headers.entrySet()) {
            String name = header.getKey(), value = header.getValue();
            if (!("Referer".equals(name) || "User-Agent".equals(name)) || value == null
                    || value.indexOf('\r') >= 0 || value.indexOf('\n') >= 0) {
                throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
            }
            safeHeaders.put(name, value);
        }
        this.headers = Collections.unmodifiableMap(safeHeaders);
        this.quality = quality;
        this.qualities = Collections.unmodifiableList(new ArrayList<>(qualities));
    }

    String qualityLabel() {
        for (Quality option : qualities) if (option.id == quality) return option.label;
        return "默认画质";
    }

    PlaybackSource withQuality(int value) throws PlaybackFailure {
        return new PlaybackSource(media(), title, durationSeconds, kind, videoUri, audioUri, headers, value, qualities);
    }

    JSONObject media() {
        try { return new JSONObject(identityJson); }
        catch (org.json.JSONException exception) { throw new IllegalStateException("Invalid stored media identity"); }
    }

    static boolean validUri(URI uri) {
        return uri != null && "https".equalsIgnoreCase(uri.getScheme()) && uri.getHost() != null
                && !uri.getHost().isEmpty() && uri.getRawUserInfo() == null && uri.getRawFragment() == null;
    }
}
