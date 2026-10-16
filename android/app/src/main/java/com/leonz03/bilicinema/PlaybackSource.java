package com.leonz03.bilicinema;

import org.json.JSONObject;
import java.net.URI;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/** Device-local, immutable playback description. Never serialize this into a room message. */
final class PlaybackSource {
    enum Kind { DASH, PROGRESSIVE }
    private final String identityJson;
    final String title;
    final double durationSeconds;
    final Kind kind;
    final URI videoUri;
    final URI audioUri;
    final Map<String, String> headers;

    PlaybackSource(JSONObject media, String title, double durationSeconds, Kind kind,
                   URI videoUri, URI audioUri, Map<String, String> headers) throws PlaybackFailure {
        if (media == null || media.optLong("aid") <= 0 || media.optLong("cid") <= 0
                || !media.optString("bvid").matches("BV[0-9A-Za-z]{10}")
                || (media.has("episodeId") && media.optLong("episodeId") <= 0)
                || kind == null || !Double.isFinite(durationSeconds) || durationSeconds <= 0
                || !validUri(videoUri) || (audioUri != null && !validUri(audioUri))
                || (kind == Kind.PROGRESSIVE && audioUri != null)) {
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
