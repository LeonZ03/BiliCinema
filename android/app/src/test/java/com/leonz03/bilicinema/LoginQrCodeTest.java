package com.leonz03.bilicinema;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.RGBLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.HybridBinarizer;
import java.util.Collections;
import java.util.EnumMap;
import java.util.Map;
import org.junit.Test;
import static org.junit.Assert.*;

public class LoginQrCodeTest {
    private static final String CHALLENGE = "https://passport.bilibili.com/h5-app/passport/login/scan"
            + "?navhide=1&auth_code=local-test-challenge-001&gourl=https%3A%2F%2Fwww.bilibili.com%2F";

    @Test public void generatedPixelsDecodeToTheOriginalLoginChallenge() throws Exception {
        for (int size : new int[]{128, 257, 512}) {
            int[] pixels = LoginQrCode.pixels(CHALLENGE, size);
            assertEquals(size * size, pixels.length);
            Result decoded = decode(pixels, size);
            assertEquals(BarcodeFormat.QR_CODE, decoded.getBarcodeFormat());
            assertEquals(CHALLENGE, decoded.getText());
        }
    }

    @Test public void nonAsciiContentSurvivesRasterEncodingAndDecoding() throws Exception {
        String content = "BiliCinema 本机登录 · 一起看 😀";
        assertEquals(content, decode(LoginQrCode.pixels(content, 512), 512).getText());
    }

    @Test public void requestedSquareHasOpaquePixelsAndAtLeastFourQuietModulesOnEverySide() throws Exception {
        for (int size : new int[]{128, 257, 512}) {
            int[] pixels = LoginQrCode.pixels(CHALLENGE, size);
            assertEquals(size * size, pixels.length);
            int left = size, top = size, right = -1, bottom = -1;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) {
                int color = pixels[y * size + x];
                if (color != 0xff000000 && color != 0xffffffff) fail("QR pixels must be opaque black or white");
                if (color == 0xff000000) {
                    left = Math.min(left, x); right = Math.max(right, x);
                    top = Math.min(top, y); bottom = Math.max(bottom, y);
                }
            }
            assertTrue("The raster must contain the QR symbol", right >= left && bottom >= top);
            // A finder pattern's top edge is exactly seven modules. Derive the physical
            // module size from the output pixels, independently of encoder hints.
            int finderEdge = 0;
            while (left + finderEdge < size && pixels[top * size + left + finderEdge] == 0xff000000) finderEdge++;
            assertEquals(0, finderEdge % 7);
            int module = finderEdge / 7;
            assertTrue("Finder modules must occupy at least one pixel", module > 0);
            assertTrue("Left quiet zone", left >= 4 * module);
            assertTrue("Top quiet zone", top >= 4 * module);
            assertTrue("Right quiet zone", size - right - 1 >= 4 * module);
            assertTrue("Bottom quiet zone", size - bottom - 1 >= 4 * module);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) {
                if (x < left || x > right || y < top || y > bottom)
                    assertEquals("The quiet zone must remain white", 0xffffffff, pixels[y * size + x]);
            }
        }
    }

    private static Result decode(int[] pixels, int size) throws Exception {
        RGBLuminanceSource source = new RGBLuminanceSource(size, size, pixels);
        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE));
        hints.put(DecodeHintType.CHARACTER_SET, "UTF-8");
        return new MultiFormatReader().decode(new BinaryBitmap(new HybridBinarizer(source)), hints);
    }
}
