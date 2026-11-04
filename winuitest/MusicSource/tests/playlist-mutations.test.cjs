'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createBridge, playlistOf } = require('../bridge.cjs');

const account = { body: { code: 200, profile: { userId: 7, nickname: 'owner' } } };
const detail = { body: { code: 200, playlist: { id: 11, creator: { userId: 7 } } } };
const mutation = { id: 11, operation: 'add', trackIds: [1, 2, 1] };
const options = { initialCookie: 'MUSIC_U=fixture-cookie' };

test('playlist metadata exposes known owner but never guesses ownership', () => {
  assert.equal(playlistOf({ id: 1, creator: { userId: 7 } }).creatorId, 7);
  assert.equal(playlistOf({ id: 1, userId: 9 }).creatorId, 9);
  assert.equal(playlistOf({ id: 1 }).creatorId, 0);
  assert.equal(playlistOf({ id: 1, userId: -2 }).creatorId, 0);
});

test('add and delete verify login and current ownership, deduplicate, and write once', async () => {
  const calls = [];
  const bridge = createBridge(async (name, params) => {
    calls.push(name);
    assert.equal(params.cookie, 'MUSIC_U=fixture-cookie');
    if (name === 'login_status') return account;
    if (name === 'playlist_detail') { assert.equal(params.id, 11); return detail; }
    assert.equal(name, 'playlist_tracks');
    assert.equal(params.pid, 11);
    assert.equal(params.tracks, '1,2');
    assert.ok(['add', 'del'].includes(params.op));
    return { body: { code: 200 } };
  }, options);
  assert.deepEqual(await bridge('playlistTracks', mutation), { count: 2 });
  assert.deepEqual(await bridge('playlistTracks', { ...mutation, operation: 'del' }), { count: 2 });
  assert.deepEqual(calls, ['login_status', 'playlist_detail', 'playlist_tracks', 'login_status', 'playlist_detail', 'playlist_tracks']);
});

test('anonymous, bad IDs, missing owner and subscribed playlists cannot be edited', async () => {
  await assert.rejects(createBridge(async () => { assert.fail('Anonymous operation reached SDK'); })('playlistTracks', mutation), { code: 'LOGIN_REQUIRED' });
  const invalid = createBridge(async () => { assert.fail('Bad arguments reached SDK'); }, options);
  for (const args of [{ ...mutation, id: -1 }, { ...mutation, trackIds: [] }, { ...mutation, trackIds: [0] },
    { ...mutation, trackIds: [true] }, { ...mutation, trackIds: [9007199254740992] }, { ...mutation, trackIds: Array(501).fill(1) }, { ...mutation, operation: 'replace' }])
    await assert.rejects(invalid('playlistTracks', args), { code: 'INVALID_ARGUMENT' });
  for (const creator of [undefined, { userId: 8 }]) {
    const bridge = createBridge(async name => {
      if (name === 'login_status') return account;
      assert.equal(name, 'playlist_detail');
      return { body: { code: 200, playlist: { id: 11, creator } } };
    }, options);
    await assert.rejects(bridge('playlistTracks', mutation), { code: 'PLAYLIST_READ_ONLY' });
  }
});

test('logout during ownership lookup prevents any later write', async () => {
  let finish;
  let lookupStarted;
  const started = new Promise(resolve => { lookupStarted = resolve; });
  const bridge = createBridge(async name => {
    if (name === 'login_status') return account;
    if (name === 'logout') return { body: { code: 200 } };
    if (name === 'playlist_detail') { lookupStarted(); return new Promise(resolve => { finish = resolve; }); }
    assert.fail('Stale account wrote a playlist');
  }, options);
  const pending = bridge('playlistTracks', mutation);
  await started;
  await bridge('authLogout');
  finish(detail);
  await assert.rejects(pending, { code: 'SESSION_CHANGED' });
});

test('upstream rejected and uncertain writes never retry or return success', async () => {
  for (const failure of [{ body: { code: 512, message: '不能添加' } }, { body: { code: 301, message: '登录过期' } },
    Object.assign(new Error('timeout'), { code: 'ETIMEDOUT' })]) {
    let writes = 0;
    const bridge = createBridge(async name => {
      if (name === 'login_status') return account;
      if (name === 'playlist_detail') return detail;
      assert.equal(name, 'playlist_tracks');
      writes++;
      throw failure;
    }, options);
    await assert.rejects(bridge('playlistTracks', mutation));
    assert.equal(writes, 1);
  }
});

test('a missing mutation acknowledgement cannot be reported as a successful write', async () => {
  for (const response of [{ body: {} }, {}, { body: { data: { code: 200 } } }, { body: '<html>login required</html>' }]) {
    let writes = 0;
    const bridge = createBridge(async name => {
      if (name === 'login_status') return account;
      if (name === 'playlist_detail') return detail;
      assert.equal(name, 'playlist_tracks');
      writes++;
      return response;
    }, options);
    await assert.rejects(bridge('playlistTracks', mutation), { code: 'MUTATION_UNCONFIRMED' });
    assert.equal(writes, 1);
  }
});
