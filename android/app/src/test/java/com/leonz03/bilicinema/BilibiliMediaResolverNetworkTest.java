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
    private static final String NAV = "{\"code\":-101,\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png\",\"sub_url\":\"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png\"}}}";
    private static final String PLAY = "{\"code\":0,\"data\":{\"format\":\"mp4\",\"timelength\":120000,\"durl\":[{\"url\":\"https://media.example.test/video.mp4?opaque=local\"}]}}";
    private BilibiliMediaResolver.Resolved selected() throws Exception {
        return new BilibiliMediaResolver.Resolved(new JSONObject().put("aid", 42).put("bvid", "BV1xx411c7mD").put("cid", 202), "https://www.bilibili.com/video/BV1xx411c7mD?p=2");
    }
    private Response fixture(Request request, String body) {
        return new Response.Builder().request(request).protocol(Protocol.HTTP_1_1).code(200).message("OK")
                .body(ResponseBody.create(body, MediaType.parse("application/json"))).build();
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
