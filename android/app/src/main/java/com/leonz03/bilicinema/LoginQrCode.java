package com.leonz03.bilicinema;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.EncodeHintType;
import com.google.zxing.WriterException;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.qrcode.QRCodeWriter;
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel;
import java.util.EnumMap;
import java.util.Map;

/** Local raster encoding keeps the login challenge away from third-party image services. */
final class LoginQrCode {
    static int[] pixels(String address, int size) throws WriterException {
        Map<EncodeHintType, Object> hints = new EnumMap<>(EncodeHintType.class);
        hints.put(EncodeHintType.CHARACTER_SET, "UTF-8");
        hints.put(EncodeHintType.ERROR_CORRECTION, ErrorCorrectionLevel.M);
        hints.put(EncodeHintType.MARGIN, 4);
        BitMatrix matrix = new QRCodeWriter().encode(address, BarcodeFormat.QR_CODE, size, size, hints);
        int[] pixels = new int[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) pixels[y * size + x] = matrix.get(x, y) ? 0xff000000 : 0xffffffff;
        return pixels;
    }
    private LoginQrCode() { }
}
