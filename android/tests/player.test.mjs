import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync(new URL('../app/src/main/assets/player.js', import.meta.url), 'utf8');
function fixture() {
    const messages = [], events = new Map(), timers = new Map();
    let cid = 202, nextTimer = 0;
    const video = {
        isConnected: true, currentTime: 10, playbackRate: 1, paused: true, readyState: 4,
        closest: () => null,
        addEventListener(name, action) { if (!events.has(name)) events.set(name, new Set()); events.get(name).add(action); },
        removeEventListener(name, action) { events.get(name)?.delete(action); },
        pause() { this.paused = true; for (const action of events.get('pause') || []) action(); },
        play() { this.paused = false; for (const action of events.get('play') || []) action(); return Promise.resolve(); }
    };
    const window = { player: { getCid: () => cid, getElements: () => ({ container: { querySelector: () => video } }) } };
    const bridge = { postMessage: value => messages.push(JSON.parse(value)) }; window.BiliCinemaNative = bridge;
    const context = vm.createContext({ window, BiliCinemaNative: bridge, document: { querySelector: () => video }, location: { href: 'https://www.bilibili.com/video/BV1xx411c7mD?p=2' }, setInterval: action => { timers.set(++nextTimer, action); return nextTimer; }, clearInterval: id => timers.delete(id), Date, Number, Object, JSON });
    return { window, video, messages, events, timers, setCid: value => { cid = value; }, install: generation => vm.runInContext(source.replace('__BC_GENERATION__', String(generation)), context) };
}
test('every probe samples the actual player CID, including same-URL SPA changes', () => {
    const app = fixture(); app.install(1); assert.equal(app.messages.at(-1).cid, 202);
    app.setCid(303); [...app.timers.values()][0](); assert.equal(app.messages.at(-1).cid, 303); assert.equal(app.messages.at(-1).generation, 1);
});
test('old queued document callbacks retain their generation after a replacement', () => {
    const app = fixture(); app.install(1); const oldHandler = [...app.events.get('canplay')][0]; app.install(2);
    assert.equal(app.timers.size, 1); oldHandler(); assert.equal(app.messages.at(-1).generation, 1);
    app.window.__bcProbe(); assert.equal(app.messages.at(-1).generation, 2);
});
test('applying a remote pause does not echo a host pause event', () => {
    const app = fixture(); app.install(1); app.messages.length = 0; app.window.__bcApply(20, 1.25, false, true);
    assert.equal(app.video.currentTime, 20); assert.equal(app.video.playbackRate, 1.25); assert.equal(app.messages.filter(message => message.event === 'pause').length, 0);
});
test('metadata carries its actual readyState without advertising ready', () => {
    const app = fixture(); app.video.readyState = 1; app.install(1); [...app.events.get('loadedmetadata')][0]();
    assert.equal(app.messages.at(-1).event, 'metadata'); assert.equal(app.messages.at(-1).readyState, 1);
    assert.equal(app.messages.filter(message => message.event === 'ready').length, 0);
    app.video.readyState = 3; [...app.events.get('canplay')][0](); assert.equal(app.messages.at(-1).event, 'ready'); assert.equal(app.messages.at(-1).readyState, 3);
});
test('probes expose buffer loss after the player was previously ready', () => {
    const app = fixture(); app.install(1); assert.equal(app.messages.at(-1).readyState, 4);
    app.video.readyState = 1; app.window.__bcProbe(); assert.equal(app.messages.at(-1).event, 'probe'); assert.equal(app.messages.at(-1).readyState, 1);
});
