package com.leonz03.bilicinema;

import org.json.JSONArray;
import org.json.JSONObject;
import java.net.URI;
import java.util.LinkedHashMap;
import java.util.Map;

/** Fail-closed playback contract selection; no HTML scraping or permission fallback. */
final class PlaybackSourceParser {
    private PlaybackSourceParser() { }

    static PlaybackSource parse(JSONObject root, BilibiliMediaResolver.Resolved resolved) throws PlaybackFailure {
        try {
            requireSuccess(root);
            JSONObject media = resolved.media;
            boolean episode = media.optLong("episodeId") > 0;
            JSONObject owner = root.getJSONObject(episode ? "result" : "data");
            JSONObject check = owner.optJSONObject("play_check");
            if (check != null && "PLAY_PREVIEW".equals(check.optString("play_detail"))) {
                throw new PlaybackFailure(PlaybackFailure.Reason.PERMISSION_DENIED);
            }
            JSONObject payload = episode ? owner.getJSONObject("video_info") : owner;
            if (flag(payload, "is_preview")) throw new PlaybackFailure(PlaybackFailure.Reason.PERMISSION_DENIED);
            if (protectedMedia(owner) || protectedMedia(payload)) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
            for (String key : new String[]{"aid", "bvid", "cid", "ep_id"}) {
                String expectedKey = "ep_id".equals(key) ? "episodeId" : key;
                if (payload.has(key) && !payload.isNull(key)
                        && !payload.get(key).toString().equals(media.opt(expectedKey).toString())) {
                    throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
                }
            }
            Map<String, String> headers = new LinkedHashMap<>();
            headers.put("Referer", resolved.pageUrl);
            headers.put("User-Agent", BilibiliMediaResolver.USER_AGENT);
            double duration = payload.optDouble("timelength", 0) / 1000.0;
            JSONObject dash = payload.optJSONObject("dash");
            if (dash != null) {
                if (protectedMedia(dash)) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
                JSONArray videos = dash.getJSONArray("video"), audios = dash.getJSONArray("audio");
                JSONObject video = best(videos, false), audio = best(audios, true);
                if (video == null || (audios.length() > 0 && audio == null)) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
                JSONObject dolby = dash.optJSONObject("dolby");
                JSONObject flac = dash.optJSONObject("flac");
                if (audio == null && ((dolby != null && dolby.optJSONArray("audio") != null && dolby.optJSONArray("audio").length() > 0)
                        || (flac != null && flac.optJSONObject("audio") != null))) {
                    throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
                }
                if (!(duration > 0)) duration = dash.optDouble("duration", 0);
                return new PlaybackSource(media, resolved.title, duration, PlaybackSource.Kind.DASH,
                        streamUri(video), audio == null ? null : streamUri(audio), headers);
            }
            JSONArray segments = payload.getJSONArray("durl");
            // Segmented FLV is deliberately unsupported; a partial first segment cannot claim readiness.
            if (segments.length() != 1) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
            JSONObject segment = segments.getJSONObject(0);
            if (protectedMedia(segment)) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
            URI uri = checkedUri(segment.getString("url"));
            String format = payload.optString("format").toLowerCase(java.util.Locale.ROOT);
            int codec = payload.optInt("video_codecid", 0);
            if (!(format.contains("mp4") || uri.getPath().toLowerCase(java.util.Locale.ROOT).endsWith(".mp4"))
                    || (codec != 0 && codec != 7)) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
            if (!(duration > 0)) duration = segment.optDouble("length", 0) / 1000.0;
            return new PlaybackSource(media, resolved.title, duration, PlaybackSource.Kind.PROGRESSIVE, uri, null, headers);
        } catch (PlaybackFailure failure) { throw failure; }
        catch (Exception exception) { throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
    }

    static void requireSuccess(JSONObject root) throws PlaybackFailure {
        if (root == null || !root.has("code")) throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        int code = root.optInt("code", Integer.MIN_VALUE);
        if (code == 0) return;
        if (code == -101) throw new PlaybackFailure(PlaybackFailure.Reason.NEEDS_LOGIN);
        if (code == -10403 || code == -403 || code == -404 || code == 6002003 || code == 6002004 || code == 10015002) {
            throw new PlaybackFailure(PlaybackFailure.Reason.PERMISSION_DENIED);
        }
        if (code == -412 || code == -352 || code == -509 || code == -500) throw new PlaybackFailure(PlaybackFailure.Reason.NETWORK_RETRYABLE);
        throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
    }

    private static JSONObject best(JSONArray tracks, boolean audio) throws Exception {
        JSONObject selected = null;
        for (int i = 0; i < tracks.length(); i++) {
            JSONObject track = tracks.getJSONObject(i);
            if (protectedMedia(track)) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
            String mime = track.optString("mimeType", track.optString("mime_type"));
            String codecs = track.optString("codecs").toLowerCase(java.util.Locale.ROOT);
            boolean supported = audio ? ("audio/mp4".equals(mime) && codecs.startsWith("mp4a"))
                    : ("video/mp4".equals(mime) && (codecs.startsWith("avc1") || codecs.startsWith("avc3") || (codecs.isEmpty() && track.optInt("codecid") == 7)));
            if (!supported) continue;
            if (selected == null || track.optInt("id") > selected.optInt("id")
                    || (track.optInt("id") == selected.optInt("id") && track.optLong("bandwidth") > selected.optLong("bandwidth"))) selected = track;
        }
        return selected;
    }

    private static URI streamUri(JSONObject track) throws Exception {
        return checkedUri(track.optString("baseUrl", track.optString("base_url")));
    }

    static URI checkedUri(String value) throws PlaybackFailure {
        try {
            URI uri = new URI(value);
            if (!PlaybackSource.validUri(uri)) throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
            String query = uri.getRawQuery();
            if (query != null) for (String pair : query.split("&")) {
                String[] parts = pair.split("=", 2);
                if (parts.length == 2 && ("deadline".equals(parts[0]) || "expires".equals(parts[0])) && parts[1].matches("[0-9]{1,12}")) {
                    if (Long.parseLong(parts[1]) <= System.currentTimeMillis() / 1000) throw new PlaybackFailure(PlaybackFailure.Reason.EXPIRED_SOURCE);
                }
            }
            return uri;
        } catch (PlaybackFailure failure) { throw failure; }
        catch (Exception exception) { throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
    }

    private static boolean protectedMedia(JSONObject object) {
        if (flag(object, "is_drm") || flag(object, "drm_tech_type") || flag(object, "drm_type")) return true;
        for (String key : new String[]{"drm", "drm_info", "drmInfo", "license_url", "licenseUrl", "widevine", "content_protection", "ContentProtection"}) {
            Object value = object.opt(key);
            if (value != null && value != JSONObject.NULL && !"".equals(value.toString()) && !"0".equals(value.toString()) && !"false".equals(value.toString())) return true;
        }
        return false;
    }

    private static boolean flag(JSONObject object, String key) {
        Object value = object.opt(key);
        return value != null && value != JSONObject.NULL && !"0".equals(value.toString()) && !"false".equalsIgnoreCase(value.toString()) && !"".equals(value.toString());
    }
}
