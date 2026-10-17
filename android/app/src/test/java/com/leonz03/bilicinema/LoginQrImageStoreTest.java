package com.leonz03.bilicinema;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import java.io.File;
import java.io.IOException;
import java.net.URI;
import java.nio.file.Files;
import java.util.Base64;
import java.util.concurrent.atomic.AtomicLong;
import static org.junit.Assert.*;

public class LoginQrImageStoreTest {
    @Rule public TemporaryFolder temp = new TemporaryFolder();
    private static final String AUTHORITY = "com.leonz03.bilicinema.loginqr";
    private static final byte[] PNG = Base64.getDecoder().decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO9W2ioAAAAASUVORK5CYII=");
    private static final class Store implements LoginQrImageStore.Store {
        String record = "";
        int commits, failAt = -1;
        Runnable beforeCommit = () -> { };
        public String read() { return record; }
        public boolean commit(String next) {
            commits++; beforeCommit.run();
            if (commits == failAt) return false;
            record = next; return true;
        }
    }
    private File root() throws IOException { return new File(temp.newFolder(), "login-qr"); }
    private LoginQrImageStore owner(File root, Store store, AtomicLong now) { return new LoginQrImageStore(root, store, now::get, AUTHORITY); }
    private void unavailable(LoginQrImageStore owner, URI uri) throws Exception {
        try { owner.readableFile(uri); fail("Unavailable image was readable"); }
        catch (IOException expected) { assertFalse(expected.getMessage().contains(temp.getRoot().getAbsolutePath())); }
    }

