package com.leonz03.bilicinema;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.text.Layout;
import android.text.StaticLayout;
import android.text.TextPaint;
import android.view.View;
import java.util.ArrayList;
import java.util.List;

/** Lightweight timed text overlay shared by native and official-page video surfaces. */
final class MediaOverlayView extends View {
    private static final android.graphics.Typeface TYPEFACE = android.graphics.Typeface.create("sans-serif", android.graphics.Typeface.NORMAL);
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private List<MediaOverlayTracks.Subtitle> subtitles = new ArrayList<>();
    private List<MediaOverlayTracks.Danmaku> danmaku = new ArrayList<>();
    private double position;
    private double rate = 1;
    private long positionAt;
    private int controlsInset;
    private boolean playing;
    private int subtitleIndex = -1;
    private boolean danmakuEnabled;
    private MediaOverlayTracks.Cue cachedCue;
    private StaticLayout cachedSubtitle;
    private int cachedWidth;
    MediaOverlayView(Context context) { super(context); setWillNotDraw(false); setClickable(false); setFocusable(false); }
    void setTracks(List<MediaOverlayTracks.Subtitle> subs, List<MediaOverlayTracks.Danmaku> comments) {
        subtitles = new ArrayList<>(subs); danmaku = new ArrayList<>(comments);
        if (subtitleIndex >= subtitles.size()) subtitleIndex = -1;
        invalidate();
    }
    void setPosition(double value) { position = Math.max(0, value); positionAt = android.os.SystemClock.elapsedRealtime(); invalidate(); }
    void setPlayback(double value, double speed, boolean active) {
        setPosition(value); rate = Math.max(.25, Math.min(3, speed)); playing = active;
    }
    void setControlsInset(int value) { controlsInset = Math.max(0, value); invalidate(); }
    void setSubtitleIndex(int value) { subtitleIndex = value >= 0 && value < subtitles.size() ? value : -1; invalidate(); }
    void setDanmakuEnabled(boolean value) { danmakuEnabled = value; invalidate(); }
    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        double nowPosition = position + (playing ? Math.max(0, android.os.SystemClock.elapsedRealtime() - positionAt) / 1000.0 * rate : 0);
        if (playing && isShown() && (subtitleIndex >= 0 || danmakuEnabled && !danmaku.isEmpty())) postInvalidateOnAnimation();
        float density = getResources().getDisplayMetrics().density;
        paint.setTextAlign(Paint.Align.CENTER); paint.setTypeface(TYPEFACE);
        if (subtitleIndex >= 0) {
            MediaOverlayTracks.Subtitle track = subtitles.get(subtitleIndex);
            for (MediaOverlayTracks.Cue cue : track.cues) {
                if (cue.from > nowPosition) break;
                if (nowPosition >= cue.to) continue;
                int width = Math.max(1, getWidth() - (int)(48 * density));
                if (cue != cachedCue || width != cachedWidth) {
                    TextPaint textPaint = new TextPaint(paint); textPaint.setTextSize(20 * density); textPaint.setColor(0xffffffff);
                    textPaint.setTextAlign(Paint.Align.LEFT);
                    textPaint.setStyle(Paint.Style.FILL); textPaint.setShadowLayer(3 * density, 1, 1, 0xff000000);
                    cachedSubtitle = StaticLayout.Builder.obtain(cue.text, 0, cue.text.length(), textPaint, width)
                            .setAlignment(Layout.Alignment.ALIGN_CENTER).setMaxLines(3).build();
                    cachedCue = cue; cachedWidth = width;
                }
                canvas.save();
                canvas.translate((getWidth() - cachedSubtitle.getWidth()) / 2f,
                        Math.max(4 * density, getHeight() - controlsInset - 12 * density - cachedSubtitle.getHeight()));
                cachedSubtitle.draw(canvas); canvas.restore(); break;
            }
        }
        if (!danmakuEnabled) return;
        paint.setTextSize(18 * density); paint.setStyle(Paint.Style.STROKE); paint.setStrokeWidth(1.5f * density); paint.setColor(0xff000000);
        boolean[] occupied = new boolean[6];
        for (MediaOverlayTracks.Danmaku item : danmaku) {
            double age = nowPosition - item.at; if (age < 0 || age > 8) continue;
            if (occupied[item.lane]) continue;
            occupied[item.lane] = true;
            float y = (36 + item.lane * 30) * density;
            float x = getWidth() - (float)(age / 8.0 * (getWidth() + paint.measureText(item.text)));
            paint.setTextAlign(Paint.Align.LEFT); canvas.drawText(item.text, x, y, paint);
            paint.setStyle(Paint.Style.FILL); paint.setColor(0xff000000 | item.color); canvas.drawText(item.text, x, y, paint);
            paint.setStyle(Paint.Style.STROKE); paint.setColor(0xff000000);
        }
    }
}
