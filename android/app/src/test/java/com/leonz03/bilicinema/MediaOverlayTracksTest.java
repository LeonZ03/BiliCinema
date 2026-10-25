package com.leonz03.bilicinema;

import org.junit.Test;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.List;
import static org.junit.Assert.*;

public final class MediaOverlayTracksTest {
    @Test public void readsReal64BitIdsAndSkipsUnknown64BitFieldsWithoutShiftingSegmentTime() throws Exception {
        ByteArrayOutputStream element = new ByteArrayOutputStream();
        field(element, 1, 1L << 50);
        field(element, 2, 360125);
        field(element, 3, 1);
        field(element, 5, 0xffffff);
        field(element, 8, 1L << 45);
        bytes(element, 7, "一起看".getBytes(StandardCharsets.UTF_8));
        ByteArrayOutputStream reply = new ByteArrayOutputStream();
        bytes(reply, 1, element.toByteArray());
        List<MediaOverlayTracks.Danmaku> result = MediaOverlayTracks.parseDanmaku(reply.toByteArray());
        assertEquals(1, result.size());
        assertEquals(1L << 50, result.get(0).id);
        assertEquals(360.125, result.get(0).at, .00001);
        assertEquals("一起看", result.get(0).text);
    }

    @Test public void rejectsTruncatedUnknownFieldsInsteadOfOverflowingTheReader() {
        ByteArrayOutputStream reply = new ByteArrayOutputStream();
        varint(reply, (20 << 3) | 2);
        varint(reply, Integer.MAX_VALUE);
        assertThrows(IOException.class, () -> MediaOverlayTracks.parseDanmaku(reply.toByteArray()));
    }

    @Test public void supportsZeroTimeAndIgnoresUnsupportedCommentModes() throws Exception {
        ByteArrayOutputStream reply = new ByteArrayOutputStream();
        for (int mode : new int[]{1, 7}) {
            ByteArrayOutputStream element = new ByteArrayOutputStream();
            field(element, 1, mode); field(element, 2, 0); field(element, 3, mode);
            bytes(element, 7, "开始".getBytes(StandardCharsets.UTF_8));
            bytes(reply, 1, element.toByteArray());
        }
        assertEquals(1, MediaOverlayTracks.parseDanmaku(reply.toByteArray()).size());
    }

    private static void field(ByteArrayOutputStream output, int field, long value) {
        varint(output, field << 3); varint(output, value);
    }
    private static void bytes(ByteArrayOutputStream output, int field, byte[] value) {
        varint(output, (field << 3) | 2); varint(output, value.length); output.write(value, 0, value.length);
    }
    private static void varint(ByteArrayOutputStream output, long value) {
        while ((value & ~127L) != 0) { output.write((int) (value & 127) | 128); value >>>= 7; }
        output.write((int) value);
    }
}
