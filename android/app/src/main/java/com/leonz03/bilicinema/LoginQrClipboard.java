package com.leonz03.bilicinema;

import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.net.Uri;
import android.os.Looper;
import android.os.PersistableBundle;
import java.io.IOException;

/** Foreground publication only; preparation and eventual expiry remain owned by the image store. */
final class LoginQrClipboard {
    private LoginQrClipboard() { }
    static void publish(Context context, LoginQrImageStore.Prepared image) throws IOException {
        if (Looper.myLooper() != Looper.getMainLooper()) throw new IllegalStateException("Clipboard publication requires the main thread");
        if (image == null || image.expiresAt <= System.currentTimeMillis()) throw new IOException("二维码已过期，请刷新后复制");
        LoginQrImageStore.requireName(image.uri, LoginQrImageStore.authority(context));
        try {
            ClipboardManager clipboard = (ClipboardManager) context.getSystemService(Context.CLIPBOARD_SERVICE);
            if (clipboard == null) throw new IOException("剪贴板暂不可用，请重试");
            ClipData clip = ClipData.newUri(context.getContentResolver(), "B站登录二维码", Uri.parse(image.uri.toString()));
            PersistableBundle sensitive = new PersistableBundle();
            sensitive.putBoolean("android.content.extra.IS_SENSITIVE", true);
            clip.getDescription().setExtras(sensitive);
            clipboard.setPrimaryClip(clip);
        } catch (RuntimeException exception) { throw new IOException("二维码图片复制失败，请重试"); }
    }
}
