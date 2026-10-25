package com.leonz03.bilicinema;

import org.json.JSONObject;

/** Safe observations only: no account data, media addresses or license material cross this boundary. */
final class OfficialPlaybackSnapshot {
    final boolean ready, playing, ended, blocked;
    final double position, duration;
    final int quality;
    final float videoAspectRatio;

    OfficialPlaybackSnapshot(JSONObject sample, PlaybackSource source) throws PlaybackFailure {
        JSONObject media = source.media();
        long cid = sample.optLong("cid"), episode = sample.optLong("episodeId");
        if (cid > 0 && (cid != media.optLong("cid")
                || (media.optLong("episodeId") > 0 && episode != media.optLong("episodeId"))))
            throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        long aid = sample.optLong("aid");
        String bvid = sample.optString("bvid", "");
        if ((aid > 0 && aid != media.optLong("aid")) || (!bvid.isEmpty() && !bvid.equals(media.optString("bvid"))))
            throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        if (sample.optInt("error") != 0 || sample.optBoolean("initError"))
            throw new PlaybackFailure(PlaybackFailure.Reason.DRM_PROTECTED);
        position = sample.optDouble("position", 0); duration = sample.optDouble("duration", 0);
        if (!Double.isFinite(position) || !Double.isFinite(duration) || position < 0 || duration < 0)
            throw new PlaybackFailure(PlaybackFailure.Reason.INVALID_MEDIA);
        // A preview or trailer must never start the room's full-film timeline.
        if (cid > 0 && duration > 0 && duration + Math.max(5, source.durationSeconds * .02) < source.durationSeconds)
            throw new PlaybackFailure(PlaybackFailure.Reason.PERMISSION_DENIED);
        ready = cid > 0 && duration > 0 && sample.optInt("readyState") >= 3 && !sample.optBoolean("qualityPending");
        playing = ready && !sample.optBoolean("paused", true) && !sample.optBoolean("ended");
        ended = ready && sample.optBoolean("ended"); blocked = sample.optBoolean("blocked");
        quality = sample.optInt("quality");
        int width = sample.optInt("videoWidth"), height = sample.optInt("videoHeight");
        videoAspectRatio = cid > 0 && width > 0 && height > 0 ? (float) width / height : 0;
    }
}
