package com.leonz03.bilicinema;

import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public class MediaSessionTest {
    private JSONObject identity(long cid) throws Exception {
        return new JSONObject().put("aid", 42).put("bvid", "BV1xx411c7mD").put("cid", cid);
    }
    private BilibiliMediaResolver.Resolved resolved(long cid) throws Exception {
        return new BilibiliMediaResolver.Resolved(identity(cid), "https://www.bilibili.com/video/BV1xx411c7mD?p=2");
    }
    private long bind(MediaSession session, long cid) throws Exception {
        long token = session.begin(identity(cid));
        assertTrue(session.resolved(token, resolved(cid))); assertTrue(session.bind(token, identity(cid)));
        return token;
    }
    @Test public void nativeReadyRequiresExactResolvedAndBoundIdentity() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202));
        assertFalse(session.prepared(token)); assertFalse(session.ready());
        assertTrue(session.resolved(token, resolved(202))); assertFalse(session.prepared(token));
        assertTrue(session.bind(token, identity(202))); assertTrue(session.prepared(token)); assertTrue(session.ready());
    }
    @Test public void differentPartCannotBecomeReady() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202));
        assertFalse(session.resolved(token, resolved(303))); assertEquals(MediaSession.State.ERROR, session.state());
        assertFalse(session.prepared(token)); assertFalse(session.ready());
    }
    @Test public void mismatchedNativeSourceCannotBecomeReady() throws Exception {
        MediaSession session = new MediaSession(); long token = session.begin(identity(202));
        assertTrue(session.resolved(token, resolved(202))); assertFalse(session.bind(token, identity(303)));
        assertFalse(session.prepared(token)); assertFalse(session.ready());
    }
    @Test public void sourceFromDifferentEpisodeIsRejected() throws Exception {
        MediaSession session = new MediaSession(); JSONObject media = identity(202).put("episodeId", 12);
        long token = session.begin(media);
        assertTrue(session.resolved(token, new BilibiliMediaResolver.Resolved(media, "https://www.bilibili.com/bangumi/play/ep12")));
        assertFalse(session.bind(token, identity(202).put("episodeId", 13))); assertFalse(session.ready());
    }
    @Test public void oldResolutionAndNativeCallbacksCannotAffectNewSelection() throws Exception {
        MediaSession session = new MediaSession(); long old = bind(session, 101); long next = bind(session, 202);
        assertFalse(session.prepared(old)); assertFalse(session.buffering(old)); assertFalse(session.fail(old));
        assertFalse(session.resolved(old, resolved(101))); assertTrue(session.prepared(next)); assertTrue(session.ready());
    }
    @Test public void exactNativeReadyCanRecoverFromTransientFailure() throws Exception {
        MediaSession session = new MediaSession(); long token = bind(session, 202);
        session.prepared(token); assertTrue(session.fail(token)); assertFalse(session.ready()); assertFalse(session.buffering());
        assertTrue(session.prepared(token)); assertTrue(session.ready());
    }
    @Test public void actualBufferingRevokesAndRestoresReadiness() throws Exception {
        MediaSession session = new MediaSession(); long token = bind(session, 202);
        session.prepared(token); session.buffering(token); assertFalse(session.ready()); assertTrue(session.buffering());
        session.prepared(token); assertTrue(session.ready()); assertFalse(session.buffering());
    }
    @Test public void foregroundReturnWaitsForActualPlayerReadiness() throws Exception {
        MediaSession session = new MediaSession(); long token = bind(session, 202); session.prepared(token);
        session.setForeground(false); assertFalse(session.ready()); session.prepared(token); assertFalse(session.ready());
        session.setForeground(true); assertFalse(session.ready()); session.prepared(token); assertTrue(session.ready());
    }
    @Test public void resetInvalidatesBoundSourceAndCallbacks() throws Exception {
        MediaSession session = new MediaSession(); long token = bind(session, 202); session.prepared(token); session.reset();
        assertEquals(MediaSession.State.EMPTY, session.state()); assertFalse(session.prepared(token)); assertFalse(session.ready()); assertFalse(session.bound());
    }
    @Test public void episodeOnlyRoomIdentityResolvesWithoutChangingRoomKey() throws Exception {
        MediaSession session = new MediaSession(); JSONObject selected = new JSONObject().put("episodeId", 12);
        long token = session.begin(selected); String key = session.key(); JSONObject actual = identity(202).put("episodeId", 12);
        assertTrue(session.resolved(token, new BilibiliMediaResolver.Resolved(actual, "https://www.bilibili.com/bangumi/play/ep12")));
        assertTrue(session.bind(token, actual)); assertTrue(session.prepared(token)); assertEquals(key, session.key());
    }
    @Test public void publicIdentityCopiesCannotMutateTheSelection() throws Exception {
        MediaSession session = new MediaSession(); JSONObject media = identity(202); session.begin(media);
        media.put("cid", 303); JSONObject copy = session.selected(); copy.put("cid", 404);
        assertEquals(202, session.selected().getLong("cid"));
    }
    @Test public void endedIsDistinctFromBuffering() throws Exception {
        MediaSession session = new MediaSession(); long token = bind(session, 202); session.prepared(token); session.ended(token);
        assertEquals(MediaSession.State.ENDED, session.state()); assertFalse(session.ready()); assertFalse(session.buffering());
        session.prepared(token); assertTrue(session.ready());
    }
    @Test public void latestLocalSeekAndRateWinTheFirstAlignmentExactlyOnce() throws Exception {
        MediaSession session = new MediaSession();
        long token = session.begin(identity(202), 120, 1, true);
        assertTrue(session.queueInitialSeek(token, 45));
        assertTrue(session.queueInitialRate(token, 2));
        assertTrue(session.resolved(token, resolved(202))); assertTrue(session.bind(token, identity(202)));
        session.prepared(token);
        MediaSession.InitialAlignment initial = session.takeInitialAlignment(token);
        assertNotNull(initial); assertEquals(45, initial.position, 0); assertEquals(2, initial.rate, 0);
        assertFalse(session.awaitingInitialAlignment()); assertNull(session.takeInitialAlignment(token));
        session.buffering(token); session.prepared(token);
        assertNull(session.takeInitialAlignment(token));
    }
    @Test public void rateOnlyIntentPreservesTheSavedInitialPosition() throws Exception {
        MediaSession session = new MediaSession();
        long token = session.begin(identity(202), 120, 1, true);
        assertTrue(session.queueInitialRate(token, 1.5));
        assertTrue(session.resolved(token, resolved(202))); assertTrue(session.bind(token, identity(202))); session.prepared(token);
        MediaSession.InitialAlignment initial = session.takeInitialAlignment(token);
        assertEquals(120, initial.position, 0); assertEquals(1.5, initial.rate, 0);
    }
    @Test public void oldGenerationCannotChangeOrConsumeNewInitialAlignment() throws Exception {
        MediaSession session = new MediaSession();
        long old = session.begin(identity(101), 80, 1, true);
        long current = session.begin(identity(202), 9, .5, true);
        assertFalse(session.queueInitialSeek(old, 30)); assertFalse(session.queueInitialRate(old, 2));
        assertNull(session.takeInitialAlignment(old));
        assertTrue(session.resolved(current, resolved(202))); assertTrue(session.bind(current, identity(202))); session.prepared(current);
        MediaSession.InitialAlignment initial = session.takeInitialAlignment(current);
        assertEquals(9, initial.position, 0); assertEquals(.5, initial.rate, 0);
    }
    @Test public void initialAlignmentWaitsForBoundForegroundReady() throws Exception {
        MediaSession session = new MediaSession();
        long token = session.begin(identity(202), 80, 1, true);
        assertNull(session.takeInitialAlignment(token));
        assertTrue(session.resolved(token, resolved(202))); assertTrue(session.bind(token, identity(202)));
        assertNull(session.takeInitialAlignment(token));
        session.setForeground(false); session.prepared(token);
        assertNull(session.takeInitialAlignment(token)); assertTrue(session.awaitingInitialAlignment());
        session.setForeground(true); session.prepared(token);
        assertEquals(80, session.takeInitialAlignment(token).position, 0);
    }
    @Test public void endedMemberAcceptsAPlayingRoomsSeekBeforeBecomingReadyAgain() throws Exception {
        MediaSession session = new MediaSession(); long token = bind(session, 202);
        session.prepared(token); session.ended(token);
        assertTrue(session.acceptsPausedTimeline(true));
        session.buffering(token);
        assertFalse(session.acceptsPausedTimeline(true)); // Loading must not repeatedly chase a moving target.
        session.prepared(token); assertTrue(session.ready());
    }
    @Test public void resetDropsTheOldInitialAlignmentAndUnboundSourcesCannotReposition() throws Exception {
        MediaSession session = new MediaSession();
        long token = session.begin(identity(202), 80, 1, true);
        assertFalse(session.acceptsPausedTimeline(true)); assertFalse(session.acceptsPausedTimeline(false));
        session.reset();
        assertFalse(session.awaitingInitialAlignment()); assertFalse(session.queueInitialSeek(token, 1));
        assertNull(session.takeInitialAlignment(token));
    }
}
