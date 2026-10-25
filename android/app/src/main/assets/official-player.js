(() => {
    const p = window.player || window.bilibiliPlayer;
    const m = p?.getManifest?.() || {};
    const root = p?.getElements?.()?.container;
    const v = root?.querySelector('video');
    const s = window.__cinemaControl ||= { playing: false, rate: 1, seek: null };
    if (!v) return JSON.stringify({ cid: Number(m.cid) || 0, episodeId: Number(m.episodeId) || 0 });
    const matches = Number(m.cid) === EXPECTED_CID && (!EXPECTED_EPISODE || Number(m.episodeId) === EXPECTED_EPISODE);
    const full = Number.isFinite(v.duration) && v.duration + Math.max(5, EXPECTED_DURATION * .02) >= EXPECTED_DURATION;
    // An authorized official player may need play() to fetch metadata/license data. Unknown
    // duration is loading, not evidence of a preview; readiness still requires the full duration.
    const preview = Number.isFinite(v.duration) && v.duration > 0 && !full;
    if (!matches || preview) v.pause();
    else {
        // The native video surface owns the viewport. Desktop page scaling can
        // otherwise survive a portrait-to-landscape resize and crop the film.
        let viewport = document.querySelector('meta[name="viewport"]');
        if (!viewport) {
            viewport = document.createElement('meta'); viewport.name = 'viewport';
            document.head.appendChild(viewport);
        }
        const viewportContent = 'width=device-width,initial-scale=1,minimum-scale=1,maximum-scale=1,user-scalable=no';
        if (viewport.content !== viewportContent) viewport.content = viewportContent;
        if (!document.getElementById('cinema-video-only')) {
            const style = document.createElement('style'); style.id = 'cinema-video-only';
            style.textContent = 'html,body{margin:0!important;overflow:hidden!important;background:#000!important}' +
                'body{visibility:hidden!important} video{visibility:visible!important;position:fixed!important;inset:0!important;' +
                'width:100vw!important;height:100vh!important;min-width:0!important;min-height:0!important;max-width:none!important;' +
                'max-height:none!important;object-fit:contain!important;z-index:2147483647!important;transform:none!important}';
            document.head.appendChild(style);
        }
        for (let e = v.parentElement; e && e !== document.documentElement; e = e.parentElement) {
            e.style.setProperty('transform', 'none', 'important');
            e.style.setProperty('overflow', 'visible', 'important');
            e.style.setProperty('contain', 'none', 'important');
            e.style.setProperty('clip-path', 'none', 'important');
        }
        // The official player also writes inline centering offsets. A stylesheet cannot override
        // its important inline declarations, so this viewport owner must set all geometry together.
        const geometry = { position: 'fixed', left: '0', top: '0', right: 'auto', bottom: 'auto',
            width: '100vw', height: '100vh', margin: '0', padding: '0', transform: 'none',
            'object-fit': 'contain', 'object-position': '50% 50%' };
        for (const [key, value] of Object.entries(geometry)) v.style.setProperty(key, value, 'important');
        v.controls = false; v.disablePictureInPicture = true;
        if (full && !s.qualityStarted) {
            s.qualityStarted = true;
            if (REQUESTED_QUALITY > 0 && p.requestQuality) {
                s.qualityPending = true;
                Promise.resolve(p.requestQuality(REQUESTED_QUALITY)).then(() => { s.qualityPending = false; }, () => { s.qualityPending = false; s.qualityRejected = true; });
            }
        }
        if (v.readyState >= 1 && s.seek !== null) { v.currentTime = s.seek; s.seek = null; }
        if (!v.__cinemaPauseObserved) {
            v.__cinemaPauseObserved = true;
            v.addEventListener('pause', () => {
                if (s.playing && !v.ended && !v.seeking && !s.qualityPending) s.blocked = true;
            });
        }
        if (v.playbackRate !== s.rate) v.playbackRate = s.rate;
        if (s.playing && !s.qualityPending && v.paused && !v.ended && !s.playPending && !s.blocked) {
            s.playPending = true;
            v.play().then(() => { s.playPending = false; }, error => {
                s.playPending = false;
                // A seek/load can cancel play. Cancellation is not a user-gesture or focus block.
                if (error.name === 'NotAllowedError') s.blocked = true;
                else if (error.name !== 'AbortError') s.playError = true;
            });
        } else if (!s.playing && !v.paused) v.pause();
    }
    return JSON.stringify({cid: Number(m.cid) || 0, episodeId: Number(m.episodeId) || 0,
        aid: Number(m.aid) || 0, bvid: typeof m.bvid === 'string' ? m.bvid : '',
        position: Number.isFinite(v.currentTime) ? v.currentTime : 0, duration: Number.isFinite(v.duration) ? v.duration : 0,
        videoWidth: v.videoWidth || 0, videoHeight: v.videoHeight || 0,
        readyState: v.readyState, paused: v.paused, ended: v.ended, blocked: !!s.blocked,
        error: v.error?.code || (s.playError ? 4 : 0), initError: !!p.getStates?.()?.initError,
        quality: Number(p.getQuality?.()?.realQ) || 0, qualityPending: !!s.qualityPending});
})()
