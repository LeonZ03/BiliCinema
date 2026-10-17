package com.leonz03.bilicinema;

import java.io.IOException;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.Cookie;
import okhttp3.MediaType;
import okhttp3.Protocol;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;
import okio.Timeout;
import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public class BilibiliLoginClientTest {
    private static final String GENERATE = "/x/passport-login/web/qrcode/generate";
    private static final String POLL = "/x/passport-login/web/qrcode/poll";
    private static final String NAV = "/x/web-interface/nav";
    private static final String CALLBACK = "/fixture-callback";
    private static final String TERMINAL = "https://passport.biligame.com/x/passport-login/web/crossDomain";
    private static final String LANDING = "https://www.bilibili.com" + CALLBACK;
    private static final String GENERATED = "{\"code\":0,\"data\":{\"url\":\"https://account.bilibili.com/h5/fixture?qrcode_key=fixture-key\",\"qrcode_key\":\"fixture-key\"}}";
    private static final String LOGGED_IN = "{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":7}}";

    @Test public void qrStatesKeepGenerateAndPollInTheSameIsolatedAttempt() throws Exception {
        try (Fixture fixture = new Fixture()) {
            fixture.add(GENERATE, GENERATED, "Set-Cookie", "attempt=fixture-attempt; Path=/; Secure");
            BilibiliLoginClient.Challenge challenge = fixture.client.generate();
            BilibiliLoginClient.ScanState[] states = {BilibiliLoginClient.ScanState.WAITING,
                BilibiliLoginClient.ScanState.SCANNED, BilibiliLoginClient.ScanState.EXPIRED};
            int[] codes = {86101, 86090, 86038};
            for (int i = 0; i < codes.length; i++) {
                fixture.add(POLL, scan(codes[i], null));
                BilibiliLoginClient.ScanResult result = fixture.client.poll(challenge);
                assertEquals(states[i], result.state); assertTrue(result.cookies.isEmpty()); assertNull(result.account);
                Request request = fixture.last();
                assertEquals("fixture-key", request.url().queryParameter("qrcode_key"));
                assertEquals("attempt=fixture-attempt", request.header("Cookie"));
            }
            assertTrue(challenge.address.startsWith("https://account.bilibili.com/"));
            fixture.finished();
        }
    }

    @Test public void pollResponseSessionDoesNotRequireALegacyCallbackUrl() throws Exception {
        for (String address : new String[]{"", "legacy-field"}) {
            try (Fixture fixture = new Fixture()) {
                BilibiliLoginClient.Challenge challenge = fixture.challenge();
                fixture.add(POLL, scan(0, address), "Set-Cookie", parent("SESSDATA=fixture-response"));
                fixture.add(NAV, LOGGED_IN);
                BilibiliLoginClient.ScanResult result = fixture.client.poll(challenge);
                assertEquals(BilibiliLoginClient.ScanState.VERIFIED, result.state);
                assertTrue(result.account.loggedIn()); assertEquals(3, fixture.requests.size());
                assertEquals("SESSDATA=fixture-response", fixture.last().header("Cookie"));
                fixture.finished();
            }
        }
    }

    @Test public void responseCookiesWinPerNameAndLegacyOnlyFillsMissingValues() throws Exception {
        try (Fixture fixture = new Fixture()) {
            BilibiliLoginClient.Challenge challenge = fixture.challenge();
            fixture.add(POLL, scan(0, LANDING + "?SESSDATA=fixture%252Fonce&bili_jct=%0A&DedeUserID=fixture-query"),
                "Set-Cookie", parent("bili_jct=fixture-poll"));
            fixture.add(CALLBACK, "", "Set-Cookie", parent("bili_jct=fixture-response"));
            fixture.add(NAV, LOGGED_IN);
            BilibiliLoginClient.ScanResult result = fixture.client.poll(challenge);
            assertEquals("fixture-response", value(result, "bili_jct"));
            assertEquals("fixture%2Fonce", value(result, "SESSDATA"));
            assertEquals("fixture-query", value(result, "DedeUserID"));
            String sent = fixture.last().header("Cookie");
            assertTrue(sent.contains("bili_jct=fixture-response")); assertFalse(sent.contains("fixture-poll"));
            fixture.finished();
        }
    }

    @Test public void responseDeletionCannotBeResurrectedByALegacyQuery() throws Exception {
        try (Fixture fixture = new Fixture()) {
            BilibiliLoginClient.Challenge challenge = fixture.challenge();
            fixture.add(POLL, scan(0, LANDING + "?SESSDATA=fixture-query&bili_jct=fixture-old"),
                "Set-Cookie", parent("bili_jct=; Max-Age=0"));
            fixture.add(CALLBACK, ""); fixture.add(NAV, LOGGED_IN);
            BilibiliLoginClient.ScanResult result = fixture.client.poll(challenge);
            assertNull(value(result, "bili_jct"));
            assertEquals("SESSDATA=fixture-query", fixture.last().header("Cookie"));
            fixture.finished();
        }
    }

    @Test public void deletedSessionFailsBeforeNavInsteadOfUsingTheLegacySession() throws Exception {
        try (Fixture fixture = new Fixture()) {
            BilibiliLoginClient.Challenge challenge = fixture.challenge();
            fixture.add(POLL, scan(0, LANDING + "?SESSDATA=fixture-old"),
                "Set-Cookie", parent("SESSDATA=; Max-Age=0"));
            fixture.add(CALLBACK, "");
            expectFailure(() -> fixture.client.poll(challenge));
            assertEquals(3, fixture.requests.size()); fixture.finished();
        }
    }

    @Test public void redirectTerminalIsAcceptedWithoutRequestingBiligame() throws Exception {
        try (Fixture fixture = new Fixture()) {
            BilibiliLoginClient.Challenge challenge = fixture.challenge();
            fixture.add(POLL, scan(0, LANDING + "?SESSDATA=fixture-query"));
            fixture.add(CALLBACK, 302, "", "Location", TERMINAL);
            fixture.add(NAV, LOGGED_IN);
            assertTrue(fixture.client.poll(challenge).account.loggedIn());
            assertEquals(4, fixture.requests.size()); fixture.noBiligameRequests(); fixture.finished();
        }
    }

    @Test public void initialTerminalIsAcceptedWithoutRequestingBiligame() throws Exception {
        try (Fixture fixture = new Fixture()) {
            BilibiliLoginClient.Challenge challenge = fixture.challenge();
            fixture.add(POLL, scan(0, TERMINAL + "?SESSDATA=fixture-query"));
            fixture.add(NAV, LOGGED_IN);
            assertTrue(fixture.client.poll(challenge).account.loggedIn());
            assertEquals(3, fixture.requests.size()); fixture.noBiligameRequests(); fixture.finished();
        }
    }

    @Test public void terminalLookalikesAreRejectedBeforeAnyOutsideRequest() throws Exception {
        for (String address : new String[]{"https://passport.biligame.com/crossDomain",
                "http://passport.biligame.com/x/passport-login/web/crossDomain",
                "https://passport.biligame.com:444/x/passport-login/web/crossDomain",
                "https://fixture@passport.biligame.com/x/passport-login/web/crossDomain",
                "https://passport.biligame.com.example.test/x/passport-login/web/crossDomain"}) {
            try (Fixture fixture = new Fixture()) {
                BilibiliLoginClient.Challenge challenge = fixture.challenge();
                fixture.add(POLL, scan(0, LANDING + "?SESSDATA=fixture-query"));
                fixture.add(CALLBACK, 302, "", "Location", address);
                expectFailure(() -> fixture.client.poll(challenge));
                assertEquals(3, fixture.requests.size()); fixture.noBiligameRequests(); fixture.finished();
            }
        }
    }

    @Test public void hostOnlyAndSubdomainCookiesStayInsideTheAttempt() throws Exception {
        try (Fixture fixture = new Fixture()) {
            fixture.add(GENERATE, GENERATED,
                "Set-Cookie", "host_only=fixture-host; Path=/; Secure",
                "Set-Cookie", "subdomain=fixture-subdomain; Domain=.passport.bilibili.com; Path=/; Secure");
            BilibiliLoginClient.Challenge challenge = fixture.client.generate();
            fixture.add(POLL, scan(0, LANDING + "?SESSDATA=fixture-query"));
            fixture.add(CALLBACK, ""); fixture.add(NAV, LOGGED_IN);
            BilibiliLoginClient.ScanResult result = fixture.client.poll(challenge);
            assertTrue(fixture.requests.get(1).header("Cookie").contains("host_only=fixture-host"));
            assertNull(fixture.requests.get(2).header("Cookie"));
            assertEquals("SESSDATA=fixture-query", fixture.last().header("Cookie"));
            assertEquals(1, result.cookies.size()); assertFalse(result.cookies.get(0).hostOnly());
            fixture.finished();
        }
    }

    @Test public void savedValidationAndOtherAttemptsCannotPolluteTheQrCookieJar() throws Exception {
        try (Fixture first = new Fixture(); Fixture second = new Fixture()) {
            first.add(GENERATE, GENERATED, "Set-Cookie", parent("attempt=fixture-first"));
            BilibiliLoginClient.Challenge challenge = first.client.generate();
            second.challenge(); assertNull(second.last().header("Cookie"));
            first.add(NAV, LOGGED_IN, "Set-Cookie", parent("outside=fixture-outside"));
            assertTrue(first.client.validateSaved("SESSDATA=fixture-saved").loggedIn());
            assertEquals("SESSDATA=fixture-saved", first.last().header("Cookie"));
            first.add(POLL, scan(86101, null)); first.client.poll(challenge);
            assertEquals("attempt=fixture-first", first.last().header("Cookie"));
            first.finished(); second.finished();
        }
    }

    @Test public void candidateNeedsLoggedInNavWithAPositiveMid() throws Exception {
        for (String body : new String[]{"{\"code\":-101}", "{\"code\":0,\"data\":{\"isLogin\":false}}",
                "{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":0}}", "{\"code\":0,\"data\":{\"mid\":7}}"}) {
            try (Fixture fixture = new Fixture()) {
                BilibiliLoginClient.Challenge challenge = fixture.challenge();
                fixture.add(POLL, scan(0, ""), "Set-Cookie", parent("SESSDATA=fixture-response"));
                fixture.add(NAV, body);
                expectFailure(() -> fixture.client.poll(challenge)); fixture.finished();
            }
        }
    }

    @Test public void savedAccountConfirmationComparesVerifiedIdentities() throws Exception {
        try (Fixture fixture = new Fixture()) {
            fixture.add(NAV, LOGGED_IN); BilibiliLoginClient.Account candidate = fixture.client.validateSaved("fixture=saved");
            fixture.add(NAV, LOGGED_IN); assertTrue(candidate.sameAs(fixture.client.validateSaved("fixture=saved")));
            fixture.add(NAV, "{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":9}}");
            assertFalse(candidate.sameAs(fixture.client.validateSaved("fixture=saved")));
            assertFalse(candidate.sameAs(fixture.client.validateSaved(null))); fixture.finished();
        }
    }

    @Test public void unknownPollStatusIsFailureAndNetworkErrorsHaveFixedSafeText() throws Exception {
        try (Fixture fixture = new Fixture()) {
            BilibiliLoginClient.Challenge challenge = fixture.challenge(); fixture.add(POLL, scan(42, null));
            expectFailure(() -> fixture.client.poll(challenge)); fixture.finished();
        }
        HttpResourceLifecycle resources = resources(new AtomicInteger());
        try (BilibiliLoginClient client = new BilibiliLoginClient(request -> new FixtureCall(request,
                ignored -> { throw new IOException("fixture-sensitive-network-text"); }), resources)) {
            try { client.generate(); fail("Network failure was accepted"); }
            catch (BilibiliLoginClient.Failure failure) { assertEquals("登录连接失败，请检查网络后重试", failure.getMessage()); }
        }
    }

    @Test public void closeCancelsTheConcreteCallEvenWhenDispatcherDoesNotKnowIt() throws Exception {
        CountDownLatch executing = new CountDownLatch(1), returnResponse = new CountDownLatch(1);
        AtomicInteger dispatcherSnapshots = new AtomicInteger(), concreteCancellations = new AtomicInteger();
        HttpResourceLifecycle resources = resources(dispatcherSnapshots);
        BilibiliLoginClient client = new BilibiliLoginClient(request -> {
            FixtureCall call = new FixtureCall(request, ignored -> {
                // This transport has not registered the Call in any dispatcher snapshot.
                executing.countDown(); await(returnResponse);
                return response(request, 200, GENERATED);
            });
            call.onCancel = concreteCancellations::incrementAndGet;
            return call;
        }, resources);
        ExecutorService worker = Executors.newSingleThreadExecutor();
        try {
            Future<?> result = worker.submit(() -> { try { client.generate(); } catch (BilibiliLoginClient.Failure failure) { throw new AssertionError(failure); } });
            assertTrue(executing.await(5, TimeUnit.SECONDS)); client.close(); client.close();
            assertEquals(1, concreteCancellations.get()); assertEquals(1, dispatcherSnapshots.get());
            returnResponse.countDown();
            try { result.get(5, TimeUnit.SECONDS); fail("Closed attempt published a late response"); }
            catch (ExecutionException failure) { assertTrue(failure.getCause() instanceof CancellationException); }
            try { client.validateSaved(null); fail("Closed attempt accepted another operation"); }
            catch (CancellationException expected) { assertEquals(HttpResourceLifecycle.State.CLOSED, resources.state()); }
        } finally { returnResponse.countDown(); client.close(); worker.shutdownNow(); }
    }

    private interface Action { void run() throws Exception; }
    private static void expectFailure(Action action) throws Exception {
        try { action.run(); fail("Invalid login candidate was accepted"); }
        catch (BilibiliLoginClient.Failure expected) { assertFalse(expected.getMessage().isEmpty()); }
    }
    private static String scan(int code, String address) throws Exception {
        JSONObject data = new JSONObject().put("code", code);
        if (address != null) data.put("url", address);
        return new JSONObject().put("code", 0).put("data", data).toString();
    }
    private static String parent(String value) { return value + "; Domain=.bilibili.com; Path=/; Secure"; }
    private static String value(BilibiliLoginClient.ScanResult result, String name) {
        for (Cookie cookie : result.cookies) if (cookie.name().equals(name)) return cookie.value();
        return null;
    }
    private static HttpResourceLifecycle resources(AtomicInteger dispatcherSnapshots) {
        return new HttpResourceLifecycle(Runnable::run, Runnable::run, dispatcherSnapshots::incrementAndGet,
            () -> { }, () -> { }, message -> { throw new AssertionError(message); });
    }
    private static void await(CountDownLatch latch) throws IOException {
        try { if (!latch.await(5, TimeUnit.SECONDS)) throw new IOException("Fixture wait timed out"); }
        catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); throw new IOException("Fixture interrupted"); }
    }
    private static Response response(Request request, int status, String body, String... headers) {
        Response.Builder response = new Response.Builder().request(request).protocol(Protocol.HTTP_1_1).code(status)
            .message("Fixture").body(ResponseBody.create(body, MediaType.get("application/json; charset=utf-8")));
        for (int i = 0; i < headers.length; i += 2) response.addHeader(headers[i], headers[i + 1]);
        return response.build();
    }
    private interface Reply { Response execute(Request request) throws IOException; }
    private static final class FixtureCall implements Call {
        private final Request request;
        private final Reply reply;
        private final AtomicBoolean executed = new AtomicBoolean(), cancelled = new AtomicBoolean();
        private Runnable onCancel = () -> { };
        FixtureCall(Request request, Reply reply) { this.request = request; this.reply = reply; }
        @Override public Request request() { return request; }
        @Override public Response execute() throws IOException {
            if (!executed.compareAndSet(false, true)) throw new IllegalStateException("Fixture already executed");
            if (cancelled.get()) throw new IOException("Fixture cancelled");
            return reply.execute(request);
        }
        @Override public void enqueue(Callback callback) { throw new UnsupportedOperationException("Synchronous fixture"); }
        @Override public void cancel() { if (cancelled.compareAndSet(false, true)) onCancel.run(); }
        @Override public boolean isExecuted() { return executed.get(); }
        @Override public boolean isCanceled() { return cancelled.get(); }
        @Override public Timeout timeout() { return new Timeout(); }
        @Override public Call clone() { return new FixtureCall(request, reply); }
    }
    private static final class Fixture implements AutoCloseable {
        final Deque<Reply> replies = new ArrayDeque<>();
        final List<Request> requests = new ArrayList<>();
        final BilibiliLoginClient client = new BilibiliLoginClient(request -> {
            requests.add(request);
            if (replies.isEmpty()) throw new AssertionError("Unexpected fixture request");
            return new FixtureCall(request, replies.removeFirst());
        }, resources(new AtomicInteger()));
        void add(String path, String body, String... headers) { add(path, 200, body, headers); }
        void add(String path, int status, String body, String... headers) {
            replies.add(request -> { assertEquals(path, request.url().encodedPath()); return response(request, status, body, headers); });
        }
        BilibiliLoginClient.Challenge challenge() throws Exception { add(GENERATE, GENERATED); return client.generate(); }
        Request last() { return requests.get(requests.size() - 1); }
        void noBiligameRequests() { for (Request request : requests) assertTrue(request.url().host().endsWith(".bilibili.com")); }
        void finished() { assertTrue("Fixture replies were not consumed", replies.isEmpty()); }
        @Override public void close() { client.close(); }
    }
}
