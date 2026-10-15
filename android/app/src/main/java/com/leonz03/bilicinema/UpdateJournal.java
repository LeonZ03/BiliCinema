package com.leonz03.bilicinema;

import org.json.JSONObject;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.security.MessageDigest;

/** Durable owner for every update output. All methods run on the update worker. */
final class UpdateJournal {
    interface Store { String read(); boolean commit(String record); }
    interface IdentityCheck { boolean valid(File apk) throws Exception; }
    enum Phase { DOWNLOADING, COMMITTING, VERIFIED, INSTALLER_LAUNCHED, CANCELLED, COMPLETED }
    private final File root;
    private final Store store;
    UpdateJournal(File root, Store store) { this.root = root; this.store = store; }

    synchronized void begin(String name, String hash, String version) throws Exception {
        if (!name.matches("BiliCinema-\\d+\\.\\d+\\.\\d+-android-universal\\.apk") || !hash.matches("[a-f0-9]{64}") || !version.matches("\\d+\\.\\d+\\.\\d+")) throw new IOException("Invalid update identity");
        JSONObject old = record();
        if (old != null) cleanup(old, Phase.CANCELLED);
        JSONObject next = new JSONObject().put("name", name).put("partial", "update.part").put("sha256", hash).put("version", version).put("phase", Phase.DOWNLOADING.name());
        persist(next); // Both possible outputs have a durable owner before mkdir or the first byte.
        if (!root.isDirectory() && !root.mkdirs()) throw new IOException("Update directory unavailable");
    }
    synchronized File partial() throws Exception { return file(required().getString("partial")); }
    synchronized File commitVerified() throws Exception {
        JSONObject operation = required();
        File partial = file(operation.getString("partial")), target = file(operation.getString("name"));
        if (!matchesDigest(partial, operation.getString("sha256"))) throw new IOException("Update checksum mismatch");
        operation.put("phase", Phase.COMMITTING.name()); persist(operation);
        if (target.exists() && !target.delete()) throw new IOException("Prior update cleanup failed");
        if (!partial.renameTo(target)) throw new IOException("Update commit failed");
        operation.put("phase", Phase.VERIFIED.name()); persist(operation);
        return target;
    }
    synchronized File recover(String installedVersion, IdentityCheck identity) throws Exception {
        JSONObject operation = record(); if (operation == null) return null;
        Phase phase = Phase.valueOf(operation.getString("phase"));
        if (phase == Phase.CANCELLED || phase == Phase.COMPLETED) { cleanup(operation, phase); return null; }
        if (!UpdateManager.newer(operation.getString("version"), installedVersion)) { cleanup(operation, Phase.COMPLETED); return null; }
        if (phase == Phase.DOWNLOADING) { cleanup(operation, Phase.CANCELLED); return null; }
        File target = file(operation.getString("name"));
        File candidate = target.isFile() ? target : file(operation.getString("partial"));
        boolean valid;
        try { valid = matchesDigest(candidate, operation.getString("sha256")) && identity.valid(candidate); }
        catch (Exception invalid) { cleanup(operation, Phase.CANCELLED); throw invalid; }
        if (!valid) { cleanup(operation, Phase.CANCELLED); return null; }
        if (!candidate.equals(target)) {
            if (!candidate.renameTo(target)) throw new IOException("Interrupted update recovery failed");
        }
        operation.put("phase", Phase.VERIFIED.name()); persist(operation);
        return target;
    }
    synchronized void installerLaunched() throws Exception { JSONObject operation = required(); operation.put("phase", Phase.INSTALLER_LAUNCHED.name()); persist(operation); }
    synchronized void retainVerified() throws Exception { JSONObject operation = required(); operation.put("phase", Phase.VERIFIED.name()); persist(operation); }
    synchronized void cancel() throws Exception { JSONObject operation = record(); if (operation != null) cleanup(operation, Phase.CANCELLED); }
    private void cleanup(JSONObject operation, Phase terminal) throws Exception {
        // Only the two recorded children of the private update directory are eligible for deletion.
        for (String field : new String[]{"partial", "name"}) {
            File output = file(operation.getString(field));
            if (output.exists() && !output.delete()) throw new IOException("Owned update output cleanup failed");
        }
        operation.put("phase", terminal.name()); persist(operation);
    }
    private File file(String name) throws IOException {
        if (!name.equals("update.part") && !name.matches("BiliCinema-\\d+\\.\\d+\\.\\d+-android-universal\\.apk")) throw new IOException("Invalid owned output");
        File child = new File(root, name).getCanonicalFile();
        if (!root.getCanonicalFile().equals(child.getParentFile())) throw new IOException("Update output escaped private directory");
        return child;
    }
    private JSONObject required() throws Exception { JSONObject result = record(); if (result == null) throw new IOException("Update has no durable owner"); return result; }
    private JSONObject record() throws Exception { String encoded = store.read(); return encoded == null || encoded.isEmpty() ? null : new JSONObject(encoded); }
    private void persist(JSONObject operation) throws IOException { if (!store.commit(operation.toString())) throw new IOException("Update ownership could not be persisted"); }
    static boolean matchesDigest(File file, String expected) throws Exception {
        if (!file.isFile()) return false;
        MessageDigest sha = MessageDigest.getInstance("SHA-256");
        try (FileInputStream input = new FileInputStream(file)) { byte[] buffer = new byte[32768]; int count; while ((count = input.read(buffer)) != -1) sha.update(buffer, 0, count); }
        StringBuilder actual = new StringBuilder(); for (byte value : sha.digest()) actual.append(String.format(java.util.Locale.ROOT, "%02x", value & 255));
        return MessageDigest.isEqual(expected.getBytes(java.nio.charset.StandardCharsets.US_ASCII), actual.toString().getBytes(java.nio.charset.StandardCharsets.US_ASCII));
    }
}
