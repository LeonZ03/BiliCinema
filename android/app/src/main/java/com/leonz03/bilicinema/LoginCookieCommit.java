package com.leonz03.bilicinema;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Executor;
import java.util.function.Consumer;

/** Process-owned commit boundary: a detached screen never abandons queued cookie writes. */
final class LoginCookieCommit {
    interface Destination extends AutoCloseable {
        void write(String cookie, Consumer<Boolean> acknowledged);
        boolean flushAndVerify() throws Exception;
        @Override void close();
    }
    static final class Ticket {
        private volatile Consumer<Boolean> observer;
        private Ticket(Consumer<Boolean> observer) { this.observer = observer; }
        void detach() { observer = null; }
        private void completed(boolean success) {
            Consumer<Boolean> current = observer; observer = null;
            if (current != null) current.accept(success);
        }
    }
    private static final class Batch {
        final List<String> cookies;
        final Destination destination;
        final Ticket ticket;
        final boolean[] acknowledged;
        int remaining;
        boolean accepted = true;
        Batch(List<String> cookies, Destination destination, Ticket ticket) {
            this.cookies = new ArrayList<>(cookies); this.destination = destination; this.ticket = ticket;
            remaining = cookies.size(); acknowledged = new boolean[remaining];
        }
    }
    private final Executor serial, io;
    private final ArrayDeque<Batch> waiting = new ArrayDeque<>();
    private final List<Ticket> idle = new ArrayList<>();
    private Batch active;

    LoginCookieCommit(Executor serial, Executor io) { this.serial = serial; this.io = io; }

    Ticket submit(List<String> cookies, Destination destination, Consumer<Boolean> completed) {
        Ticket ticket = new Ticket(completed);
        Batch batch = new Batch(cookies, destination, ticket);
        serial.execute(() -> { waiting.addLast(batch); startNext(); });
        return ticket;
    }

    Ticket whenIdle(Runnable ready) {
        Ticket ticket = new Ticket(ignored -> ready.run());
        serial.execute(() -> {
            if (active == null && waiting.isEmpty()) ticket.completed(true); else idle.add(ticket);
        });
        return ticket;
    }

    private void startNext() {
        if (active != null) return;
        active = waiting.pollFirst();
        if (active == null) {
            List<Ticket> observers = new ArrayList<>(idle); idle.clear();
            for (Ticket ticket : observers) ticket.completed(true);
            return;
        }
        Batch batch = active;
        if (batch.remaining == 0) { batch.accepted = false; flush(batch); return; }
        for (int index = 0; index < batch.cookies.size(); index++) {
            final int slot = index;
            try {
                batch.destination.write(batch.cookies.get(index), success -> serial.execute(() -> acknowledge(batch, slot, Boolean.TRUE.equals(success))));
            } catch (RuntimeException rejected) { serial.execute(() -> acknowledge(batch, slot, false)); }
        }
    }

    private void acknowledge(Batch batch, int index, boolean success) {
        if (active != batch || batch.acknowledged[index]) return;
        batch.acknowledged[index] = true; batch.accepted &= success;
        if (--batch.remaining == 0) flush(batch);
    }

    private void flush(Batch batch) {
        try {
            io.execute(() -> {
                boolean verified;
                try { verified = batch.destination.flushAndVerify() && batch.accepted; }
                catch (Exception failure) { verified = false; }
                finish(batch, verified);
            });
        } catch (RuntimeException rejected) { finish(batch, false); }
    }

    private void finish(Batch batch, boolean verified) {
        boolean released = true;
        try { batch.destination.close(); }
        catch (RuntimeException failure) { released = false; }
        final boolean result = verified && released;
        serial.execute(() -> {
            active = null;
            try { batch.ticket.completed(result); }
            finally { startNext(); }
        });
    }
}
