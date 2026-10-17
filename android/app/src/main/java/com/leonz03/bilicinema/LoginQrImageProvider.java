package com.leonz03.bilicinema;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.net.URI;

/** Only recorded, complete, unexpired QR PNGs can be read through a granted URI. */
public final class LoginQrImageProvider extends ContentProvider {
    @Override public boolean onCreate() { return true; }
    private Context context() {
        Context context = getContext();
        if (context == null) throw new IllegalStateException("QR image provider unavailable");
        return context;
    }
    private URI checked(Uri uri) throws IOException {
        if (uri == null) throw new IOException("QR image URI unavailable");
        try {
            URI parsed = URI.create(uri.toString());
            LoginQrImageStore.requireName(parsed, LoginQrImageStore.authority(context()));
            return parsed;
        } catch (IllegalArgumentException exception) { throw new IOException("QR image URI unavailable"); }
    }
    @Override public String getType(Uri uri) {
        try { checked(uri); return "image/png"; }
        catch (IOException exception) { throw new IllegalArgumentException("QR image URI unavailable"); }
    }
    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode)) throw new FileNotFoundException("QR images are read-only");
        try {
            File image = new LoginQrImageStore(context()).readableFile(checked(uri));
            return ParcelFileDescriptor.open(image, ParcelFileDescriptor.MODE_READ_ONLY);
        } catch (IOException | SecurityException exception) { throw new FileNotFoundException("QR image expired or unavailable"); }
    }
    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] args, String sortOrder) {
        try {
            File image = new LoginQrImageStore(context()).readableFile(checked(uri));
            String[] columns = projection == null ? new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE} : projection;
            MatrixCursor result = new MatrixCursor(columns);
            Object[] row = new Object[columns.length];
            for (int i = 0; i < columns.length; i++) row[i] = OpenableColumns.DISPLAY_NAME.equals(columns[i]) ? image.getName()
                    : OpenableColumns.SIZE.equals(columns[i]) ? image.length() : null;
            result.addRow(row);
            return result;
        } catch (IOException | SecurityException exception) { throw new IllegalArgumentException("QR image expired or unavailable"); }
    }
    @Override public int delete(Uri uri, String selection, String[] args) { throw readOnly(); }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] args) { throw readOnly(); }
    @Override public Uri insert(Uri uri, ContentValues values) { throw readOnly(); }
    private static UnsupportedOperationException readOnly() { return new UnsupportedOperationException("QR image provider is read-only"); }
}
