package com.leonz03.bilicinema;

import android.content.Context;
import android.content.SharedPreferences;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.net.URI;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.UUID;
import java.util.function.LongSupplier;

/** Sole durable owner of short-lived QR PNGs. Disk methods run on a worker/provider thread. */
final class LoginQrImageStore {
    interface Store { String read(); boolean commit(String record); }
    static final class Prepared {
        final URI uri;
        final long expiresAt;
        private Prepared(URI uri, long expiresAt) { this.uri = uri; this.expiresAt = expiresAt; }
    }
    static final long MAX_LIFETIME_MS = 5 * 60 * 1000;
    private static final int MAX_IMAGES = 32, MAX_PNG_BYTES = 2 * 1024 * 1024;
    private static final byte[] PNG = {(byte) 137, 80, 78, 71, 13, 10, 26, 10};
    private static final Object GATE = new Object();
    private final File root;
    private final Store store;
    private final LongSupplier clock;
    private final String authority;

    LoginQrImageStore(Context context) {
        this(new File(context.getFilesDir(), "login-qr"), preferences(context), System::currentTimeMillis, authority(context));
    }
    LoginQrImageStore(File root, Store store, LongSupplier clock, String authority) {
        if (!"login-qr".equals(root.getName()) || authority == null || !authority.matches("[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)+")) {
            throw new IllegalArgumentException("Invalid QR image owner");
        }
        this.root = root; this.store = store; this.clock = clock; this.authority = authority;
    }

    /** Records both derived output paths before mkdir/write; existing unexpired images remain valid. */
    Prepared prepare(byte[] png, long expiresAt) throws IOException {
        if (png == null || png.length < PNG.length || png.length > MAX_PNG_BYTES) throw unavailable();
        byte[] bytes = png.clone();
        for (int i = 0; i < PNG.length; i++) if (bytes[i] != PNG[i]) throw unavailable();
        synchronized (GATE) {
            long now = clock.getAsLong();
            if (expiresAt <= now || Thread.currentThread().isInterrupted()) throw unavailable();
            long boundedExpiry = Math.min(expiresAt, now + MAX_LIFETIME_MS);
            List<Image> images = read();
            recover(images, now);
            if (images.size() >= MAX_IMAGES) throw new IOException("二维码图片暂存已满，请稍后重试");
            Image image = new Image("qr-" + UUID.randomUUID().toString().replace("-", "") + ".png", boundedExpiry, false);
            File partial = file(image.partial()), target = file(image.name);
            if (partial.exists() || target.exists()) throw unavailable();
            images.add(image);
            persist(images);
            try {
                if (!root.isDirectory() && !root.mkdirs()) throw unavailable();
                try (FileOutputStream output = new FileOutputStream(partial)) {
                    if (Thread.currentThread().isInterrupted()) throw unavailable();
                    output.write(bytes); output.getFD().sync();
                }
                if (Thread.currentThread().isInterrupted() || boundedExpiry <= clock.getAsLong() || !partial.renameTo(target)) throw unavailable();
                image.ready = true;
                persist(images);
                return new Prepared(URI.create("content://" + authority + "/" + image.name), boundedExpiry);
            } catch (IOException | SecurityException exception) {
                // The durable WRITING record still owns both names; recovery removes interrupted output.
                throw unavailable();
            }
        }
    }

    File readableFile(URI uri) throws IOException {
        String name = requireName(uri, authority);
        synchronized (GATE) {
            List<Image> images = read();
            long now = clock.getAsLong();
            recover(images, now);
            for (Image image : images) if (image.name.equals(name) && image.ready && image.expiresAt > now) {
                File candidate = file(name);
                if (candidate.isFile()) return candidate;
                break;
            }
            throw unavailable();
        }
    }

    void recover() throws IOException {
        synchronized (GATE) { List<Image> images = read(); recover(images, clock.getAsLong()); }
    }

    static String authority(Context context) { return context.getPackageName() + ".loginqr"; }
    static String requireName(URI uri, String expectedAuthority) throws IOException {
        if (uri == null || !"content".equals(uri.getScheme()) || !expectedAuthority.equals(uri.getRawAuthority())
                || uri.getRawUserInfo() != null || uri.getPort() != -1 || uri.getRawQuery() != null || uri.getRawFragment() != null) throw unavailable();
        String path = uri.getRawPath();
        if (path == null || !path.matches("/qr-[a-f0-9]{32}\\.png")) throw unavailable();
        return path.substring(1);
    }

    private void recover(List<Image> images, long now) throws IOException {
        boolean changed = false;
        for (int i = images.size() - 1; i >= 0; i--) {
            Image image = images.get(i);
            if (!image.ready || image.expiresAt <= now) {
                remove(file(image.partial())); remove(file(image.name));
                images.remove(i); changed = true;
            }
        }
        if (changed) persist(images);
    }
    private List<Image> read() throws IOException {
        try {
            String record = store.read();
            if (record == null || record.isEmpty()) return new ArrayList<>();
            JSONObject root = new JSONObject(record);
            if (root.getInt("version") != 1) throw unavailable();
            JSONArray entries = root.getJSONArray("images");
            if (entries.length() > MAX_IMAGES) throw unavailable();
            Set<String> names = new HashSet<>();
            List<Image> result = new ArrayList<>();
            for (int i = 0; i < entries.length(); i++) {
                JSONObject entry = entries.getJSONObject(i);
                String name = entry.getString("file"), phase = entry.getString("phase");
                long expiry = entry.getLong("expiresAt");
                if (!name.matches("qr-[a-f0-9]{32}\\.png") || !names.add(name) || expiry <= 0
                        || !("WRITING".equals(phase) || "READY".equals(phase))) throw unavailable();
                result.add(new Image(name, expiry, "READY".equals(phase)));
            }
            return result;
        } catch (Exception exception) { throw unavailable(); }
    }
    private void persist(List<Image> images) throws IOException {
        try {
            JSONArray entries = new JSONArray();
            for (Image image : images) entries.put(new JSONObject().put("file", image.name)
                    .put("expiresAt", image.expiresAt).put("phase", image.ready ? "READY" : "WRITING"));
            if (!store.commit(new JSONObject().put("version", 1).put("images", entries).toString())) throw unavailable();
        } catch (Exception exception) { throw unavailable(); }
    }
    private File file(String name) throws IOException {
        if (!name.matches("qr-[a-f0-9]{32}\\.(?:png|part)")) throw unavailable();
        try {
            File candidate = new File(root, name).getCanonicalFile();
            if (!root.getCanonicalFile().equals(candidate.getParentFile())) throw unavailable();
            return candidate;
        } catch (IOException | SecurityException exception) { throw unavailable(); }
    }
    private static void remove(File file) throws IOException { if (file.exists() && !file.delete()) throw unavailable(); }
    private static IOException unavailable() { return new IOException("二维码图片已失效或无法准备，请重新生成后复制"); }
    private static Store preferences(Context context) {
        SharedPreferences preferences = context.getSharedPreferences("login_qr_images", Context.MODE_PRIVATE);
        return new Store() {
            public String read() { return preferences.getString("ownership", ""); }
            public boolean commit(String record) { return preferences.edit().putString("ownership", record).commit(); }
        };
    }
    private static final class Image {
        final String name;
        final long expiresAt;
        boolean ready;
        Image(String name, long expiresAt, boolean ready) { this.name = name; this.expiresAt = expiresAt; this.ready = ready; }
        String partial() { return name.substring(0, name.length() - 4) + ".part"; }
    }
}
