package com.leonz03.bilicinema;

import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public class BilibiliMediaResolverTest {
    private JSONObject view() throws Exception { return new JSONObject("{\"code\":0,\"data\":{\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"pages\":[{\"page\":1,\"cid\":101},{\"page\":2,\"cid\":202}]}}"); }
    @Test public void hostSelectionPublishesExactCidAndBothVideoIds() throws Exception {
        BilibiliMediaResolver.Resolved result = BilibiliMediaResolver.video(view(), new JSONObject().put("bvid", "BV1xx411c7mD"), 2);
        assertEquals(42, result.media.getLong("aid")); assertEquals(202, result.media.getLong("cid")); assertEquals("BV1xx411c7mD", result.media.getString("bvid"));
        assertEquals("https://www.bilibili.com/video/BV1xx411c7mD?p=2", result.pageUrl);
    }
    @Test public void guestUsesCidToRecoverCorrectPage() throws Exception {
        BilibiliMediaResolver.Resolved result = BilibiliMediaResolver.video(view(), new JSONObject().put("aid", 42).put("cid", 202), 0);
        assertTrue(result.pageUrl.endsWith("?p=2"));
    }
    @Test(expected = java.io.IOException.class) public void missingCidCannotSilentlyOpenPageOne() throws Exception { BilibiliMediaResolver.video(view(), new JSONObject().put("aid", 42).put("cid", 999), 0); }
    @Test(expected = java.io.IOException.class) public void mismatchedPublicIdentityFailsClosed() throws Exception { BilibiliMediaResolver.video(view(), new JSONObject().put("aid", 99).put("cid", 202), 0); }
    @Test public void episodePublishesVerifiedCompleteIdentity() throws Exception {
        JSONObject response = new JSONObject("{\"result\":{\"episodes\":[{\"id\":17,\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"cid\":202}]}}");
        BilibiliMediaResolver.Resolved result = BilibiliMediaResolver.episode(response, new JSONObject().put("episodeId", 17));
        assertEquals(17, result.media.getLong("episodeId")); assertEquals(202, result.media.getLong("cid")); assertTrue(result.pageUrl.endsWith("ep17"));
    }
    @Test(expected = java.io.IOException.class) public void differentEpisodeCannotReportReady() throws Exception { BilibiliMediaResolver.episode(new JSONObject("{\"result\":{\"episodes\":[]}}"), new JSONObject().put("episodeId", 17)); }
}
