package com.leonz03.bilicinema;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Binder;
import android.os.Build;
import android.os.IBinder;
import org.json.JSONObject;

/** The room socket survives the watching activity. Playback remains a visible-screen resource. */
public final class RoomSessionService extends Service implements RoomClient.Listener {
    private static final String CHANNEL = "watching-room";
    private static final String LEAVE = "com.leonz03.bilicinema.LEAVE_ROOM";
    private static final int NOTIFICATION = 42;
    private final LocalBinder binder = new LocalBinder();
    private final RoomSessionReplay replay = new RoomSessionReplay();
    private RoomClient client;
    private RoomClient.Listener observer;
    private boolean promoted;

    final class LocalBinder extends Binder {
        RoomSessionService service() { return RoomSessionService.this; }
    }

    @Override public void onCreate() {
        super.onCreate();
        client = new RoomClient(this);
        NotificationChannel channel = new NotificationChannel(CHANNEL, "观影房间", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("保持当前房间在线，可随时返回或退出");
        channel.setShowBadge(false);
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
    }

    @Override public IBinder onBind(Intent intent) { return binder; }

    RoomClient client() { return client; }

    void attach(RoomClient.Listener value) {
        observer = value;
        replay.deliver(value);
        if (client.progress() != null) value.connectionProgress(client.progress());
    }

    void detach(RoomClient.Listener value) { if (observer == value) observer = null; }

    boolean connect(String room, String credential, RoomConnectionProgress.Operation operation) {
        if (!keepOnline()) return false;
        replay.clear();
        client.connect(room, credential, operation);
        return true;
    }

    boolean retry() { return keepOnline() && client.retry(); }

    private boolean keepOnline() {
        if (promoted) return true;
        try {
            // Called from the visible activity's explicit create/join/retry action.
            startService(new Intent(this, RoomSessionService.class));
            if (Build.VERSION.SDK_INT >= 34)
                startForeground(NOTIFICATION, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
            else startForeground(NOTIFICATION, notification());
            promoted = true;
            return true;
        } catch (RuntimeException error) {
            stopSelf();
            status("系统暂不允许保持房间后台连接，请返回前台重试");
            return false;
        }
    }

    @androidx.annotation.OptIn(markerClass = androidx.media3.common.util.UnstableApi.class)
    private Notification notification() {
        Intent open = new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent content = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        PendingIntent leave = PendingIntent.getService(this, 1,
                new Intent(this, RoomSessionService.class).setAction(LEAVE), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_film)
                .setContentTitle("观影房间保持在线").setContentText("点按返回房间，或退出结束后台连接")
                .setContentIntent(content).setOngoing(true).setOnlyAlertOnce(true)
                .setCategory(Notification.CATEGORY_SERVICE)
                .addAction(new Notification.Action.Builder(null, "退出房间", leave).build()).build();
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && LEAVE.equals(intent.getAction())) {
            forgetSavedRoom();
            stopRoom(true);
            try { if (observer != null) observer.message(new JSONObject().put("type", "left")); }
            catch (org.json.JSONException error) { status("房间已退出"); }
        }
        // A killed process must not silently recreate a room or continue using stale credentials.
        return START_NOT_STICKY;
    }

    void stopRoom(boolean leave) {
        replay.clear();
        client.stop(leave);
        releaseForeground();
    }

    void forgetSavedRoom() {
        getSharedPreferences("MainActivity", MODE_PRIVATE).edit().remove("room").remove("client").apply();
    }

    private void releaseForeground() {
        if (promoted) { stopForeground(STOP_FOREGROUND_REMOVE); promoted = false; }
        stopSelf();
    }

    @Override public void credentials(String room, String credential) {
        // Same private preferences as MainActivity; never place credentials in notifications.
        getSharedPreferences("MainActivity", MODE_PRIVATE).edit().putString("room", room).putString("client", credential).apply();
        if (observer != null) observer.credentials(room, credential);
    }

    @Override public void message(JSONObject value) {
        replay.record(value);
        String type = value.optString("type"), code = value.optString("code");
        if ("closed".equals(type) || "left".equals(type)
                || "error".equals(type) && RoomClient.terminal(code) && !"room_full".equals(code)) forgetSavedRoom();
        if (!replay.active() && ("closed".equals(value.optString("type")) || "left".equals(value.optString("type"))
                || "error".equals(value.optString("type")) && RoomClient.terminal(value.optString("code")))) releaseForeground();
        if (observer != null) observer.message(value);
    }

    @Override public void status(String value) { if (observer != null) observer.status(value); }

    @Override public void connectionProgress(RoomConnectionProgress value) {
        if (value.outcome == RoomConnectionProgress.Outcome.FAILED) releaseForeground();
        if (observer != null) observer.connectionProgress(value);
    }

    @Override public void onDestroy() {
        observer = null;
        client.destroy();
        super.onDestroy();
    }
}
