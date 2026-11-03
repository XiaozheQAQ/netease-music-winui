'use strict';

const { spawn } = require('node:child_process');
const readline = require('node:readline');
const path = require('node:path');
const assert = require('node:assert/strict');

async function main() {
  const child = spawn(process.execPath, [path.join(__dirname, '../service.cjs')], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  const waiting = new Map();
  let nextId = 0;
  let diagnostics = '';
  child.stderr.on('data', chunk => { diagnostics = (diagnostics + chunk).slice(-3000); });
  const input = readline.createInterface({ input: child.stdout });
  input.on('line', line => {
    const message = JSON.parse(line);
    const slot = waiting.get(message.id);
    if (!slot) return;
    waiting.delete(message.id);
    clearTimeout(slot.timer);
    message.error ? slot.reject(new Error(message.error.message)) : slot.resolve(message.result);
  });
  child.on('exit', code => {
    for (const slot of waiting.values()) { clearTimeout(slot.timer); slot.reject(new Error(`Source exited (${code}): ${diagnostics}`)); }
  });
  function call(method, params = {}) {
    const id = ++nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { waiting.delete(id); reject(new Error(`${method} timed out: ${diagnostics}`)); }, 35000);
      waiting.set(id, { resolve, reject, timer });
      child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
    });
  }
  try {
    const health = await call('health');
    assert.equal(health.transport, 'stdio');
    // Exercise the actual anonymous QR flow without displaying a login key or
    // collecting a user's credentials. Confirming login remains a user action.
    await call('authLogout');
    assert.equal((await call('authStatus')).profile, null);
    const qr = await call('authQrCreate');
    assert.ok(qr.key && qr.qrImage.startsWith('data:image/png;base64,'));
    const qrState = await call('authQrCheck', { key: qr.key });
    assert.ok([800, 801, 802].includes(qrState.code));
    assert.equal(qrState.cookie, undefined);
    const search = await call('search', { keywords: '陈奕迅', limit: 5 });
    assert.ok(search.total > 0 && search.tracks.length > 0);
    const feed = await call('discover');
    assert.ok(feed.playlists.length > 0 || feed.newSongs.length > 0);
    const playlist = feed.playlists.length ? await call('playlist', { id: feed.playlists[0].id }) : { tracks: [] };
    const track = search.tracks[0];
    const lyrics = await call('lyrics', { id: track.id });
    assert.ok(Array.isArray(lyrics.lines));
    const translatedSearch = await call('search', { keywords: 'だれかの心臓になれたなら', limit: 5 });
    const translatedLyrics = translatedSearch.tracks.length ? await call('lyrics', { id: translatedSearch.tracks[0].id }) : { lines: [] };
    let stream;
    // Prefer a known generally available catalogue song; source restrictions remain legitimate failures.
    for (const candidate of [...feed.newSongs, ...search.tracks].slice(0, 8)) {
      try { stream = await call('stream', { id: candidate.id, quality: 'lossless' }); if (stream.url) break; } catch { }
    }
    assert.ok(stream?.url, 'No available stream was returned by NetEase for smoke-test candidates.');
    const response = await fetch(stream.url, { headers: { Range: 'bytes=0-1023' }, signal: AbortSignal.timeout(20000) });
    assert.ok(response.ok, `Audio HTTP ${response.status}`);
    const reader = response.body.getReader();
    const chunk = await reader.read();
    assert.ok(chunk.value?.length > 0, 'Audio stream has no bytes');
    await reader.cancel();
    console.log(JSON.stringify({ health, qrState: qrState.code, searchCount: search.total, track: track.title, playlists: feed.playlists.length, newSongs: feed.newSongs.length, playlistTracks: playlist.tracks.length, lyricLines: lyrics.lines.length,
      translatedLines: translatedLyrics.lines.filter(line => line.translation).length, romanizedLines: translatedLyrics.lines.filter(line => line.romanization).length,
      audioStatus: response.status, preview: stream.isPreview, requestedQuality: stream.requestedQuality, actualQuality: stream.quality, qualityFallback: stream.fellBack }, null, 2));
  } finally {
    child.stdin.end();
    child.kill();
  }
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
