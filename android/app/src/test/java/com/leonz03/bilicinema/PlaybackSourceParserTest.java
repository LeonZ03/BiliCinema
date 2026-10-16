package com.leonz03.bilicinema;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import java.net.URI;
import java.util.LinkedHashMap;
import java.util.Map;
import static org.junit.Assert.*;

public class PlaybackSourceParserTest {
    private BilibiliMediaResolver.Resolved selected(boolean episode) throws Exception {
        JSONObject media = new JSONObject().put("aid", 42).put("bvid", "BV1xx411c7mD").put("cid", 202);
        if (episode) media.put("episodeId", 17);
        return new BilibiliMediaResolver.Resolved(media, episode ? "https://www.bilibili.com/bangumi/play/ep17"
                : "https://www.bilibili.com/video/BV1xx411c7mD?p=2", "测试影片");
    }
    private JSONObject track(int id, String mime, String codecs, String name) throws Exception {
        return new JSONObject().put("id", id).put("mimeType", mime).put("codecs", codecs)
                .put("bandwidth", 1000).put("base_url", "https://media.example.test/" + name + ".m4s?opaque=local");
    }
    private JSONObject dash() throws Exception {
        return new JSONObject().put("code", 0).put("data", new JSONObject().put("timelength", 120000)
                .put("dash", new JSONObject().put("duration", 120)
                        .put("video", new JSONArray().put(track(80, "video/mp4", "hev1.1.6.L120.90", "hevc"))
                                .put(track(64, "video/mp4", "avc1.64001F", "avc720"))
                                .put(track(80, "video/mp4", "avc1.640028", "avc1080")))
                        .put("audio", new JSONArray().put(track(30280, "audio/mp4", "mp4a.40.2", "aac")))));
    }
    private JSONObject mp4() throws Exception {
        return new JSONObject().put("code", 0).put("data", new JSONObject().put("format", "mp4")
                .put("timelength", 120000).put("video_codecid", 7)
                .put("durl", new JSONArray().put(new JSONObject().put("url", "https://media.example.test/video.mp4").put("length", 120000))));
    }
    private void failure(JSONObject response, boolean episode, PlaybackFailure.Reason reason) throws Exception {
        try { PlaybackSourceParser.parse(response, selected(episode)); fail("Expected typed failure"); }
        catch (PlaybackFailure failure) {
            assertEquals(reason, failure.reason);
            assertEquals(failure.safeMessage(), failure.getMessage());
            assertFalse(failure.safeMessage().contains("https://"));
        }
    }

