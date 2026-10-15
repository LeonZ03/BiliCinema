package com.leonz03.bilicinema;

import org.json.JSONObject;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import java.io.File;
import java.nio.file.Files;
import static org.junit.Assert.*;

public class UpdateJournalTest {
    @Rule public TemporaryFolder temp = new TemporaryFolder();
    private static final String NAME = "BiliCinema-1.5.0-android-universal.apk";
    private static final String EMPTY_HASH = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static class Store implements UpdateJournal.Store { String record = ""; boolean writable = true; public String read() { return record; } public boolean commit(String next) { if (!writable) return false; record = next; return true; } }
    @Test public void pathsAreDurableBeforeTheFirstOutputWrite() throws Exception {
        Store store = new Store(); File root = temp.newFolder(); UpdateJournal journal = new UpdateJournal(root, store);
        journal.begin(NAME, EMPTY_HASH, "1.5.0"); JSONObject state = new JSONObject(store.record);
        assertEquals(NAME, state.getString("name")); assertEquals("update.part", state.getString("partial")); assertEquals("DOWNLOADING", state.getString("phase")); assertFalse(journal.partial().exists());
    }
    @Test public void interruptedDownloadIsRemovedAndEndsInDurableCancellation() throws Exception {
        Store store = new Store(); File root = temp.newFolder(); UpdateJournal journal = new UpdateJournal(root, store); journal.begin(NAME, EMPTY_HASH, "1.5.0"); Files.write(journal.partial().toPath(), new byte[]{1, 2});
        assertNull(new UpdateJournal(root, store).recover("1.4.0", file -> true)); assertFalse(new File(root, "update.part").exists()); assertEquals("CANCELLED", new JSONObject(store.record).getString("phase"));
    }
    @Test public void verifiedPackageAndInstallerPhaseSurviveRestart() throws Exception {
        Store store = new Store(); File root = temp.newFolder(); UpdateJournal journal = new UpdateJournal(root, store); journal.begin(NAME, EMPTY_HASH, "1.5.0"); Files.write(journal.partial().toPath(), new byte[0]); File apk = journal.commitVerified(); journal.installerLaunched();
        assertEquals(apk, new UpdateJournal(root, store).recover("1.4.0", file -> true)); assertEquals("VERIFIED", new JSONObject(store.record).getString("phase"));
        assertNull(new UpdateJournal(root, store).recover("1.5.0", file -> true)); assertFalse(apk.exists()); assertEquals("COMPLETED", new JSONObject(store.record).getString("phase"));
    }
    @Test public void badRecoveredPackageIsRemovedInsteadOfInstalled() throws Exception {
        Store store = new Store(); File root = temp.newFolder(); UpdateJournal journal = new UpdateJournal(root, store); journal.begin(NAME, EMPTY_HASH, "1.5.0"); Files.write(journal.partial().toPath(), new byte[0]); File apk = journal.commitVerified(); Files.write(apk.toPath(), new byte[]{9});
        assertNull(new UpdateJournal(root, store).recover("1.4.0", file -> true)); assertFalse(apk.exists());
    }
    @Test public void ownershipPersistenceFailurePreventsOutputCreation() throws Exception {
        Store store = new Store(); store.writable = false; File root = new File(temp.getRoot(), "updates");
        try { new UpdateJournal(root, store).begin(NAME, EMPTY_HASH, "1.5.0"); fail("ownership failure must abort"); } catch (java.io.IOException expected) { assertFalse(root.exists()); }
    }
    @Test public void crashAtCommitBoundaryRecoversTheRecordedPartial() throws Exception {
        Store store = new Store(); File root = temp.newFolder(); UpdateJournal journal = new UpdateJournal(root, store); journal.begin(NAME, EMPTY_HASH, "1.5.0"); Files.write(journal.partial().toPath(), new byte[0]);
        store.record = new JSONObject(store.record).put("phase", "COMMITTING").toString();
        File recovered = new UpdateJournal(root, store).recover("1.4.0", file -> true); assertNotNull(recovered); assertEquals(NAME, recovered.getName()); assertFalse(new File(root, "update.part").exists());
    }
}
