'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createBridge, trackOf } = require('../bridge.cjs');

test('song metadata keeps stable multi-artist IDs and legacy display names', async () => {
  const song = { id: 42, name: '声', ar: [{ id: 7, name: '声の人' }, { id: 8, name: 'Guest' }, { id: 7, name: '声の人' }, { id: 0, name: 'Unknown' }] };
  const track = trackOf(song);
  assert.deepEqual(track.artists, [{ id: 7, name: '声の人' }, { id: 8, name: 'Guest' }]);
  assert.match(track.artist, /声の人/);
  const calls = [];
  const dispatch = createBridge(async (name, params) => {
    calls.push(name); assert.equal(params.ids, '42');
    return { body: { code: 200, songs: [song] } };
  });
  assert.deepEqual(await dispatch('trackArtists', { id: 42 }), track.artists);
  assert.deepEqual(calls, ['song_detail']);
  const missing = createBridge(async () => ({ body: { code: 200, songs: [{ id: 99, ar: song.ar }] } }));
  assert.deepEqual(await missing('trackArtists', { id: 42 }), []);
});

test('artist metadata and song pagination preserve artwork and upstream boundaries', async () => {
  const dispatch = createBridge(async (name, params) => {
    assert.equal(params.id, 7);
    if (name === 'artists') return { body: { code: 200, artist: { id: 7, name: '歌手', picUrl: 'http://example.test/artist.jpg', briefDesc: '介绍' }, hotSongs: [{ id: 42, ar: [{ id: 7, name: '歌手' }] }] } };
    assert.equal(name, 'artist_songs'); assert.equal(params.order, 'time'); assert.equal(params.limit, 2);
    return { body: { code: 200, total: 3, songs: params.offset === 0 ? [{ id: 42 }, { id: 43 }] : [{ id: 44 }] } };
  });
  const artist = await dispatch('artist', { id: 7 });
  assert.equal(artist.name, '歌手'); assert.equal(artist.description, '介绍');
  assert.equal(artist.artworkUrl, 'https://example.test/artist.jpg'); assert.equal(artist.tracks[0].artists[0].id, 7);
  assert.equal((await dispatch('artistSongs', { id: 7, limit: 2, offset: 0 })).more, true);
  assert.equal((await dispatch('artistSongs', { id: 7, limit: 2, offset: 2 })).more, false);
});

test('album pages retain release date, next-page flag and parent artwork on songs', async () => {
  const album = { id: 90, name: '作品集', picUrl: 'http://example.test/album.jpg', artist: { id: 7, name: '歌手' }, publishTime: 1234567890000, size: 3 };
  const dispatch = createBridge(async (name, params) => {
    if (name === 'artist_album') { assert.equal(params.offset, 30); return { body: { code: 200, hotAlbums: [album], more: false } }; }
    assert.equal(name, 'album'); assert.equal(params.id, 90);
    return { body: { code: 200, album, songs: [{ id: 42, name: '声', ar: [{ id: 7, name: '歌手' }] }] } };
  });
  const page = await dispatch('artistAlbums', { id: 7, offset: 30, limit: 30 });
  assert.equal(page.more, false); assert.equal(page.albums[0].publishTime, 1234567890000); assert.equal(page.albums[0].trackCount, 3);
  const detail = await dispatch('album', { id: 90 });
  assert.equal(detail.artist, '歌手'); assert.equal(detail.tracks[0].album, '作品集');
  assert.equal(detail.tracks[0].artworkUrl, 'https://example.test/album.jpg');
});

test('artist requests reject bad IDs/pages and malformed upstream payloads', async () => {
  let calls = 0;
  const dispatch = createBridge(async () => { calls++; return { body: { code: 200 } }; });
  for (const method of ['artist', 'trackArtists', 'artistSongs', 'artistAlbums', 'album'])
    await assert.rejects(dispatch(method, { id: -1 }), error => error.code === 'INVALID_ARGUMENT');
  for (const params of [{ offset: -1 }, { offset: Infinity }, { offset: 0.5 }, { limit: 0 }])
    await assert.rejects(dispatch('artistSongs', { id: 7, ...params }), error => error.code === 'INVALID_ARGUMENT');
  assert.equal(calls, 0);
  for (const method of ['artist', 'artistSongs', 'artistAlbums', 'album']) await assert.rejects(dispatch(method, { id: 7 }));
});
