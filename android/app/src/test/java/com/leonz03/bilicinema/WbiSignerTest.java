package com.leonz03.bilicinema;

import org.junit.Test;
import java.util.LinkedHashMap;
import java.util.Map;
import static org.junit.Assert.*;

public class WbiSignerTest {
    private static final String IMG = "7cd084941338484aae1ad9425b84077c";
    private static final String SUB = "4932caff0ff746eab6f01bf08b70ac45";
    @Test public void matchesCoreProtocolVector() throws Exception {
        Map<String, String> parameters = new LinkedHashMap<>();
        parameters.put("foo", "114"); parameters.put("bar", "514"); parameters.put("baz", "1919810");
        assertEquals("bar=514&baz=1919810&foo=114&wts=1702204169&w_rid=6149fdadf571698ca7e6a567265cd0ee",
                WbiSigner.sign(parameters, IMG, SUB, 1702204169));
        assertEquals(3, parameters.size());
    }
    @Test public void formEncodingAndFilteringMatchCore() throws Exception {
        Map<String, String> parameters = new LinkedHashMap<>(); parameters.put("value", "A B+!'()*中文~");
        String signed = WbiSigner.sign(parameters, IMG, SUB, 1702204169);
        assertTrue(signed.startsWith("value=A+B%2B%E4%B8%AD%E6%96%87~&wts="));
    }
    @Test public void keyExtractionAcceptsOnlyFullAsciiKeys() throws Exception {
        assertEquals(IMG, WbiSigner.extractKey("https://i0.hdslb.com/bfs/wbi/" + IMG + ".png"));
        try { WbiSigner.extractKey("https://i0.hdslb.com/bfs/wbi/short.png"); fail("Invalid key accepted"); }
        catch (PlaybackFailure failure) { assertEquals(PlaybackFailure.Reason.INVALID_MEDIA, failure.reason); }
    }
    @Test public void resolverCloseIsPermanentAndIdempotent() throws Exception {
        BilibiliMediaResolver resolver = new BilibiliMediaResolver();
        resolver.cancel(); resolver.close(); resolver.close(); resolver.cancel();
        try { resolver.input("BV1xx411c7mD", ""); fail("Closed resolver accepted work"); }
        catch (java.util.concurrent.CancellationException expected) { assertNotNull(expected); }
    }
}