    @Test public void ownershipIsCommittedBeforeTheFirstDirectoryOrByteIsWritten() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        store.beforeCommit = () -> { if (store.commits == 1) assertFalse(root.exists()); };
        LoginQrImageStore owner = owner(root, store, now);
        LoginQrImageStore.Prepared image = owner.prepare(PNG, 5000);
        JSONObject entry = new JSONObject(store.record).getJSONArray("images").getJSONObject(0);
        assertEquals("READY", entry.getString("phase"));
        assertTrue(entry.getString("file").matches("qr-[a-f0-9]{32}\\.png"));
        assertEquals("content", image.uri.getScheme()); assertEquals(AUTHORITY, image.uri.getAuthority());
        assertArrayEquals(PNG, Files.readAllBytes(owner.readableFile(image.uri).toPath()));
    }
    @Test public void ownershipCommitFailureCreatesNoOutput() throws Exception {
        Store store = new Store(); store.failAt = 1; File root = root();
        try { owner(root, store, new AtomicLong(1000)).prepare(PNG, 5000); fail("Ownership failure was ignored"); }
        catch (IOException expected) { assertFalse(root.exists()); assertEquals("", store.record); }
    }
    @Test public void completeImageRemainsReadableAfterItsPreparingActivityOwnerIsGone() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        LoginQrImageStore.Prepared image = owner(root, store, now).prepare(PNG, 5000);
        LoginQrImageStore recreated = owner(root, store, now);
        assertArrayEquals(PNG, Files.readAllBytes(recreated.readableFile(image.uri).toPath()));
    }
    @Test public void preparingANewImageRetainsThePreviouslyCopiedOutput() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        LoginQrImageStore owner = owner(root, store, now);
        LoginQrImageStore.Prepared previous = owner.prepare(PNG, 5000), next = owner.prepare(PNG, 5000);
        assertNotEquals(previous.uri, next.uri);
        assertTrue(owner.readableFile(previous.uri).isFile()); assertTrue(owner.readableFile(next.uri).isFile());
        assertEquals(2, new JSONObject(store.record).getJSONArray("images").length());
    }
    @Test public void failedReplacementCommitPreservesTheOldImageAndRecoversOnlyTheNewOutput() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        LoginQrImageStore owner = owner(root, store, now);
        LoginQrImageStore.Prepared previous = owner.prepare(PNG, 5000);
        store.failAt = store.commits + 2;
        try { owner.prepare(PNG, 5000); fail("Final commit failure was ignored"); }
        catch (IOException expected) { assertNotNull(expected); }
        assertArrayEquals(PNG, Files.readAllBytes(owner.readableFile(previous.uri).toPath()));
        assertEquals(1, new JSONObject(store.record).getJSONArray("images").length());
        assertEquals(1, root.list().length);
    }
    @Test public void recoveryRemovesRecordedPartialAndRenamedInterruptedFilesWithoutEnumeratingOthers() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        LoginQrImageStore owner = owner(root, store, now);
        LoginQrImageStore.Prepared previous = owner.prepare(PNG, 5000);
        String name = "qr-0123456789abcdef0123456789abcdef.png";
        JSONObject record = new JSONObject(store.record);
        record.getJSONArray("images").put(new JSONObject().put("file", name).put("expiresAt", 5000).put("phase", "WRITING"));
        store.record = record.toString();
        File partial = new File(root, name.replace(".png", ".part")), renamed = new File(root, name), unrelated = new File(root, "keep.png");
        Files.write(partial.toPath(), PNG); Files.write(renamed.toPath(), PNG); Files.write(unrelated.toPath(), PNG);
        owner(root, store, now).recover();
        assertFalse(partial.exists()); assertFalse(renamed.exists()); assertTrue(unrelated.exists());
        assertTrue(owner.readableFile(previous.uri).isFile());
    }
    @Test public void readsAtTheBoundedExpiryRefuseAndRemoveOnlyRecordedImages() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        LoginQrImageStore owner = owner(root, store, now);
        LoginQrImageStore.Prepared image = owner.prepare(PNG, now.get() + 2 * LoginQrImageStore.MAX_LIFETIME_MS);
        assertEquals(1000 + LoginQrImageStore.MAX_LIFETIME_MS, image.expiresAt);
        File imageFile = owner.readableFile(image.uri), unrelated = new File(root, "keep.png");
        Files.write(unrelated.toPath(), PNG);
        now.set(image.expiresAt); unavailable(owner, image.uri);
        assertFalse(imageFile.exists()); assertTrue(unrelated.exists());
        assertEquals(0, new JSONObject(store.record).getJSONArray("images").length());
    }
    @Test public void aWellNamedButUnrecordedPngIsNeitherExposedNorDeleted() throws Exception {
        Store store = new Store(); File root = root(); assertTrue(root.mkdirs()); AtomicLong now = new AtomicLong(1000);
        String name = "qr-0123456789abcdef0123456789abcdef.png";
        File unowned = new File(root, name); Files.write(unowned.toPath(), PNG);
        unavailable(owner(root, store, now), URI.create("content://" + AUTHORITY + "/" + name));
        assertTrue(unowned.exists());
    }
    @Test public void uriAuthorityAndPathChecksRejectAliasesTraversalAndUpdateFiles() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        LoginQrImageStore owner = owner(root, store, now);
        LoginQrImageStore.Prepared image = owner.prepare(PNG, 5000);
        String path = image.uri.getRawPath();
        for (String address : new String[]{"file://" + AUTHORITY + path, "content://other.loginqr" + path,
                "content://user@" + AUTHORITY + path, "content://" + AUTHORITY + ":80" + path,
                image.uri + "?extra=1", image.uri + "#fragment", "content://" + AUTHORITY + "/../" + path.substring(1),
                "content://" + AUTHORITY + "/%2e%2e%2f" + path.substring(1),
                "content://" + AUTHORITY + "/nested" + path, "content://" + AUTHORITY + "/BiliCinema-1.5.0-android-universal.apk"}) {
            unavailable(owner, URI.create(address));
        }
        assertTrue(owner.readableFile(image.uri).isFile());
    }
    @Test public void corruptOwnershipCannotDeleteAFileOutsideThePrivateDirectory() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        File outside = new File(root.getParentFile(), "outside.png"); Files.write(outside.toPath(), PNG);
        store.record = new JSONObject().put("version", 1).put("images", new JSONArray().put(new JSONObject()
                .put("file", "../outside.png").put("expiresAt", 1).put("phase", "WRITING"))).toString();
        try { owner(root, store, now).recover(); fail("Corrupt owner was accepted"); }
        catch (IOException expected) { assertTrue(outside.isFile()); }
    }
    @Test public void expiredOrInvalidInputDoesNotAcquireOutputOwnership() throws Exception {
        Store store = new Store(); File root = root(); AtomicLong now = new AtomicLong(1000);
        LoginQrImageStore owner = owner(root, store, now);
        try { owner.prepare(PNG, 1000); fail("Expired image accepted"); }
        catch (IOException expected) { assertEquals("", store.record); }
        try { owner.prepare(new byte[]{1, 2, 3}, 5000); fail("Non-PNG accepted"); }
        catch (IOException expected) { assertFalse(root.exists()); }
    }
}
