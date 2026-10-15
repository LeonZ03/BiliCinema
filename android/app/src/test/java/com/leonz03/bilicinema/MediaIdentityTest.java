package com.leonz03.bilicinema;

import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public class MediaIdentityTest {
    @Test public void sharedLinksOnlyExtractPublicIdentifiers() throws Exception {
        JSONObject media = MediaIdentity.parse("分享 https://www.bilibili.com/video/BV1xx411c7mD?cookie=private");
        assertEquals(1, media.length()); assertEquals("BV1xx411c7mD", media.getString("bvid"));
        assertEquals(12345, MediaIdentity.parse("https://www.bilibili.com/bangumi/play/ep12345").getLong("episodeId"));
    }
    @Test(expected = IllegalArgumentException.class) public void rejectsArbitraryPages() throws Exception { MediaIdentity.parse("https://example.org/account"); }
    @Test public void playbackUsesTimestampAndRateOnlyWhilePlaying() throws Exception {
        JSONObject sample = new JSONObject().put("positionSeconds", 20).put("playing", true).put("rate", 1.5).put("serverTimeUnixMs", 1000);
        assertEquals(23, MediaIdentity.targetPosition(sample, 3000, 0), .001);
        sample.put("playing", false); assertEquals(20, MediaIdentity.targetPosition(sample, 3000, 0), .001);
    }
    @Test public void staleFutureTimeNeverRewindsBasePosition() throws Exception {
        JSONObject sample = new JSONObject().put("positionSeconds", 20).put("playing", true).put("serverTimeUnixMs", 5000);
        assertEquals(20, MediaIdentity.targetPosition(sample, 1000, 0), .001);
    }
}
