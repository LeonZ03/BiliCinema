package com.leonz03.bilicinema;
import org.junit.Test;
import static org.junit.Assert.*;

public class UpdateManagerTest {
    @Test public void versionOrderIsNumericAndStableOnly() {
        assertTrue(UpdateManager.newer("1.10.0", "1.9.9")); assertFalse(UpdateManager.newer("1.4.0", "1.4.0")); assertFalse(UpdateManager.newer("2.0.0-beta", "1.4.0"));
    }
    @Test public void selectsExactChecksumEntry() throws Exception {
        String hash = new String(new char[64]).replace('\0', 'a');
        assertEquals(hash, UpdateManager.checksum(hash + "  other.apk\n" + hash + " *BiliCinema.apk\n", "BiliCinema.apk"));
    }
    @Test(expected = java.io.IOException.class) public void rejectsMissingOrMalformedChecksum() throws Exception { UpdateManager.checksum("123  BiliCinema.apk", "BiliCinema.apk"); }
}
