package com.leonz03.bilicinema;

import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.File;
import java.io.FileNotFoundException;

/** Minimal same-app FileProvider so updates can use the platform installer without external libraries. */
public final class UpdateFileProvider extends android.content.ContentProvider {
    @Override public boolean onCreate() { return true; }
    private File fileFor(Uri uri) throws FileNotFoundException {
        String name = uri.getLastPathSegment();
        if (name == null || name.contains("..") || name.contains("/") || name.contains("\\")) throw new FileNotFoundException();
        File root = new File(providerContext().getFilesDir(), "updates");
        File file = new File(root, name);
        try { if (!file.getCanonicalFile().toPath().startsWith(root.getCanonicalFile().toPath())) throw new FileNotFoundException(); }
        catch (java.io.IOException e) { throw new FileNotFoundException(); }
        if (!file.isFile()) throw new FileNotFoundException();
        return file;
    }
    private android.content.Context providerContext() { android.content.Context c = getContext(); if (c == null) throw new IllegalStateException(); return c; }
    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException { return ParcelFileDescriptor.open(fileFor(uri), ParcelFileDescriptor.MODE_READ_ONLY); }
    @Override public String getType(Uri uri) { return "application/vnd.android.package-archive"; }
    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] args, String sortOrder) {
        try { File f = fileFor(uri); String[] cols = projection == null ? new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE} : projection; MatrixCursor c = new MatrixCursor(cols); Object[] row = new Object[cols.length]; for (int i=0;i<cols.length;i++) row[i] = OpenableColumns.DISPLAY_NAME.equals(cols[i]) ? f.getName() : OpenableColumns.SIZE.equals(cols[i]) ? f.length() : null; c.addRow(row); return c; } catch (FileNotFoundException e) { return null; }
    }
    @Override public int delete(Uri uri, String selection, String[] args) { try { return fileFor(uri).delete() ? 1 : 0; } catch (FileNotFoundException e) { return 0; } }
    @Override public int update(Uri uri, android.content.ContentValues values, String selection, String[] args) { return 0; }
    @Override public Uri insert(Uri uri, android.content.ContentValues values) { return null; }
}
