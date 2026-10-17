package com.leonz03.bilicinema;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.Executor;
import java.util.function.Consumer;
import org.junit.Test;
import static org.junit.Assert.*;

public class LoginCookieCommitTest {
    private static final class Queue implements Executor {
        final ArrayDeque<Runnable> tasks = new ArrayDeque<>();
        @Override public void execute(Runnable task) { tasks.add(task); }
        void drain() { while (!tasks.isEmpty()) tasks.removeFirst().run(); }
    }
    private static final class Destination implements LoginCookieCommit.Destination {
        final List<Consumer<Boolean>> callbacks = new ArrayList<>();
        int flushed, closed;
        boolean verified = true, rejectWrite, failFlush;
        @Override public void write(String value, Consumer<Boolean> callback) {
            if (rejectWrite) throw new IllegalStateException("write rejected");
            callbacks.add(callback);
        }
        @Override public boolean flushAndVerify() throws Exception {
            flushed++;
            if (failFlush) throw new Exception("unavailable");
            return verified;
        }
        @Override public void close() { closed++; }
    }

    @Test public void detachedScreenAndLateAcknowledgementRetainWholeBatchBeforeNextLogin() {
        Queue ui = new Queue(), io = new Queue();
        LoginCookieCommit owner = new LoginCookieCommit(ui, io);
        Destination first = new Destination(), second = new Destination();
        List<Boolean> results = new ArrayList<>();
        LoginCookieCommit.Ticket screen = owner.submit(Arrays.asList("one", "two"), first, results::add);
        ui.drain();
        first.callbacks.get(0).accept(true); ui.drain();
        screen.detach(); // Activity teardown (or a waiting notice) cannot release the commit boundary.
        owner.submit(Collections.singletonList("next"), second, results::add);
        boolean[] idle = {false}; owner.whenIdle(() -> idle[0] = true); ui.drain();
        assertEquals(0, first.flushed); assertEquals(0, second.callbacks.size()); assertFalse(idle[0]);
        first.callbacks.get(1).accept(true); ui.drain();
        assertEquals(0, second.callbacks.size()); assertEquals(1, io.tasks.size());
        io.drain(); ui.drain();
        assertEquals(1, first.flushed); assertEquals(1, first.closed); assertTrue(results.isEmpty());
        assertEquals(1, second.callbacks.size()); assertFalse(idle[0]);
        second.callbacks.get(0).accept(true); ui.drain(); io.drain(); ui.drain();
        assertEquals(Collections.singletonList(true), results); assertTrue(idle[0]);
    }

    @Test public void rejectedOrDuplicateAcknowledgementCannotSkipFlushOrClaimSuccess() {
        Queue ui = new Queue(), io = new Queue();
        LoginCookieCommit owner = new LoginCookieCommit(ui, io);
        Destination target = new Destination(); List<Boolean> results = new ArrayList<>();
        owner.submit(Arrays.asList("one", "two"), target, results::add); ui.drain();
        target.callbacks.get(0).accept(false); target.callbacks.get(0).accept(true); ui.drain();
        assertTrue(io.tasks.isEmpty());
        target.callbacks.get(1).accept(true); ui.drain(); io.drain(); ui.drain();
        assertEquals(1, target.flushed); assertEquals(1, target.closed);
        assertEquals(Collections.singletonList(false), results);
    }

    @Test public void writeExceptionAndReadbackFailureAreObservedAndReleaseNextAttempt() {
        Queue ui = new Queue(), io = new Queue();
        LoginCookieCommit owner = new LoginCookieCommit(ui, io);
        Destination rejected = new Destination(), unverified = new Destination(), failed = new Destination();
        rejected.rejectWrite = true; unverified.verified = false; failed.failFlush = true;
        List<Boolean> results = new ArrayList<>();
        owner.submit(Collections.singletonList("one"), rejected, results::add);
        owner.submit(Collections.singletonList("two"), unverified, results::add);
        owner.submit(Collections.singletonList("three"), failed, results::add);
        ui.drain(); io.drain(); ui.drain();
        unverified.callbacks.get(0).accept(true); ui.drain(); io.drain(); ui.drain();
        failed.callbacks.get(0).accept(true); ui.drain(); io.drain(); ui.drain();
        assertEquals(Arrays.asList(false, false, false), results);
        assertEquals(1, rejected.flushed); assertEquals(1, failed.closed);
    }

    @Test public void unavailableWorkerDoesNotLeaveCommitPermanentlyOwned() {
        Queue ui = new Queue();
        LoginCookieCommit owner = new LoginCookieCommit(ui, task -> { throw new IllegalStateException("rejected"); });
        Destination target = new Destination(); List<Boolean> results = new ArrayList<>();
        owner.submit(Collections.singletonList("one"), target, results::add); ui.drain();
        target.callbacks.get(0).accept(true); ui.drain();
        assertEquals(1, target.closed); assertEquals(Collections.singletonList(false), results);
        boolean[] idle = {false}; owner.whenIdle(() -> idle[0] = true); ui.drain(); assertTrue(idle[0]);
    }
}
