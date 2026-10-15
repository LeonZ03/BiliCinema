package com.leonz03.bilicinema;

import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public class MediaSessionTest {
    private JSONObject identity(long cid) throws Exception { return new JSONObject().put("aid", 42).put("bvid", "BV1xx411c7mD").put("cid", cid); }
    private BilibiliMediaResolver.Resolved resolved(long cid, int page) throws Exception { return new BilibiliMediaResolver.Resolved(identity(cid), "https://www.bilibili.com/video/BV1xx411c7mD?p=" + page); }
    @Test public void readyRequiresResolvedPageAndPositiveMatchingPlayerCid() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202)); session.resolved(token, resolved(202, 2));
        assertEquals(MediaSession.Decision.WAITING, session.observe(token, session.pageUrl(), 0, "probe", 4, true)); assertFalse(session.ready());
        assertEquals(MediaSession.Decision.WAITING, session.observe(token, "https://www.bilibili.com/video/BV1xx411c7mD?p=1", 101, "probe", 4, true)); assertFalse(session.ready());
        assertEquals(MediaSession.Decision.ACCEPT, session.observe(token, session.pageUrl(), 202, "probe", 4, true)); assertTrue(session.ready());
    }
    @Test public void spaCidChangeRevokesPreviouslyPublishedReadiness() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202)); session.resolved(token, resolved(202, 2)); session.observe(token, session.pageUrl(), 202, "probe", 4, true);
        assertEquals(MediaSession.Decision.CHANGED, session.observe(token, session.pageUrl(), 303, "probe", 4, true)); assertFalse(session.ready()); assertEquals(MediaSession.State.CHANGED, session.state());
    }
    @Test public void unknownCidAfterVerificationRevokesReadiness() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202)); session.resolved(token, resolved(202, 2)); session.observe(token, session.pageUrl(), 202, "probe", 4, true);
        assertEquals(MediaSession.Decision.CHANGED, session.observe(token, session.pageUrl(), 0, "probe", 4, true)); assertFalse(session.ready());
    }
    @Test public void oldResolutionAndCallbacksCannotPublishIntoNextSelection() throws Exception {
        MediaSession session = new MediaSession(); long first = session.begin(identity(101)); long next = session.begin(identity(202));
        assertFalse(session.resolved(first, resolved(101, 1))); assertTrue(session.resolved(next, resolved(202, 2)));
        assertEquals(MediaSession.Decision.IGNORE, session.observe(first, "https://www.bilibili.com/video/BV1xx411c7mD?p=1", 101, "probe", 4, true)); assertFalse(session.ready());
    }
    @Test public void navigationAndBackgroundHaveExplicitReadinessTransitions() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202)); session.resolved(token, resolved(202, 2)); session.observe(token, session.pageUrl(), 202, "probe", 4, true);
        session.suspend(); assertFalse(session.ready()); session.observe(token, session.pageUrl(), 202, "probe", 4, true); assertTrue(session.ready());
        assertEquals(MediaSession.Decision.CHANGED, session.navigating("https://www.bilibili.com/video/BV1xx411c7mD?p=3")); assertFalse(session.ready());
    }
    @Test public void trustedOriginHasNoWildcardOrPortBypass() { assertTrue(MediaSession.trustedOrigin("https", "www.bilibili.com", 443)); assertFalse(MediaSession.trustedOrigin("https", "evil.bilibili.com", 443)); assertFalse(MediaSession.trustedOrigin("http", "www.bilibili.com", 80)); assertFalse(MediaSession.trustedOrigin("https", "www.bilibili.com", 444)); }
    @Test public void samePageReloadInvalidatesOldDocumentCallbacks() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202)); session.resolved(token, resolved(202, 2)); session.observe(token, session.pageUrl(), 202, "probe", 4, true);
        session.navigating(session.pageUrl()); assertFalse(session.ready());
        assertEquals(MediaSession.Decision.IGNORE, session.observe(token, session.pageUrl(), 202, "probe", 4, true)); assertFalse(session.ready());
        assertEquals(MediaSession.Decision.ACCEPT, session.observe(session.generation(), session.pageUrl(), 202, "probe", 4, true)); assertTrue(session.ready());
    }
    @Test public void bufferingRecoveryIsDerivedFromVerifiedPlayerEvents() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202)); session.resolved(token, resolved(202, 2)); session.observe(token, session.pageUrl(), 202, "probe", 4, true);
        session.observe(token, session.pageUrl(), 202, "probe", 4, true); assertFalse(session.buffering()); session.observe(token, session.pageUrl(), 202, "waiting", 2, true); assertTrue(session.buffering()); session.observe(token, session.pageUrl(), 202, "probe", 4, true); assertFalse(session.buffering());
    }
    @Test public void metadataAndCanplayWithoutFutureDataCannotPublishReady() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202)); session.resolved(token, resolved(202, 2));
        assertEquals(MediaSession.Decision.ACCEPT, session.observe(token, session.pageUrl(), 202, "metadata", 1, true)); assertFalse(session.ready()); assertTrue(session.buffering()); assertTrue(session.awaitingPreparation());
        session.observe(token, session.pageUrl(), 202, "ready", 2, true); assertFalse(session.ready()); assertTrue(session.buffering());
        session.observe(token, session.pageUrl(), 202, "ready", 3, true); assertTrue(session.ready()); assertFalse(session.buffering()); assertFalse(session.awaitingPreparation());
    }
    @Test public void lostFutureDataRevokesReadinessWithoutChangingMediaIdentity() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202)); session.resolved(token, resolved(202, 2)); session.observe(token, session.pageUrl(), 202, "probe", 4, true);
        session.observe(token, session.pageUrl(), 202, "probe", 1, true); assertFalse(session.ready()); assertTrue(session.buffering()); assertEquals(MediaSession.State.VERIFIED, session.state());
        session.observe(token, session.pageUrl(), 202, "probe", 3, true); assertTrue(session.ready()); assertFalse(session.buffering());
    }
}
