package com.leonz03.bilicinema;

import android.util.Log;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.Future;
import java.util.concurrent.FutureTask;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;
import okhttp3.OkHttpClient;

/** Owns and observes physical HTTP cancellation/closure. TLS close may itself perform network I/O. */
final class HttpResourceLifecycle {
    enum State { OPEN, CLOSING, CLOSED, FAILED }
    private static final Executor CANCELLATION = backgroundExecutor("BiliCinema-HttpCancel", 2);
    private static final Executor CLEANUP = backgroundExecutor("BiliCinema-HttpCleanup", 1);
    private final Executor cancellationExecutor, cleanupExecutor;
    private final Runnable cancelAll, evictAll, shutdown;
    private final Consumer<String> reportFailure;
    private final Set<CleanupTask> pending = ConcurrentHashMap.newKeySet();
    private final AtomicInteger failures = new AtomicInteger();
    private final AtomicInteger completed = new AtomicInteger();
    private volatile State state = State.OPEN;
    private Future<?> closeTask;

    HttpResourceLifecycle(OkHttpClient client) {
        this(CANCELLATION, CLEANUP, client.dispatcher()::cancelAll, client.connectionPool()::evictAll,
                () -> client.dispatcher().executorService().shutdown(),
                message -> Log.e("BiliCinemaHttp", message));
    }

    HttpResourceLifecycle(Executor cancellationExecutor, Executor cleanupExecutor, Runnable cancelAll, Runnable evictAll,
                          Runnable shutdown, Consumer<String> reportFailure) {
        this.cancellationExecutor = cancellationExecutor; this.cleanupExecutor = cleanupExecutor;
        this.cancelAll = cancelAll; this.evictAll = evictAll;
        this.shutdown = shutdown; this.reportFailure = reportFailure;
    }

    /** The caller must invalidate its operation/generation immediately before physical cancellation. */
    Future<?> cancel(Runnable cancellation) { return submit(cancellation, false, null); }

    synchronized Future<?> close() {
        if (closeTask != null) return closeTask;
        state = State.CLOSING;
        AtomicBoolean cancellationFailed = new AtomicBoolean();
        CleanupTask release = new CleanupTask(() -> {
            try { evictAll.run(); }
            finally { shutdown.run(); }
            if (cancellationFailed.get()) throw new IllegalStateException("HTTP cancellation failed");
        }, true, null);
        pending.add(release);
        closeTask = release;
        // Pool eviction can block in TLS close. A separate bounded cancellation executor prevents
        // an older client's idle connection cleanup from delaying a new selection's Call.cancel.
        submit(cancelAll, false, success -> {
            cancellationFailed.set(!success);
            execute(cleanupExecutor, release);
        });
        return closeTask;
    }

    State state() { return state; }
    int pendingCount() { return pending.size(); }
    int failureCount() { return failures.get(); }
    int completedCount() { return completed.get(); }

    private Future<?> submit(Runnable action, boolean closing, Consumer<Boolean> completion) {
        CleanupTask task = new CleanupTask(action, closing, completion);
        pending.add(task);
        execute(cancellationExecutor, task);
        return task;
    }

    private void execute(Executor executor, CleanupTask task) {
        try { executor.execute(task); }
        catch (RuntimeException exception) { task.rejected(exception); }
    }

    private final class CleanupTask extends FutureTask<Void> {
        private final boolean closing;
        private final Consumer<Boolean> completion;
        CleanupTask(Runnable action, boolean closing, Consumer<Boolean> completion) {
            super(action, null); this.closing = closing; this.completion = completion;
        }
        void rejected(RuntimeException failure) { setException(failure); }
        @Override protected void done() {
            boolean success = false;
            try { get(); success = true; }
            catch (InterruptedException exception) { Thread.currentThread().interrupt(); }
            catch (ExecutionException | CancellationException exception) { success = false; }
            finally {
                pending.remove(this);
                completed.incrementAndGet();
                if (closing) state = success ? State.CLOSED : State.FAILED;
                try {
                    if (!success) {
                        failures.incrementAndGet();
                        // Never log exception text/cause: network failures may embed cookies or signed URLs.
                        reportFailure.accept("Background network resource cleanup failed");
                    }
                } finally {
                    if (completion != null) completion.accept(success);
                }
            }
        }
    }

    private static Executor backgroundExecutor(String name, int threads) {
        ThreadPoolExecutor executor = new ThreadPoolExecutor(threads, threads, 30, TimeUnit.SECONDS,
                new LinkedBlockingQueue<>(), runnable -> {
                    Thread thread = new Thread(runnable, name);
                    thread.setDaemon(true);
                    return thread;
                });
        executor.allowCoreThreadTimeOut(true);
        return executor;
    }
}
