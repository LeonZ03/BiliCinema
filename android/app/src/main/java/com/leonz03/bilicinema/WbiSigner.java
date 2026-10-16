package com.leonz03.bilicinema;

import java.net.URI;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Map;
import java.util.TreeMap;

/** Same WBI form encoding and key permutation as DownKyi.Core/BiliApi/Sign/WbiSign.cs. */
final class WbiSigner {
    private static final int[] MIXIN = {46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13};
    private WbiSigner() { }

    static String extractKey(String address) throws PlaybackFailure {
        try {
            URI uri = new URI(address);
            String path = uri.getPath();
            if (!PlaybackSource.validUri(uri) || path == null) throw new IllegalArgumentException();
            String file = path.substring(path.lastIndexOf('/') + 1);
            String key = file.substring(0, file.lastIndexOf('.'));
            if (!key.matches("[A-Za-z0-9]{32}")) throw new IllegalArgumentException();
            return key;
        } catch (Exception exception) { throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA); }
    }

    static String sign(Map<String, String> parameters, String imgKey, String subKey, long unixSeconds) throws PlaybackFailure {
        if (imgKey == null || subKey == null || !imgKey.matches("[A-Za-z0-9]{32}")
                || !subKey.matches("[A-Za-z0-9]{32}") || unixSeconds <= 0) {
            throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        }
        TreeMap<String, String> sorted = new TreeMap<>(parameters);
        sorted.remove("w_rid");
        sorted.put("wts", Long.toString(unixSeconds));
        StringBuilder query = new StringBuilder(), mixin = new StringBuilder();
        String keys = imgKey + subKey;
        for (int index : MIXIN) mixin.append(keys.charAt(index));
        for (Map.Entry<String, String> pair : sorted.entrySet()) {
            if (pair.getValue() == null) continue;
            if (query.length() > 0) query.append('&');
            query.append(encode(pair.getKey())).append('=').append(encode(pair.getValue().replaceAll("[!'()*]", "")));
        }
        try {
            // MD5 is the required Bilibili protocol signature, not a local trust primitive.
            byte[] digest = MessageDigest.getInstance("MD5").digest((query.toString() + mixin).getBytes(StandardCharsets.UTF_8));
            StringBuilder hex = new StringBuilder();
            for (byte value : digest) hex.append(Character.forDigit((value >>> 4) & 15, 16)).append(Character.forDigit(value & 15, 16));
            return query.append("&w_rid=").append(hex).toString();
        } catch (java.security.NoSuchAlgorithmException exception) { throw new IllegalStateException("Required WBI digest unavailable"); }
    }

    private static String encode(String value) {
        try { return URLEncoder.encode(value, "UTF-8").replace("*", "%2A").replace("%7E", "~"); }
        catch (java.io.UnsupportedEncodingException exception) { throw new IllegalStateException("Required UTF-8 unavailable"); }
    }
}
