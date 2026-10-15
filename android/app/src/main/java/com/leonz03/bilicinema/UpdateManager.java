package com.leonz03.bilicinema;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

/** Release verification precedes the system installer; user data and signing identity stay intact. */
final class UpdateManager {
    private static final String RELEASE_API = "https://api.github.com/repos/LeonZ03/BiliCinema/releases/latest";
    private static final long MAX_APK = 128L * 1024 * 1024;
    private final Activity activity;
    private final Consumer<String> status;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService work = Executors.newSingleThreadExecutor();
    private final OkHttpClient http = new OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS).readTimeout(45, TimeUnit.SECONDS).callTimeout(5, TimeUnit.MINUTES).build();
    private final UpdateJournal journal;
    private volatile boolean closed;
    private boolean busy;
    private File pendingInstall;
    private boolean permissionRequested;
    private boolean installing;
    UpdateManager(Activity activity, Consumer<String> status) {
        this.activity = activity; this.status = status;
        android.content.SharedPreferences owner = activity.getSharedPreferences("update_operation", Activity.MODE_PRIVATE);
        journal = new UpdateJournal(new File(activity.getFilesDir(), "updates"), new UpdateJournal.Store() {
            public String read() { return owner.getString("operation", ""); }
            public boolean commit(String record) { return owner.edit().putString("operation", record).commit(); }
        });
    }
    void recoverAndCheck() {
        if (busy || closed) return; busy = true;
        work.execute(() -> {
            File recovered = null;
            try { recovered = journal.recover(BuildConfig.VERSION_NAME, file -> { validateIdentity(file); return true; }); }
            catch (Exception error) { showStatus("上次更新未完成，已停止安装；可重新检查更新"); }
            File candidate = recovered;
            main.post(() -> {
                busy = false;
                if (closed || activity.isFinishing()) return;
                if (candidate == null) { check(false); return; }
                new AlertDialog.Builder(activity).setTitle("发现已校验的更新包").setMessage("上次安装尚未完成，可以继续安装或删除此更新包。").setPositiveButton("继续安装", (d, w) -> { pendingInstall = candidate; installPending(); }).setNegativeButton("删除更新包", (d, w) -> cancelPending()).show();
            });
        });
    }
    void check(boolean explicit) {
        if (busy || closed) return;
        busy = true;
        if (explicit) status.accept("正在检查新版本…");
        work.execute(() -> {
            try {
                JSONObject release = new JSONObject(readText(RELEASE_API, 2 * 1024 * 1024));
                String tag = release.getString("tag_name").replaceFirst("^bilicinema-v", "");
                if (!newer(tag, BuildConfig.VERSION_NAME)) {
                    if (explicit) showStatus("当前已是最新版本 " + BuildConfig.VERSION_NAME);
                    return;
                }
                String expectedName = "BiliCinema-" + tag + "-android-universal.apk";
                String apk = "", sums = "";
                JSONArray assets = release.getJSONArray("assets");
                for (int i = 0; i < assets.length(); i++) {
                    JSONObject asset = assets.getJSONObject(i);
                    if (expectedName.equals(asset.optString("name"))) apk = asset.getString("browser_download_url");
                    if ("SHA256SUMS.txt".equals(asset.optString("name"))) sums = asset.getString("browser_download_url");
                }
                if (apk.isEmpty() || sums.isEmpty()) { if (explicit) showStatus("发现新版本，但安卓更新包尚未发布，请稍后重试"); return; }
                String apkUrl = approvedAsset(apk), sumUrl = approvedAsset(sums);
                main.post(() -> {
                    if (!closed && !activity.isFinishing()) new AlertDialog.Builder(activity).setTitle("发现新版本 " + tag).setMessage("下载更新后将打开系统安装界面。安装会保留本机登录和房间记录。").setNegativeButton("稍后", null).setPositiveButton("下载更新", (d, w) -> download(apkUrl, sumUrl, expectedName, tag)).show();
                });
            } catch (Exception error) { if (explicit) showStatus("更新检查失败，请检查网络后重试"); }
            finally { main.post(() -> busy = false); }
        });
    }
    static boolean newer(String available, String installed) {
        if (!available.matches("\\d+\\.\\d+\\.\\d+")) return false;
        String[] left = available.split("\\."), right = installed.split("\\.");
        try { for (int i = 0; i < 3; i++) { int compare = Integer.compare(Integer.parseInt(left[i]), Integer.parseInt(right[i])); if (compare != 0) return compare > 0; } }
        catch (RuntimeException error) { return false; }
        return false;
    }
    static String checksum(String sums, String filename) throws IOException {
        for (String line : sums.split("\\r?\\n")) {
            String[] parts = line.trim().split("\\s+", 2);
            if (parts.length == 2 && parts[1].replaceFirst("^\\*", "").equals(filename) && parts[0].matches("[0-9A-Fa-f]{64}")) return parts[0].toLowerCase(java.util.Locale.ROOT);
        }
        throw new IOException("checksum missing");
    }
    private static String approvedAsset(String value) throws IOException {
        Uri uri = Uri.parse(value);
        if (!"https".equals(uri.getScheme()) || !"github.com".equals(uri.getHost()) || uri.getPath() == null || !uri.getPath().startsWith("/LeonZ03/BiliCinema/releases/download/")) throw new IOException("invalid release asset");
        return value;
    }
    private void download(String apkUrl, String sumUrl, String name, String targetVersion) {
        if (busy || closed) return; busy = true; status.accept("正在下载并校验更新包…");
        work.execute(() -> {
            boolean owned = false, committed = false;
            try {
                String expected = checksum(readText(sumUrl, 256 * 1024), name);
                journal.begin(name, expected, targetVersion); owned = true;
                File partial = journal.partial();
                MessageDigest sha = MessageDigest.getInstance("SHA-256");
                try (Response response = http.newCall(request(apkUrl)).execute()) {
                    if (!response.isSuccessful() || response.body() == null) throw new IOException("download failed");
                    if (response.body().contentLength() > MAX_APK) throw new IOException("package too large");
                    try (InputStream source = response.body().byteStream(); FileOutputStream target = new FileOutputStream(partial)) {
                        byte[] buffer = new byte[32768]; int count; long size = 0;
                        while ((count = source.read(buffer)) != -1) { if (closed || Thread.currentThread().isInterrupted()) throw new IOException("cancelled"); size += count; if (size > MAX_APK) throw new IOException("package too large"); sha.update(buffer, 0, count); target.write(buffer, 0, count); }
                        target.getFD().sync();
                    }
                }
                StringBuilder actual = new StringBuilder(); for (byte value : sha.digest()) actual.append(String.format(java.util.Locale.ROOT, "%02x", value & 255));
                if (!MessageDigest.isEqual(expected.getBytes(StandardCharsets.US_ASCII), actual.toString().getBytes(StandardCharsets.US_ASCII))) throw new IOException("checksum mismatch");
                validateIdentity(partial);
                File apk = journal.commitVerified(); committed = true;
                main.post(() -> { pendingInstall = apk; permissionRequested = false; installPending(); });
            } catch (Exception error) { showStatus("更新包下载或校验失败，未执行安装；请检查网络后重试"); }
            finally {
                if (owned && !committed) try { journal.cancel(); } catch (Exception cleanup) { showStatus("更新临时文件仍有记录，重启后将继续清理"); }
                main.post(() -> busy = false);
            }
        });
    }
    @SuppressWarnings("deprecation")
    private void validateIdentity(File apk) throws Exception {
        PackageManager pm = activity.getPackageManager();
        int flags = Build.VERSION.SDK_INT >= 28 ? PackageManager.GET_SIGNING_CERTIFICATES : PackageManager.GET_SIGNATURES;
        PackageInfo incoming = pm.getPackageArchiveInfo(apk.getAbsolutePath(), flags), current = pm.getPackageInfo(activity.getPackageName(), flags);
        if (incoming == null || !activity.getPackageName().equals(incoming.packageName) || !newer(incoming.versionName, current.versionName)) throw new IOException("package identity mismatch");
        Signature[] incomingSigners = Build.VERSION.SDK_INT >= 28 ? incoming.signingInfo.getApkContentsSigners() : incoming.signatures;
        Signature[] installedSigners = Build.VERSION.SDK_INT >= 28 ? current.signingInfo.getApkContentsSigners() : current.signatures;
        if (incomingSigners == null || installedSigners == null || incomingSigners.length != installedSigners.length) throw new IOException("signing identity mismatch");
        for (int i = 0; i < incomingSigners.length; i++) if (!Arrays.equals(incomingSigners[i].toByteArray(), installedSigners[i].toByteArray())) throw new IOException("signing identity mismatch");
    }
    void installPending() {
        if (closed || installing || pendingInstall == null || activity.isFinishing()) return;
        try {
            if (!activity.getPackageManager().canRequestPackageInstalls()) {
                if (permissionRequested) { status.accept("尚未允许安装更新，可在系统设置中允许后返回继续"); return; }
                permissionRequested = true;
                status.accept("请允许 BiliCinema 安装更新，返回后继续安装");
                activity.startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + activity.getPackageName()))); return;
            }
            installing = true; File apk = pendingInstall;
            work.execute(() -> {
                try {
                    validateIdentity(apk); journal.installerLaunched();
                    main.post(() -> {
                        installing = false; if (closed || activity.isFinishing()) return;
                        try {
                            Uri uri = new Uri.Builder().scheme("content").authority(activity.getPackageName() + ".fileprovider").appendPath(apk.getName()).build();
                            Intent install = new Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
                            activity.startActivity(install); pendingInstall = null; status.accept("更新包已校验，请在系统界面完成安装");
                        } catch (RuntimeException error) { status.accept("无法打开系统安装界面，更新包已保留，下次启动可继续安装"); }
                    });
                } catch (Exception error) { main.post(() -> { installing = false; status.accept("更新安装准备失败，未打开安装界面；更新记录已保留"); }); }
            });
        } catch (RuntimeException error) { status.accept("无法打开安装界面，请在系统设置中允许安装应用后重试"); }
    }
    private void cancelPending() { pendingInstall = null; work.execute(() -> { try { journal.cancel(); showStatus("已删除下载的更新包"); } catch (Exception error) { showStatus("更新包清理未完成，记录已保留以便重试"); } }); }
    private Request request(String url) { return new Request.Builder().url(url).header("Accept", "application/vnd.github+json").header("User-Agent", "BiliCinema-Android/" + BuildConfig.VERSION_NAME).build(); }
    private String readText(String url, int limit) throws IOException {
        try (Response response = http.newCall(request(url)).execute()) {
            if (!response.isSuccessful() || response.body() == null) throw new IOException("request failed");
            try (InputStream input = response.body().byteStream(); java.io.ByteArrayOutputStream result = new java.io.ByteArrayOutputStream()) {
                byte[] buffer = new byte[8192]; int count; while ((count = input.read(buffer)) != -1) { if (result.size() + count > limit) throw new IOException("response too large"); result.write(buffer, 0, count); }
                return result.toString(StandardCharsets.UTF_8.name());
            }
        }
    }
    private void showStatus(String text) { main.post(() -> { if (!closed) status.accept(text); }); }
    void close() { closed = true; http.dispatcher().cancelAll(); work.shutdownNow(); }
}
