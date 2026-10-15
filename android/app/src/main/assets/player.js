(function (generation) {
    'use strict';
    const previous = window.__bcSession;
    if (previous && previous.generation === generation) {
        previous.probe();
        return;
    }
    if (previous) previous.dispose();
    const state = { generation, mutedUntil: 0, video: null, handlers: [], timer: null };
    function video() {
        for (const api of [window.player, window.bilibiliPlayer]) {
            try {
                const root = api && typeof api.getElements === 'function' && api.getElements().container;
                const item = root && root.querySelector('video');
                if (item && item.isConnected) return item;
            } catch (_) { /* The official player may be replaced during episode navigation. */ }
        }
        return document.querySelector('.bpx-player-container video, #bilibili-player video, #bilibiliPlayer video, video');
    }
    function cid() {
        for (const api of [window.player, window.bilibiliPlayer]) {
            try {
                const value = api && typeof api.getCid === 'function' && Number(api.getCid());
                if (Number.isSafeInteger(value) && value > 0) return value;
            } catch (_) { /* An unavailable identity is sent as zero; native readiness stays false. */ }
        }
        return 0;
    }
    function emit(event) {
        const current = video();
        if (!current || !window.BiliCinemaNative) return;
        BiliCinemaNative.postMessage(JSON.stringify({
            generation, event, url: location.href, cid: cid(), readyState: Number(current.readyState) || 0,
            position: Number(current.currentTime) || 0,
            rate: Number(current.playbackRate) || 1,
            playing: !current.paused
        }));
    }
    function unbind() {
        for (const [item, name, handler] of state.handlers) item.removeEventListener(name, handler);
        state.handlers = [];
    }
    function fitPlayer(current) {
        const root = current.closest('.bpx-player-container, .bilibili-player') || current.closest('#bilibili-player, #bilibiliPlayer');
        if (!root) return;
        const viewport = document.querySelector('meta[name="viewport"]') || document.createElement('meta');
        viewport.name = 'viewport';
        viewport.content = 'width=device-width, initial-scale=1';
        if (!viewport.parentNode) document.head.appendChild(viewport);
        let kept = root;
        for (let parent = kept.parentElement; parent && parent !== document.documentElement; parent = kept.parentElement) {
            for (const sibling of parent.children) if (sibling !== kept && sibling.tagName !== 'SCRIPT' && sibling.tagName !== 'STYLE') sibling.style.setProperty('display', 'none', 'important');
            parent.style.setProperty('transform', 'none', 'important');
            parent.style.setProperty('overflow', 'visible', 'important');
            parent.style.setProperty('min-width', '0', 'important');
            kept = parent;
        }
        for (const [property, value] of Object.entries({ position: 'fixed', inset: '0', width: '100vw', height: '100vh', 'min-width': '0', 'z-index': '2147483000', margin: '0' })) root.style.setProperty(property, value, 'important');
        document.documentElement.style.overflow = 'hidden';
        document.body.style.margin = '0';
        document.body.style.background = '#000';
    }
    state.probe = function () {
        const current = video();
        if (!current) return;
        if (current !== state.video) {
            unbind(); state.video = current; fitPlayer(current);
            for (const name of ['loadedmetadata', 'canplay', 'waiting', 'stalled', 'playing', 'play', 'pause', 'seeked', 'ratechange', 'error']) {
                const handler = function () {
                    if (['play', 'pause', 'seeked', 'ratechange'].includes(name) && Date.now() < state.mutedUntil) return;
                    emit(name === 'canplay' ? 'ready' : name === 'loadedmetadata' ? 'metadata' : name);
                };
                current.addEventListener(name, handler); state.handlers.push([current, name, handler]);
            }
        }
        emit('probe');
    };
    state.dispose = function () { clearInterval(state.timer); unbind(); };
    window.__bcSession = state;
    window.__bcVideo = video;
    window.__bcProbe = state.probe;
    window.__bcPause = function () { state.mutedUntil = Date.now() + 1800; const current = video(); if (current) current.pause(); };
    window.__bcApply = function (position, rate, playing, force) {
        const current = video(); if (!current) return;
        state.mutedUntil = Date.now() + 1800;
        if (force || Math.abs(current.currentTime - position) > 1.5) current.currentTime = position;
        current.playbackRate = rate;
        if (playing) { const result = current.play(); if (result && result.catch) result.catch(() => emit('blocked')); }
        else current.pause();
    };
    state.timer = setInterval(state.probe, 700);
    state.probe();
})(__BC_GENERATION__);
