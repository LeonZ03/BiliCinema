package com.leonz03.bilicinema;

import org.json.JSONArray;
import org.json.JSONObject;
import java.net.URI;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.ArrayList;
import java.util.List;
import java.util.TreeMap;
import java.util.Collections;

/** Selects a complete native source or an authorized official-player session; previews never bind. */
final class PlaybackSourceParser {
    private PlaybackSourceParser() { }

    static PlaybackSource parse(JSONObject root, BilibiliMediaResolver.Resolved resolved) throws PlaybackFailure {
        return parse(root, resolved, 0);
    }

    static PlaybackSource parse(JSONObject root, BilibiliMediaResolver.Resolved resolved, int preferredQuality) throws PlaybackFailure {
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
            for (String key : new String[]{"aid", "bvid", "cid", "ep_id"}) {
                String expectedKey = "ep_id".equals(key) ? "episodeId" : key;
                if (payload.has(key) && !payload.isNull(key)
                        && !payload.get(key).toString().equals(media.opt(expectedKey).toString())) {
                    throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
                }
            }
            if (protectedMedia(owner) || protectedMedia(payload)) throw new PlaybackFailure(PlaybackFailure.Reason.DRM_PROTECTED);
            Map<String, String> headers = new LinkedHashMap<>();
            headers.put("Referer", resolved.pageUrl);
            headers.put("User-Agent", BilibiliMediaResolver.USER_AGENT);
            double duration = payload.optDouble("timelength", 0) / 1000.0;
            JSONObject dash = payload.optJSONObject("dash");
            if (dash != null) {
                if (protectedMedia(dash)) throw new PlaybackFailure(PlaybackFailure.Reason.DRM_PROTECTED);
                JSONArray videos = dash.getJSONArray("video"), audios = dash.getJSONArray("audio");
                Map<Integer, JSONObject> available = videoTracks(videos);
                JSONObject video = selectVideo(available, preferredQuality), audio = bestAudio(audios);
                if (video == null || (audios.length() > 0 && audio == null)) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
                JSONObject dolby = dash.optJSONObject("dolby");
                JSONObject flac = dash.optJSONObject("flac");
                if (audio == null && ((dolby != null && dolby.optJSONArray("audio") != null && dolby.optJSONArray("audio").length() > 0)
                        || (flac != null && flac.optJSONObject("audio") != null))) {
                    throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
                }
                if (!(duration > 0)) duration = dash.optDouble("duration", 0);
                List<PlaybackSource.Quality> qualities = new ArrayList<>();
                for (int id : available.keySet()) qualities.add(new PlaybackSource.Quality(id, qualityLabel(payload, id)));
                return new PlaybackSource(media, resolved.title, duration, PlaybackSource.Kind.DASH,
                        streamUri(video), audio == null ? null : streamUri(audio), headers, video.getInt("id"), qualities);
            }
            Map<Integer, JSONObject> alternatives = new TreeMap<>(Collections.reverseOrder());
            JSONArray groups = payload.optJSONArray("durls");
            if (groups != null) for (int i = 0; i < groups.length(); i++) {
                JSONObject group = groups.getJSONObject(i);
                JSONArray parts = group.optJSONArray("durl");
                if (parts == null || parts.length() != 1 || group.optInt("quality") <= 0) continue;
                if (protectedMedia(group)) throw new PlaybackFailure(PlaybackFailure.Reason.DRM_PROTECTED);
                alternatives.put(group.getInt("quality"), parts.getJSONObject(0));
            }
            JSONArray segments = payload.getJSONArray("durl");
            // Segmented FLV is deliberately unsupported; a partial first segment cannot claim readiness.
            if (segments.length() != 1) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
            int quality = payload.optInt("quality", 0);
            if (quality > 0 && !alternatives.containsKey(quality)) alternatives.put(quality, segments.getJSONObject(0));
            JSONObject segment = alternatives.isEmpty() ? segments.getJSONObject(0) : selectVideo(alternatives, preferredQuality);
            for (Map.Entry<Integer, JSONObject> entry : alternatives.entrySet()) if (entry.getValue() == segment) quality = entry.getKey();
            if (protectedMedia(segment)) throw new PlaybackFailure(PlaybackFailure.Reason.DRM_PROTECTED);
            URI uri = checkedUri(segment.getString("url"));
            String format = payload.optString("format").toLowerCase(java.util.Locale.ROOT);
            int codec = payload.optInt("video_codecid", 0);
            if (!(format.contains("mp4") || uri.getPath().toLowerCase(java.util.Locale.ROOT).endsWith(".mp4"))
                    || (codec != 0 && codec != 7)) throw new PlaybackFailure(PlaybackFailure.Reason.UNSUPPORTED);
            if (!(duration > 0)) duration = segment.optDouble("length", 0) / 1000.0;
            List<PlaybackSource.Quality> qualities = new ArrayList<>();
            for (int id : alternatives.keySet()) qualities.add(new PlaybackSource.Quality(id, qualityLabel(payload, id)));
            return new PlaybackSource(media, resolved.title, duration, PlaybackSource.Kind.PROGRESSIVE, uri, null, headers, quality, qualities);
        } catch (PlaybackFailure failure) {
            if (failure.reason == PlaybackFailure.Reason.DRM_PROTECTED) return official(root, resolved, preferredQuality);
            throw failure;
        }
        catch (Exception exception) { throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
    }

