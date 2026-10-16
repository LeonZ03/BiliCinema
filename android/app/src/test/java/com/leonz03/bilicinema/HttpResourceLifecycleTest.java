package com.leonz03.bilicinema;

import org.junit.Test;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import static org.junit.Assert.*;

public class HttpResourceLifecycleTest {
    private void await(CountDownLatch latch) {
        try { if (!latch.await(5, TimeUnit.SECONDS)) throw new IllegalStateException("Fixture wait timed out"); }
        catch (InterruptedException exception) { Thread.currentThread().interrupt(); throw new IllegalStateException("Fixture interrupted"); }
    }
    private void drain(ExecutorService worker) throws Exception { worker.submit(() -> { }).get(5, TimeUnit.SECONDS); }

    @Test public void lifecycleCloseReturnsWhileTlsEvictionRunsOffTheCallingThread() throws Exception {
        Thread lifecycleThread = Thread.currentThread();
        ExecutorService cancellation = Executors.newSingleThreadExecutor(), cleanup = Executors.newSingleThreadExecutor();
        CountDownLatch evicting = new CountDownLatch(1), finish = new CountDownLatch(1);
        AtomicInteger cancelled = new AtomicInteger(), shutdown = new AtomicInteger();
        List<String> failures = Collections.synchronizedList(new ArrayList<>());
        HttpResourceLifecycle resources = new HttpResourceLifecycle(cancellation, cleanup,
                () -> { assertNotSame(lifecycleThread, Thread.currentThread()); cancelled.incrementAndGet(); },
                () -> { assertNotSame(lifecycleThread, Thread.currentThread()); evicting.countDown(); await(finish); },
                () -> { assertNotSame(lifecycleThread, Thread.currentThread()); shutdown.incrementAndGet(); }, failures::add);
        try {
            Future<?> closing = resources.close();
            assertSame(closing, resources.close());
            assertTrue(evicting.await(5, TimeUnit.SECONDS));
            assertFalse(closing.isDone());
            assertEquals(HttpResourceLifecycle.State.CLOSING, resources.state());
            assertTrue(resources.pendingCount() >= 1);
            finish.countDown(); closing.get(5, TimeUnit.SECONDS); drain(cleanup);
            assertEquals(1, cancelled.get()); assertEquals(1, shutdown.get());
            assertEquals(HttpResourceLifecycle.State.CLOSED, resources.state());
            assertEquals(0, resources.pendingCount()); assertEquals(2, resources.completedCount());
            assertTrue(failures.isEmpty());
        } finally { finish.countDown(); cancellation.shutdownNow(); cleanup.shutdownNow(); }
    }

    @Test public void slowIdleTlsEvictionCannotDelayANewSelectionsCancellation() throws Exception {
        ExecutorService cancellation = Executors.newSingleThreadExecutor(), cleanup = Executors.newSingleThreadExecutor();
        CountDownLatch evicting = new CountDownLatch(1), finish = new CountDownLatch(1);
        AtomicInteger capturedOldCall = new AtomicInteger();
        HttpResourceLifecycle older = new HttpResourceLifecycle(cancellation, cleanup, () -> { },
                () -> { evicting.countDown(); await(finish); }, () -> { }, message -> fail(message));
        HttpResourceLifecycle newer = new HttpResourceLifecycle(cancellation, cleanup, () -> { }, () -> { }, () -> { }, message -> fail(message));
        try {
            Future<?> closing = older.close();
            assertTrue(evicting.await(5, TimeUnit.SECONDS));
            newer.cancel(capturedOldCall::incrementAndGet).get(5, TimeUnit.SECONDS);
            assertEquals(1, capturedOldCall.get());
            assertFalse(closing.isDone());
            finish.countDown(); closing.get(5, TimeUnit.SECONDS);
            newer.close().get(5, TimeUnit.SECONDS); drain(cleanup);
            assertEquals(0, older.pendingCount()); assertEquals(0, newer.pendingCount());
        } finally { finish.countDown(); cancellation.shutdownNow(); cleanup.shutdownNow(); }
    }

    @Test public void tlsEvictionFailureStillShutsDownAndIsObservedWithoutSensitiveText() throws Exception {
        ExecutorService cancellation = Executors.newSingleThreadExecutor(), cleanup = Executors.newSingleThreadExecutor();
        AtomicInteger shutdown = new AtomicInteger();
        List<String> failures = Collections.synchronizedList(new ArrayList<>());
        HttpResourceLifecycle resources = new HttpResourceLifecycle(cancellation, cleanup, () -> { },
                () -> { throw new IllegalStateException("https://private.example.test/?signed=fixture-secret"); },
                shutdown::incrementAndGet, failures::add);
        try {
            try { resources.close().get(5, TimeUnit.SECONDS); fail("Cleanup failure was lost"); }
            catch (ExecutionException expected) { assertTrue(expected.getCause() instanceof IllegalStateException); }
            drain(cleanup);
            assertEquals(1, shutdown.get());
            assertEquals(HttpResourceLifecycle.State.FAILED, resources.state());
            assertEquals(1, resources.failureCount()); assertEquals(0, resources.pendingCount());
            assertEquals(Collections.singletonList("Background network resource cleanup failed"), failures);
        } finally { cancellation.shutdownNow(); cleanup.shutdownNow(); }
    }

    @Test public void cancellationFailureCannotAbandonPoolEvictionOrDispatcherShutdown() throws Exception {
        ExecutorService cancellation = Executors.newSingleThreadExecutor(), cleanup = Executors.newSingleThreadExecutor();
        AtomicInteger evicted = new AtomicInteger(), shutdown = new AtomicInteger();
        List<String> failures = Collections.synchronizedList(new ArrayList<>());
        HttpResourceLifecycle resources = new HttpResourceLifecycle(cancellation, cleanup,
                () -> { throw new IllegalStateException("fixture-secret"); }, evicted::incrementAndGet,
                shutdown::incrementAndGet, failures::add);
        try {
            try { resources.close().get(5, TimeUnit.SECONDS); fail("Cancellation failure was lost"); }
            catch (ExecutionException expected) { assertEquals("HTTP cancellation failed", expected.getCause().getMessage()); }
            drain(cleanup);
            assertEquals(1, evicted.get()); assertEquals(1, shutdown.get());
            assertEquals(HttpResourceLifecycle.State.FAILED, resources.state());
            assertEquals(2, resources.failureCount()); assertEquals(0, resources.pendingCount());
            for (String failure : failures) assertEquals("Background network resource cleanup failed", failure);
        } finally { cancellation.shutdownNow(); cleanup.shutdownNow(); }
    }
}
