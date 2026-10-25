const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { createContext, runInContext } = require('node:vm');

const script = readFileSync(resolve(__dirname, '../app/src/main/assets/official-player.js'), 'utf8')
  .replaceAll('EXPECTED_CID', '20').replaceAll('EXPECTED_EPISODE', '30')
  .replaceAll('EXPECTED_DURATION', '120').replaceAll('REQUESTED_QUALITY', '0');

function fixture({ cid = 20, duration = 120, playing = true, playError, viewportContent } = {}) {
  const handlers = {};
  const state = { playing, rate: 1, seek: null };
  const video = { duration, readyState: 4, currentTime: 0, playbackRate: 1, paused: true, ended: false,
    parentElement: null, style: { setProperty() {} }, calls: 0, pause() { this.paused = true; },
    play() { this.calls++; return playError ? Promise.reject({ name: playError }) : Promise.resolve(); },
    addEventListener(name, fn) { handlers[name] = fn; } };
  const player = { getManifest: () => ({ cid, episodeId: 30 }), getElements: () => ({ container: { querySelector: () => video } }),
    getStates: () => ({}), getQuality: () => ({ realQ: 32 }) };
  let viewport = viewportContent === undefined ? null : { content: viewportContent };
  const document = { getElementById: () => ({}), querySelector: () => viewport,
    createElement: () => ({}), head: { appendChild: element => { viewport = element; } } };
  const context = createContext({ window: { player, __cinemaControl: state }, document });
  return { state, video, handlers, viewport: () => viewport, tick: () => JSON.parse(runInContext(script, context)) };
}

test('a cancelled play during seek/load stays retryable instead of claiming a gesture block', async () => {
  const f = fixture({ playError: 'AbortError' });
  f.tick(); await Promise.resolve();
  assert.equal(f.state.blocked, undefined); assert.equal(f.state.playPending, false);
  f.tick(); assert.equal(f.video.calls, 2);
});
test('an actual autoplay denial is reported and not retried indefinitely', async () => {
  const f = fixture({ playError: 'NotAllowedError' });
  f.tick(); await Promise.resolve();
  assert.equal(f.tick().blocked, true); assert.equal(f.video.calls, 1);
});
test('a different episode or preview is paused before any requested play', () => {
  for (const f of [fixture({ cid: 21 }), fixture({ cid: 21, duration: NaN }), fixture({ duration: 60 })]) {
    f.video.paused = false; f.tick(); assert.equal(f.video.paused, true); assert.equal(f.video.calls, 0);
  }
});
test('remote pause is not relayed as a system block; an unexpected pause is', () => {
  const f = fixture({ playing: false }); f.video.paused = false;
  f.tick(); f.handlers.pause(); assert.equal(f.state.blocked, undefined);
  f.state.playing = true; f.handlers.pause(); assert.equal(f.state.blocked, true);
});
test('position and speed commands apply while paused without starting playback', () => {
  const f = fixture({ playing: false }); f.state.seek = 42; f.state.rate = 1.5;
  const result = f.tick(); assert.equal(result.position, 42); assert.equal(f.video.playbackRate, 1.5);
  assert.equal(f.state.seek, null); assert.equal(f.video.calls, 0);
});

test('cold media can receive play before metadata exists, without claiming readiness', () => {
  const f = fixture({ duration: NaN }); f.video.readyState = 0;
  const result = f.tick();
  assert.equal(f.video.calls, 1);
  assert.equal(result.duration, 0); assert.equal(result.readyState, 0);
});

test('a host pause during metadata loading cancels the retained play request', async () => {
  const f = fixture({ duration: NaN }); f.video.readyState = 0;
  f.tick(); await Promise.resolve();
  f.state.playing = false; f.video.paused = false; f.tick();
  f.video.duration = 120; f.video.readyState = 4; f.tick();
  assert.equal(f.video.paused, true); assert.equal(f.video.calls, 1);
});

test('orientation reports intrinsic dimensions rather than the resized page viewport', () => {
  const f = fixture();
  f.video.videoWidth = 720; f.video.videoHeight = 1280;
  const sample = f.tick();
  assert.equal(sample.videoWidth, 720); assert.equal(sample.videoHeight, 1280);
});

test('video viewport replaces desktop scaling once and stays stable on later samples', () => {
  for (const viewportContent of [undefined, 'width=1100']) {
    const f = fixture({ viewportContent }); f.tick();
    const viewport = f.viewport();
    assert.equal(viewport.content, 'width=device-width,initial-scale=1,minimum-scale=1,maximum-scale=1,user-scalable=no');
    const content = viewport.content;
    Object.defineProperty(viewport, 'content', { get: () => content, set: () => assert.fail('Stable viewport was rewritten') });
    f.tick(); assert.equal(f.viewport(), viewport);
  }
});