    private static PlaybackSource official(JSONObject root, BilibiliMediaResolver.Resolved resolved, int preferred) throws PlaybackFailure {
        try {
            JSONObject owner = root.getJSONObject(resolved.media.optLong("episodeId") > 0 ? "result" : "data");
            JSONObject payload = resolved.media.optLong("episodeId") > 0 ? owner.getJSONObject("video_info") : owner;
            Map<Integer, Boolean> available = new TreeMap<>(Collections.reverseOrder());
            JSONObject dash = payload.optJSONObject("dash");
            JSONArray tracks = dash == null ? null : dash.optJSONArray("video");
            if (tracks != null) for (int i = 0; i < tracks.length(); i++) {
                int id = tracks.getJSONObject(i).optInt("id"); if (id > 0) available.put(id, true);
            }
            JSONArray groups = payload.optJSONArray("durls");
            if (groups != null) for (int i = 0; i < groups.length(); i++) {
                JSONObject group = groups.getJSONObject(i);
                if (group.optInt("quality") > 0 && group.optJSONArray("durl") != null && group.getJSONArray("durl").length() > 0)
                    available.put(group.getInt("quality"), true);
            }
            if (payload.optInt("quality") > 0 && payload.optJSONArray("durl") != null && payload.getJSONArray("durl").length() > 0)
                available.put(payload.getInt("quality"), true);
            List<PlaybackSource.Quality> choices = new ArrayList<>();
            for (int id : available.keySet()) choices.add(new PlaybackSource.Quality(id, qualityLabel(payload, id)));
            // Only the public official page goes to WebView. Its own signed-in player obtains licenses.
            return new PlaybackSource(resolved.media, resolved.title, payload.optDouble("timelength", 0) / 1000,
                    PlaybackSource.Kind.OFFICIAL, new URI(resolved.pageUrl), null, Collections.emptyMap(),
                    available.containsKey(preferred) ? preferred : 0, choices);
        } catch (PlaybackFailure failure) { throw failure; }
        catch (Exception failure) { throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
    }

    private static Map<Integer, JSONObject> videoTracks(JSONArray tracks) throws Exception {
        Map<Integer, JSONObject> available = new TreeMap<>(Collections.reverseOrder());
        for (int i = 0; i < tracks.length(); i++) {
            JSONObject track = tracks.getJSONObject(i);
            if (protectedMedia(track)) throw new PlaybackFailure(PlaybackFailure.Reason.DRM_PROTECTED);
            String mime = track.optString("mimeType", track.optString("mime_type"));
            String codecs = track.optString("codecs").toLowerCase(java.util.Locale.ROOT);
            if (!"video/mp4".equals(mime) || !(codecs.startsWith("avc1") || codecs.startsWith("avc3")
                    || (codecs.isEmpty() && track.optInt("codecid") == 7))) continue;
            int id = track.optInt("id");
            if (id <= 0) continue;
            JSONObject previous = available.get(id);
            if (previous == null || track.optLong("bandwidth") > previous.optLong("bandwidth")) available.put(id, track);
        }
        return available;
    }

    private static JSONObject selectVideo(Map<Integer, JSONObject> tracks, int preferred) {
        JSONObject lowest = null;
        for (Map.Entry<Integer, JSONObject> track : tracks.entrySet()) {
            if (preferred <= 0 || track.getKey() <= preferred) return track.getValue();
            lowest = track.getValue();
        }
        return lowest;
    }

    private static String qualityLabel(JSONObject payload, int quality) {
        JSONArray formats = payload.optJSONArray("support_formats");
        if (formats != null) for (int i = 0; i < formats.length(); i++) {
            JSONObject format = formats.optJSONObject(i);
            if (format == null || format.optInt("quality") != quality) continue;
            String label = format.optString("new_description", format.optString("display_desc"));
            if (!label.isEmpty() && label.length() <= 32 && !label.chars().anyMatch(Character::isISOControl)) return label;
        }
        switch (quality) {
            case 6: return "240P"; case 16: return "360P"; case 32: return "480P";
            case 64: return "720P"; case 74: return "720P 60帧"; case 80: return "1080P";
            case 112: return "1080P 高码率"; case 116: return "1080P 60帧";
            case 120: return "4K"; case 125: return "HDR"; case 126: return "杜比视界"; case 127: return "8K";
            default: return "画质 " + quality;
        }
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

    private static JSONObject bestAudio(JSONArray tracks) throws Exception {
        JSONObject selected = null;
        for (int i = 0; i < tracks.length(); i++) {
            JSONObject track = tracks.getJSONObject(i);
            if (protectedMedia(track)) throw new PlaybackFailure(PlaybackFailure.Reason.DRM_PROTECTED);
            String mime = track.optString("mimeType", track.optString("mime_type"));
            String codecs = track.optString("codecs").toLowerCase(java.util.Locale.ROOT);
            boolean supported = "audio/mp4".equals(mime) && codecs.startsWith("mp4a");
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
            if (value instanceof JSONObject && ((JSONObject) value).length() == 0) continue;
            if (value instanceof JSONArray && ((JSONArray) value).length() == 0) continue;
            if (value != null && value != JSONObject.NULL && !"".equals(value.toString()) && !"0".equals(value.toString()) && !"false".equals(value.toString())) return true;
        }
        return false;
    }

    private static boolean flag(JSONObject object, String key) {
        Object value = object.opt(key);
        return value != null && value != JSONObject.NULL && !"0".equals(value.toString()) && !"false".equalsIgnoreCase(value.toString()) && !"".equals(value.toString());
    }
}
