package com.leonz03.bilicinema;

import org.json.JSONObject;
import org.junit.Test;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Protocol;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;
import static org.junit.Assert.*;

/** Interceptors return fixtures before network I/O; no real account or API connection is used. */
public class BilibiliMediaResolverNetworkTest {
    @Test public void previewCoverIsBoundedAndNeverReceivesAccountCookies() throws Exception {
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            assertNull(chain.request().header("Cookie"));
            assertTrue(chain.request().url().isHttps());
            return new Response.Builder().request(chain.request()).protocol(Protocol.HTTP_1_1).code(200).message("OK")
                    .body(ResponseBody.create(new byte[1024 * 1024 + 1], MediaType.get("image/png"))).build();
        }).build());
        try {
            BilibiliMediaResolver.Resolved resolved = new BilibiliMediaResolver.Resolved(new JSONObject(), "", "", "https://i0.hdslb.com/cover.png", 0);
            try { resolver.cover(resolved); fail("Unbounded artwork was accepted"); }
            catch (java.io.IOException expected) { assertEquals("Cover too large", expected.getMessage()); }
        } finally { resolver.close(); }
    }
    @Test public void previewCoverRejectsUntrustedOriginsBeforeRequest() throws Exception {
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> { throw new AssertionError("Unexpected artwork request"); }).build());
        try {
            for (String url : new String[]{"https://hdslb.com.example.test/cover.png", "https://local.invalid/cover.png", "https://user@i0.hdslb.com/cover.png"}) {
                try { resolver.cover(new BilibiliMediaResolver.Resolved(new JSONObject(), "", "", url, 0)); fail("Untrusted artwork accepted"); }
                catch (java.io.IOException expected) { assertEquals("Cover unavailable", expected.getMessage()); }
            }
        } finally { resolver.close(); }
    }
    private static final String NAV = "{\"code\":-101,\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png\",\"sub_url\":\"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png\"}}}";
    private static final String PLAY = "{\"code\":0,\"data\":{\"format\":\"mp4\",\"timelength\":120000,\"durl\":[{\"url\":\"https://media.example.test/video.mp4?opaque=local\"}]}}";
    private BilibiliMediaResolver.Resolved selected() throws Exception {
        return new BilibiliMediaResolver.Resolved(new JSONObject().put("aid", 42).put("bvid", "BV1xx411c7mD").put("cid", 202), "https://www.bilibili.com/video/BV1xx411c7mD?p=2");
    }
    private Response fixture(Request request, String body) {
        return new Response.Builder().request(request).protocol(Protocol.HTTP_1_1).code(200).message("OK")
                .body(ResponseBody.create(body, MediaType.parse("application/json"))).build();
    }

    private Response redirect(Request request, String location) {
        return new Response.Builder().request(request).protocol(Protocol.HTTP_1_1).code(302).message("Found")
                .header("Location", location).body(ResponseBody.create(new byte[0], null)).build();
    }

    private PlaybackFailure.Reason inputFailure(OkHttpClient transport, String input) throws Exception {
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(transport);
        try { resolver.input(input, ""); fail("Input unexpectedly resolved"); return null; }
        catch (PlaybackFailure failure) { return failure.reason; }
        finally { resolver.close(); }
    }

    @Test public void pastedShareTextKeepsPartNumberAndIgnoresTrailingChinesePunctuation() throws Exception {
        AtomicInteger views = new AtomicInteger();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request();
            if (request.url().encodedPath().endsWith("/view")) {
                views.incrementAndGet();
                assertEquals("BV1xx411c7mD", request.url().queryParameter("bvid"));
                return fixture(request, "{\"code\":0,\"data\":{\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"pages\":[{\"page\":1,\"cid\":101},{\"page\":2,\"cid\":202}]}} ");
            }
            throw new AssertionError("Unexpected endpoint");
        }).build());
        try {
            BilibiliMediaResolver.Resolved resolved = resolver.input(
                    "【哔哩哔哩】标题提到旧 BV1zz411c7mD，推荐说明 p=999。很长的中文分享文字和推荐语。https://www.bilibili.com/video/BV1xx411c7mD/?p=2。复制打开哔哩哔哩观看！", "");
            assertEquals(202, resolved.media.getLong("cid"));
            assertTrue(resolved.pageUrl.endsWith("?p=2"));
            assertEquals(1, views.get());
        } finally { resolver.close(); }
    }

    @Test public void shortShareLinkExpandsWithoutForwardingApiCookieAndKeepsRedirectedPart() throws Exception {
        List<Request> requests = new ArrayList<>();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request(); requests.add(request);
            if ("b23.tv".equals(request.url().host())) {
                return redirect(request, "https://www.bilibili.com/video/BV1xx411c7mD/?p=2");
            }
            return fixture(request, "{\"code\":0,\"data\":{\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"pages\":[{\"page\":1,\"cid\":101},{\"page\":2,\"cid\":202}]}} ");
        }).build());
        try {
            BilibiliMediaResolver.Resolved resolved = resolver.input("【视频】http://b23.tv/AbC123，复制打开", "SESSDATA=fixture-only");
            assertEquals(202, resolved.media.getLong("cid"));
            assertTrue(resolved.pageUrl.endsWith("?p=2"));
            assertEquals(2, requests.size());
            assertEquals("https", requests.get(0).url().scheme());
            assertNull(requests.get(0).header("Cookie"));
            assertEquals("SESSDATA=fixture-only", requests.get(1).header("Cookie"));
        } finally { resolver.close(); }
    }

    @Test public void appEpisodeShareAndWebLinksReachTheSamePlaybackWhenShortHostIsUnavailable() throws Exception {
        AtomicInteger shortRequests = new AtomicInteger();
        List<String> playbackRequests = new ArrayList<>();
        String episodePlayback = "{\"code\":0,\"result\":{\"video_info\":" + new JSONObject(PLAY).getJSONObject("data") + "}}";
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request();
            if ("b23.tv".equals(request.url().host())) {
                shortRequests.incrementAndGet();
                throw new java.io.IOException("Short host unavailable in fixture");
            }
            assertEquals("api.bilibili.com", request.url().host());
            assertEquals("SESSDATA=fixture-only", request.header("Cookie"));
            if (request.url().encodedPath().equals("/pgc/view/web/season")) {
                assertEquals("251429", request.url().queryParameter("ep_id"));
                return fixture(request, "{\"code\":0,\"result\":{\"title\":\"猫鼠游戏\",\"episodes\":[{\"id\":251429,\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"cid\":202}]}}");
            }
            assertEquals("/pgc/player/web/v2/playurl", request.url().encodedPath());
            playbackRequests.add(request.url().toString());
            return fixture(request, episodePlayback);
        }).build());
        try {
            String[] inputs = {
                "https://www.bilibili.com/bangumi/play/ep251429?theme=movie&",
                "【猫鼠游戏】 https://www.bilibili.com/bangumi/play/ep251429/?share_source=copy_web",
                "【《猫鼠游戏》-哔哩哔哩电影】https://b23.tv/ep251429",
                "标题有旧 BV1zz411c7mD 和 ep17 http://b23.tv/ep251429/?share_source=copy_link。复制打开"
            };
            for (String input : inputs) {
                BilibiliMediaResolver.Resolved resolved = resolver.input(input, "SESSDATA=fixture-only");
                assertEquals(251429, resolved.media.getLong("episodeId"));
                assertEquals(202, resolved.media.getLong("cid"));
                assertEquals("https://www.bilibili.com/bangumi/play/ep251429", resolved.pageUrl);
                PlaybackSource source = resolver.playable(resolved, "SESSDATA=fixture-only");
                assertEquals(PlaybackSource.Kind.PROGRESSIVE, source.kind);
                assertEquals(resolved.pageUrl, source.headers.get("Referer"));
                assertEquals(251429, source.media().getLong("episodeId"));
            }
            assertEquals(0, shortRequests.get());
            assertEquals(inputs.length, playbackRequests.size());
            for (String request : playbackRequests) assertEquals(playbackRequests.get(0), request);
        } finally { resolver.close(); }
    }

    @Test public void explicitVideoShortLinksKeepPartWithoutShortHostLookup() throws Exception {
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request();
            assertEquals("api.bilibili.com", request.url().host());
            assertEquals("/x/web-interface/view", request.url().encodedPath());
            assertTrue("BV1xx411c7mD".equals(request.url().queryParameter("bvid")) || "42".equals(request.url().queryParameter("aid")));
            return fixture(request, "{\"code\":0,\"data\":{\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"pages\":[{\"page\":1,\"cid\":101},{\"page\":2,\"cid\":202}]}}");
        }).build());
        try {
            for (String input : new String[]{"【视频】https://b23.tv/BV1xx411c7mD?p=%32", "b23.tv/av42/?p=2#ignored"}) {
                BilibiliMediaResolver.Resolved resolved = resolver.input(input, "");
                assertEquals(202, resolved.media.getLong("cid"));
                assertEquals("https://www.bilibili.com/video/BV1xx411c7mD?p=2", resolved.pageUrl);
            }
        } finally { resolver.close(); }
    }

    @Test public void opaqueShortCodeBeginningWithEpisodeDigitsStillUsesRedirectIdentity() throws Exception {
        AtomicInteger shortRequests = new AtomicInteger();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request();
            if ("b23.tv".equals(request.url().host())) {
                shortRequests.incrementAndGet();
                return redirect(request, "https://b23.tv/ep17");
            }
            assertEquals("17", request.url().queryParameter("ep_id"));
            return fixture(request, "{\"code\":0,\"result\":{\"episodes\":[{\"id\":17,\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"cid\":202}]}}");
        }).build());
        try {
            assertEquals(17, resolver.input("【ep251429】https://b23.tv/ep251429extra", "").media.getLong("episodeId"));
            assertEquals(1, shortRequests.get());
        } finally { resolver.close(); }
    }

    @Test public void urlPathOwnsIdentityAndQueryOwnsThePageNumber() throws Exception {
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request();
            assertEquals("/pgc/view/web/season", request.url().encodedPath());
            assertEquals("17", request.url().queryParameter("ep_id"));
            return fixture(request, "{\"code\":0,\"result\":{\"episodes\":[{\"id\":17,\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"cid\":202}]}}");
        }).build());
        try {
            BilibiliMediaResolver.Resolved result = resolver.input("分享 http://www.bilibili.com/bangumi/play/ep17?from=BV1GJ411x7h7#p=999。推荐语", "");
            assertEquals(17, result.media.getLong("episodeId"));
        } finally { resolver.close(); }
    }

    @Test public void encodedPageQueryIsParsedWithoutReadingFragmentOrShareText() throws Exception {
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain ->
            fixture(chain.request(), "{\"code\":0,\"data\":{\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"pages\":[{\"page\":1,\"cid\":101},{\"page\":2,\"cid\":202}]}}")
        ).build());
        try {
            assertEquals(202, resolver.input("分享 https://www.bilibili.com/video/BV1xx411c7mD?p=%32#note?p=999，推荐语", "").media.getLong("cid"));
            assertEquals(101, resolver.input("分享 https://www.bilibili.com/video/BV1xx411c7mD#note?p=999，推荐语", "").media.getLong("cid"));
        } finally { resolver.close(); }
    }

    @Test public void shortShareLinkRequiresRedirectAndDoesNotParseTitleAsFallback() throws Exception {
        AtomicInteger requests = new AtomicInteger();
        PlaybackFailure.Reason reason = inputFailure(new OkHttpClient.Builder().addInterceptor(chain -> {
            requests.incrementAndGet();
            return fixture(chain.request(), "分享标题 BV1xx411c7mD，没有跳转");
        }).build(), "标题 BV1xx411c7mD https://b23.tv/AbC123，继续说明");
        assertEquals(PlaybackFailure.Reason.INVALID_MEDIA, reason);
        assertEquals(1, requests.get());
    }

    @Test public void shortShareLinkRejectsOffsiteAndExcessiveRedirects() throws Exception {
        PlaybackFailure.Reason offsite = inputFailure(new OkHttpClient.Builder().addInterceptor(chain ->
                redirect(chain.request(), "https://example.test/video/BV1xx411c7mD")).build(), "https://b23.tv/AbC123");
        assertEquals(PlaybackFailure.Reason.INVALID_MEDIA, offsite);

        AtomicInteger hops = new AtomicInteger();
        PlaybackFailure.Reason excessive = inputFailure(new OkHttpClient.Builder().addInterceptor(chain -> {
            hops.incrementAndGet(); return redirect(chain.request(), "/next");
        }).build(), "b23.tv/AbC123");
        assertEquals(PlaybackFailure.Reason.INVALID_MEDIA, excessive);
        assertEquals(5, hops.get());
    }

    @Test public void shortShareLinkServerFailureRemainsRetryable() throws Exception {
        PlaybackFailure.Reason reason = inputFailure(new OkHttpClient.Builder().addInterceptor(chain ->
                new Response.Builder().request(chain.request()).protocol(Protocol.HTTP_1_1).code(503).message("Unavailable")
                        .body(ResponseBody.create(new byte[0], null)).build()).build(), "https://b23.tv/AbC123");
        assertEquals(PlaybackFailure.Reason.NETWORK_RETRYABLE, reason);
    }

    @Test public void cancellingShortLinkLookupCancelsTheCurrentOperation() throws Exception {
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            entered.countDown();
            try { if (!release.await(5, TimeUnit.SECONDS)) throw new java.io.IOException("Fixture wait timed out"); }
            catch (InterruptedException exception) { Thread.currentThread().interrupt(); throw new java.io.IOException("Fixture interrupted"); }
            return redirect(chain.request(), "https://www.bilibili.com/video/BV1xx411c7mD");
        }).build());
        ExecutorService worker = Executors.newSingleThreadExecutor();
        try {
            Future<BilibiliMediaResolver.Resolved> result = worker.submit(() -> resolver.input("https://b23.tv/AbC123", ""));
            assertTrue(entered.await(5, TimeUnit.SECONDS));
            resolver.cancel(); release.countDown();
            try { result.get(5, TimeUnit.SECONDS); fail("Cancelled operation completed"); }
            catch (ExecutionException failure) { assertTrue(failure.getCause() instanceof CancellationException); }
        } finally { release.countDown(); resolver.close(); worker.shutdownNow(); }
    }

    @Test public void episodeWebPlaybackKeepsADesktopProfileThroughTheMediaRequest() throws Exception {
        List<Request> requests = new ArrayList<>();
        String episodePayload = "{\"code\":0,\"result\":{\"video_info\":" + new JSONObject(PLAY).getJSONObject("data") + "}}";
        OkHttpClient transport = new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request(); requests.add(request);
            String agent = request.header("User-Agent");
            assertNotNull(agent);
            assertTrue(agent.contains("AppleWebKit/") && agent.contains("Chrome/") && agent.contains("Safari/"));
            assertFalse(agent.contains("Android") || agent.contains("Mobile"));
            if (request.url().encodedPath().equals("/pgc/view/web/season")) {
                return fixture(request, "{\"code\":0,\"result\":{\"episodes\":[{\"id\":17,\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"cid\":202}]}}");
            }
            assertEquals("/pgc/player/web/v2/playurl", request.url().encodedPath());
            return fixture(request, episodePayload);
        }).build();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(transport);
        try {
            PlaybackSource source = resolver.playable(resolver.input("https://www.bilibili.com/bangumi/play/ep17", ""), "");
            assertEquals(17, source.media().getLong("episodeId"));
            assertEquals(requests.get(0).header("User-Agent"), source.headers.get("User-Agent"));
            assertEquals(requests.get(1).header("User-Agent"), source.headers.get("User-Agent"));
            assertFalse(source.headers.containsKey("Cookie"));
        } finally { resolver.close(); }
    }

    @Test public void cookieStaysOnApiRequestsAndAnonymousKeysRemainUsable() throws Exception {
        List<Request> requests = new ArrayList<>();
        OkHttpClient transport = new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request(); requests.add(request);
            return fixture(request, request.url().encodedPath().endsWith("/nav") ? NAV : PLAY);
        }).build();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(transport);
        try {
            PlaybackSource source = resolver.playable(selected(), "SESSDATA=fixture-only");
            assertEquals(2, requests.size());
            for (Request request : requests) {
                assertEquals("https", request.url().scheme());
                assertEquals("api.bilibili.com", request.url().host());
                assertEquals("SESSDATA=fixture-only", request.header("Cookie"));
            }
            assertNotNull(requests.get(1).url().queryParameter("w_rid"));
            assertEquals("202", requests.get(1).url().queryParameter("cid"));
            assertFalse(source.headers.containsKey("Cookie"));
            assertFalse(source.media().toString().contains("fixture-only"));
        } finally { resolver.close(); }
    }

    @Test public void bareBvidResolvesAndObtainsPlayableSourceWithAnonymousPublicFixtures() throws Exception {
        List<Request> requests = new ArrayList<>();
        OkHttpClient transport = new OkHttpClient.Builder().addInterceptor(chain -> {
            Request request = chain.request(); requests.add(request);
            String path = request.url().encodedPath();
            if (path.endsWith("/view")) {
                return fixture(request, "{\"code\":0,\"data\":{\"aid\":42,\"bvid\":\"BV1xx411c7mD\",\"pages\":[{\"page\":1,\"cid\":101},{\"page\":2,\"cid\":202}]}} ");
            }
            if (path.endsWith("/nav")) return fixture(request, NAV);
            if (path.endsWith("/playurl")) return fixture(request, PLAY);
            throw new AssertionError("Unexpected endpoint");
        }).build();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(transport);
        try {
            BilibiliMediaResolver.Resolved resolved = resolver.input("BV1xx411c7mD", "");
            PlaybackSource source = resolver.playable(resolved, "");
            assertEquals(101, source.media().getLong("cid"));
            assertEquals(PlaybackSource.Kind.PROGRESSIVE, source.kind);
            assertEquals("media.example.test", source.videoUri.getHost());
            assertEquals(3, requests.size());
            assertEquals("api.bilibili.com", requests.get(0).url().host());
            assertEquals("api.bilibili.com", requests.get(1).url().host());
            assertEquals("api.bilibili.com", requests.get(2).url().host());
            assertNull(requests.get(0).header("Cookie"));
            assertNull(requests.get(1).header("Cookie"));
            assertNull(requests.get(2).header("Cookie"));
        } finally { resolver.close(); }
    }

    @Test public void cancelDuringNavCannotStartTheNextRequestAndResolverCanBeReused() throws Exception {
        AtomicInteger count = new AtomicInteger();
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        OkHttpClient transport = new OkHttpClient.Builder().addInterceptor(chain -> {
            if (count.incrementAndGet() == 1) {
                entered.countDown();
                try { if (!release.await(5, TimeUnit.SECONDS)) throw new java.io.IOException("Fixture wait timed out"); }
                catch (InterruptedException exception) { Thread.currentThread().interrupt(); throw new java.io.IOException("Fixture interrupted"); }
            }
            return fixture(chain.request(), chain.request().url().encodedPath().endsWith("/nav") ? NAV : PLAY);
        }).build();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(transport);
        ExecutorService worker = Executors.newSingleThreadExecutor();
        try {
            BilibiliMediaResolver.Resolved selected = selected();
            Future<PlaybackSource> result = worker.submit(() -> resolver.playable(selected, ""));
            assertTrue(entered.await(5, TimeUnit.SECONDS));
            resolver.cancel(); release.countDown();
            try { result.get(5, TimeUnit.SECONDS); fail("Cancelled operation completed"); }
            catch (ExecutionException failure) { assertTrue(failure.getCause() instanceof CancellationException); }
            assertEquals(1, count.get());
            assertNotNull(resolver.playable(selected, ""));
            assertEquals(3, count.get());
        } finally { release.countDown(); resolver.close(); worker.shutdownNow(); }
    }

    @Test public void malformedCookieFailsWithFixedMessageBeforeTransport() throws Exception {
        AtomicInteger count = new AtomicInteger();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            count.incrementAndGet(); return fixture(chain.request(), NAV);
        }).build());
        try {
            try { resolver.playable(selected(), "SESSDATA=fixture\r\nInjected: fake"); fail("Malformed cookie accepted"); }
            catch (PlaybackFailure failure) {
                assertEquals(PlaybackFailure.Reason.NEEDS_LOGIN, failure.reason);
                assertFalse(failure.safeMessage().contains("fixture"));
            }
            assertEquals(0, count.get());
        } finally { resolver.close(); }
    }

    @Test public void rejectedWbiSignatureRefreshesKeysOnlyOnce() throws Exception {
        AtomicInteger navs = new AtomicInteger(), playUrls = new AtomicInteger();
        BilibiliMediaResolver resolver = new BilibiliMediaResolver(new OkHttpClient.Builder().addInterceptor(chain -> {
            if (chain.request().url().encodedPath().endsWith("/nav")) {
                navs.incrementAndGet(); return fixture(chain.request(), NAV);
            }
            return fixture(chain.request(), playUrls.incrementAndGet() == 1 ? "{\"code\":-403}" : PLAY);
        }).build());
        try {
            assertNotNull(resolver.playable(selected(), ""));
            assertEquals(2, navs.get()); assertEquals(2, playUrls.get());
        } finally { resolver.close(); }
    }
}
