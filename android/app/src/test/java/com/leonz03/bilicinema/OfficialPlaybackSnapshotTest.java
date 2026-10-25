package com.leonz03.bilicinema;

import org.json.JSONObject;
import org.junit.Test;
import java.net.URI;
import java.util.Collections;
import static org.junit.Assert.*;

public class OfficialPlaybackSnapshotTest {
    private PlaybackSource source() throws Exception {
        return new PlaybackSource(new JSONObject().put("aid", 10).put("cid", 20).put("bvid", "BV1xx411c7mD").put("episodeId", 30),
                "Movie", 8400, PlaybackSource.Kind.OFFICIAL, URI.create("https://www.bilibili.com/bangumi/play/ep30"), null, Collections.emptyMap());
    }
    private JSONObject full() throws Exception {
        return new JSONObject().put("cid", 20).put("episodeId", 30).put("duration", 8400).put("position", 12)
                .put("readyState", 4).put("paused", false).put("quality", 80);
    }
    private void reject(JSONObject value, PlaybackFailure.Reason reason) throws Exception {
        try { new OfficialPlaybackSnapshot(value, source()); fail("Invalid player session became ready"); }
        catch (PlaybackFailure failure) { assertEquals(reason, failure.reason); }
    }
    @Test public void correctFullFilmCanPublishReadyWithoutOptionalBvid() throws Exception {
        OfficialPlaybackSnapshot sample = new OfficialPlaybackSnapshot(full(), source());
        assertTrue(sample.ready); assertTrue(sample.playing); assertEquals(80, sample.quality);
    }
    @Test public void orientationUsesIntrinsicVideoSizeOnlyAfterIdentityIsConfirmed() throws Exception {
        assertEquals(16f / 9f, new OfficialPlaybackSnapshot(full().put("videoWidth", 1920).put("videoHeight", 1080), source()).videoAspectRatio, .001f);
        assertEquals(9f / 16f, new OfficialPlaybackSnapshot(full().put("videoWidth", 720).put("videoHeight", 1280), source()).videoAspectRatio, .001f);
        assertEquals(0, new OfficialPlaybackSnapshot(full().put("videoWidth", 0).put("videoHeight", 0), source()).videoAspectRatio, 0);
        assertEquals(0, new OfficialPlaybackSnapshot(full().put("cid", 0).put("videoWidth", 1920).put("videoHeight", 1080), source()).videoAspectRatio, 0);
    }
    @Test public void playerWithoutConfirmedIdentityCannotPublishReady() throws Exception {
        JSONObject sample = full(); sample.remove("cid");
        assertFalse(new OfficialPlaybackSnapshot(sample, source()).ready);
    }
    @Test public void differentEpisodeOrCidAndOptionalIdentityFailClosed() throws Exception {
        reject(full().put("cid", 21), PlaybackFailure.Reason.INVALID_MEDIA);
        reject(full().put("episodeId", 31), PlaybackFailure.Reason.INVALID_MEDIA);
        reject(full().put("aid", 11), PlaybackFailure.Reason.INVALID_MEDIA);
        reject(full().put("bvid", "BV1GJ411x7h7"), PlaybackFailure.Reason.INVALID_MEDIA);
    }
    @Test public void previewCannotBecomeFullMovieEvenWithMatchingCid() throws Exception {
        reject(full().put("duration", 360), PlaybackFailure.Reason.PERMISSION_DENIED);
    }
    @Test public void loadingAndQualitySwitchDoNotPublishReady() throws Exception {
        assertFalse(new OfficialPlaybackSnapshot(full().put("duration", 0).put("readyState", 0), source()).ready);
        assertFalse(new OfficialPlaybackSnapshot(full().put("readyState", 2), source()).ready);
        assertFalse(new OfficialPlaybackSnapshot(full().put("qualityPending", true), source()).ready);
    }
    @Test public void officialLicenseOrDecoderFailureIsNotPlaybackSuccess() throws Exception {
        reject(full().put("error", 3), PlaybackFailure.Reason.DRM_PROTECTED);
        reject(full().put("initError", true), PlaybackFailure.Reason.DRM_PROTECTED);
    }
}
