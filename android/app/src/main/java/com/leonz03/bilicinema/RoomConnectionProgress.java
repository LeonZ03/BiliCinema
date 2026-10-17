package com.leonz03.bilicinema;

/** Immutable evidence of one connection attempt. Only RoomClient advances it from transport events. */
final class RoomConnectionProgress {
    enum Operation { CREATE, JOIN, RESTORE, RECONNECT }
    enum Stage { CONNECTING, SENDING_REQUEST, WAITING_ROOM, SYNCHRONIZING, COMPLETE }
    enum Outcome { RUNNING, FAILED, COMPLETE, CANCELLED }
    final Operation operation;
    final Stage stage;
    final Outcome outcome;
    final boolean joiningRequest;
    final long startedMillis, endedMillis;
    final String failure;
    private RoomConnectionProgress(Operation operation, Stage stage, Outcome outcome, boolean joiningRequest, long started, long ended, String failure) {
        this.operation = operation; this.stage = stage; this.outcome = outcome;
        this.joiningRequest = joiningRequest;
        this.startedMillis = started; this.endedMillis = ended; this.failure = failure;
    }
    static RoomConnectionProgress start(Operation operation, long now) {
        return start(operation, now, operation != Operation.CREATE);
    }
    static RoomConnectionProgress start(Operation operation, long now, boolean joiningRequest) {
        return new RoomConnectionProgress(operation, Stage.CONNECTING, Outcome.RUNNING, joiningRequest, now, 0, "");
    }
    RoomConnectionProgress advance(Stage next, long now) {
        if (outcome != Outcome.RUNNING || next.ordinal() != stage.ordinal() + 1) return this;
        boolean complete = next == Stage.COMPLETE;
        return new RoomConnectionProgress(operation, next, complete ? Outcome.COMPLETE : Outcome.RUNNING, joiningRequest,
                startedMillis, complete ? now : 0, "");
    }
    RoomConnectionProgress fail(String message, long now) {
        if (outcome != Outcome.RUNNING) return this;
        return new RoomConnectionProgress(operation, stage, Outcome.FAILED, joiningRequest, startedMillis, now, message);
    }
    RoomConnectionProgress cancel(long now) {
        return new RoomConnectionProgress(operation, stage, Outcome.CANCELLED, joiningRequest, startedMillis, now, "");
    }
    boolean needsResolution() { return outcome == Outcome.RUNNING || outcome == Outcome.FAILED; }
    long elapsedMillis(long now) { return Math.max(0, (outcome == Outcome.RUNNING ? now : endedMillis) - startedMillis); }
}