    @Test public void dashChoosesAvcAndAacAndKeepsUrlsDeviceLocal() throws Exception {
        PlaybackSource source = PlaybackSourceParser.parse(dash(), selected(false));
        assertEquals(PlaybackSource.Kind.DASH, source.kind);
        assertTrue(source.videoUri.getPath().contains("avc1080"));
        assertTrue(source.audioUri.getPath().contains("aac"));
        assertEquals(120, source.durationSeconds, 0);
        assertEquals("测试影片", source.title);
        assertEquals(202, source.media().getLong("cid"));
        assertFalse(source.media().toString().contains("opaque"));
        assertEquals(2, source.headers.size());
        assertFalse(source.headers.containsKey("Cookie"));
    }
    @Test public void progressiveMp4KeepsTheExactPart() throws Exception {
        PlaybackSource source = PlaybackSourceParser.parse(mp4(), selected(false));
        assertEquals(PlaybackSource.Kind.PROGRESSIVE, source.kind);
        assertNull(source.audioUri);
        assertEquals(202, source.media().getLong("cid"));
    }
    @Test public void bangumiUsesNestedV2VideoInfo() throws Exception {
        JSONObject root = new JSONObject().put("code", 0).put("result", new JSONObject().put("video_info", dash().getJSONObject("data")));
        PlaybackSource source = PlaybackSourceParser.parse(root, selected(true));
        assertEquals(17, source.media().getLong("episodeId"));
    }
    @Test public void publicIdentityIsDefensivelyCopiedAndWhitelistOnly() throws Exception {
        JSONObject original = selected(false).media.put("cookie", "fake-secret");
        Map<String, String> headers = new LinkedHashMap<>(); headers.put("Referer", "https://www.bilibili.com/");
        PlaybackSource source = new PlaybackSource(original, "", 12, PlaybackSource.Kind.PROGRESSIVE,
                URI.create("https://media.example.test/video.mp4"), null, headers);
        original.put("cid", 999); headers.put("Cookie", "fake-secret"); source.media().put("cid", 888);
        assertEquals(202, source.media().getLong("cid"));
        assertFalse(source.media().has("cookie"));
        assertFalse(source.headers.containsKey("Cookie"));
        try { source.headers.put("Cookie", "fake-secret"); fail("Headers must be immutable"); }
        catch (UnsupportedOperationException expected) { assertNotNull(expected); }
    }
    @Test public void explicitCookieHeaderCannotEnterMediaTransport() throws Exception {
        Map<String, String> headers = new LinkedHashMap<>(); headers.put("Cookie", "fake-secret");
        try { new PlaybackSource(selected(false).media, "", 12, PlaybackSource.Kind.PROGRESSIVE, URI.create("https://media.example.test/a.mp4"), null, headers); fail("Cookie accepted"); }
        catch (PlaybackFailure failure) { assertEquals(PlaybackFailure.Reason.INVALID_MEDIA, failure.reason); }
    }
    @Test public void missingPayloadAndMismatchedCidFailClosed() throws Exception {
        failure(new JSONObject().put("code", 0), false, PlaybackFailure.Reason.INVALID_MEDIA);
        JSONObject root = dash(); root.getJSONObject("data").put("cid", 999);
        failure(root, false, PlaybackFailure.Reason.INVALID_MEDIA);
    }
    @Test public void absentAudioArrayCannotSilentlyClaimCompletePlayback() throws Exception {
        JSONObject root = dash(); root.getJSONObject("data").getJSONObject("dash").remove("audio");
        failure(root, false, PlaybackFailure.Reason.INVALID_MEDIA);
    }
    @Test public void unsupportedCodecIsExplicit() throws Exception {
        JSONObject root = dash(); root.getJSONObject("data").getJSONObject("dash").put("video", new JSONArray().put(track(80, "video/mp4", "av01.0.08M.08", "av1")));
        failure(root, false, PlaybackFailure.Reason.UNSUPPORTED);
    }
    @Test public void previewAndDrmNeverBecomeFullPlayback() throws Exception {
        JSONObject preview = dash(); preview.getJSONObject("data").put("is_preview", 1);
        failure(preview, false, PlaybackFailure.Reason.PERMISSION_DENIED);
        JSONObject drm = dash(); drm.getJSONObject("data").put("drm_tech_type", 2);
        failure(drm, false, PlaybackFailure.Reason.UNSUPPORTED);
        JSONObject pgc = new JSONObject().put("code", 0).put("result", new JSONObject().put("video_info", dash().getJSONObject("data"))
                .put("play_check", new JSONObject().put("play_detail", "PLAY_PREVIEW")));
        failure(pgc, true, PlaybackFailure.Reason.PERMISSION_DENIED);
    }
    @Test public void multiSegmentAndFlvDoNotPlayOnlyTheFirstSegment() throws Exception {
        JSONObject root = mp4(); root.getJSONObject("data").getJSONArray("durl").put(new JSONObject().put("url", "https://media.example.test/second.mp4"));
        failure(root, false, PlaybackFailure.Reason.UNSUPPORTED);
        JSONObject flv = mp4(); flv.getJSONObject("data").put("format", "flv").getJSONArray("durl").getJSONObject(0).put("url", "https://media.example.test/video.flv");
        failure(flv, false, PlaybackFailure.Reason.UNSUPPORTED);
    }
    @Test public void apiFailuresHaveFixedSafeMessages() throws Exception {
        failure(new JSONObject().put("code", -101).put("message", "fake-secret"), false, PlaybackFailure.Reason.NEEDS_LOGIN);
        failure(new JSONObject().put("code", -10403).put("message", "fake-secret"), false, PlaybackFailure.Reason.PERMISSION_DENIED);
        failure(new JSONObject().put("code", -412), false, PlaybackFailure.Reason.NETWORK_RETRYABLE);
    }
    @Test public void expiredAndNonHttpsAddressesAreExplicitFailures() throws Exception {
        JSONObject expired = mp4(); expired.getJSONObject("data").getJSONArray("durl").getJSONObject(0).put("url", "https://media.example.test/a.mp4?deadline=1");
        failure(expired, false, PlaybackFailure.Reason.EXPIRED_SOURCE);
        JSONObject insecure = mp4(); insecure.getJSONObject("data").getJSONArray("durl").getJSONObject(0).put("url", "http://media.example.test/a.mp4");
        failure(insecure, false, PlaybackFailure.Reason.INVALID_MEDIA);
        assertTrue(new PlaybackFailure(PlaybackFailure.Reason.EXPIRED_SOURCE).refreshRequired());
        assertFalse(new PlaybackFailure(PlaybackFailure.Reason.PLAYBACK_BLOCKED).refreshRequired());
    }
}
