'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createBridge } = require('../bridge.cjs');

test('search distinguishes free, VIP, purchased album and member quality without guessing unknown fees', async () => {
  const fees = [0, 1, 4, 8, null, undefined, false, '', '0', 16];
  const bridge = createBridge(async () => ({ body: { code: 200, result: {
    songs: fees.map((fee, index) => ({ id: index + 1, name: String(index), fee })), songCount: fees.length,
  } } }));
  const result = await bridge('search', { keywords: 'test' });
  assert.deepEqual(result.tracks.map(track => track.fee), [0, 1, 4, 8, null, null, null, null, 0, null]);
});

test('privilege fallback joins by song ID, preserves fee zero, and never treats purchased access as free', async () => {
  const bridge = createBridge(async () => ({ body: { code: 200, result: {
    songs: [
      { id: 1, fee: 0 },
      { id: 2, privilege: { fee: 8 } },
      { id: 3, fee: 1, privilege: { payed: 1, pl: 999000 } },
      { id: 4, privilege: { fee: 4 } },
    ],
    privileges: [{ id: 4, fee: 4, payed: 1 }, { id: 2, fee: 8 }, { id: 1, fee: 0 }],
  } } }));
  const tracks = (await bridge('search', { keywords: 'test' })).tracks;
  assert.deepEqual(tracks.map(track => track.fee), [0, 8, 1, 4]);
});

test('playlist detail batches retain fee and rights even when privileges are a separate response array', async () => {
  const bridge = createBridge(async (name, params) => {
    if (name === 'playlist_detail') return { body: { code: 200,
      playlist: { id: 10, tracks: [{ id: 1 }], trackIds: [{ id: 1 }, { id: 2 }, { id: 3 }] },
      privileges: [{ id: 1, fee: 1 }],
    } };
    assert.equal(name, 'song_detail'); assert.equal(params.ids, '1,2,3');
    return { body: { code: 200, songs: [{ id: 3 }, { id: 1 }, { id: 2 }],
      privileges: [{ id: 2, fee: 4 }, { id: 3, fee: 8 }],
    } };
  });
  const tracks = (await bridge('playlist', { id: 10 })).tracks;
  assert.deepEqual(tracks.map(track => [track.id, track.fee]), [[1, 1], [2, 4], [3, 8]]);
});

test('known cloud and unavailable rights are retained without inferring rights from noCopyrightRcmd', async () => {
  const bridge = createBridge(async () => ({ body: { code: 200, result: { songs: [
    { id: 1, fee: 0, t: 1 },
    { id: 2, fee: 0, privilege: { cs: true } },
    { id: 3, fee: 0, pc: {} },
    { id: 4, fee: 0, privilege: { st: -200 } },
    { id: 5, fee: 0, privilege: { toast: true } },
    { id: 6, fee: 0, noCopyrightRcmd: { type: 2 } },
  ] } } }));
  const tracks = (await bridge('search', { keywords: 'test' })).tracks;
  assert.deepEqual(tracks.map(track => track.isCloudTrack), [true, true, true, false, false, false]);
  assert.deepEqual(tracks.map(track => track.isUnavailable), [false, false, false, true, true, false]);
});

test('discovery wrappers and album contents preserve catalogue fees', async () => {
  const bridge = createBridge(async name => {
    if (name === 'personalized') return { body: { code: 200, result: [] } };
    if (name === 'personalized_newsong') return { body: { code: 200, result: [{ id: 1, song: { id: 1, fee: 1 } }] } };
    assert.equal(name, 'album');
    return { body: { code: 200, album: { id: 10 }, songs: [{ id: 2 }], privileges: [{ id: 2, fee: 4 }] } };
  });
  assert.equal((await bridge('discover')).newSongs[0].fee, 1);
  assert.equal((await bridge('album', { id: 10 })).tracks[0].fee, 4);
});

test('song metadata resolves original song ID, album ID and fee together without text search', async () => {
  const bridge = createBridge(async (name, params) => {
    assert.equal(name, 'song_detail'); assert.equal(params.ids, '42');
    return { body: { code: 200,
      songs: [{ id: 99, fee: 0, al: { id: 900 } }, { id: 42, name: '声', album: { id: 90, name: '专辑' }, ar: [{ id: 7, name: '歌手' }] }],
      privileges: [{ id: 42, fee: 8 }],
    } };
  });
  const track = await bridge('trackMetadata', { id: 42 });
  assert.equal(track.id, 42); assert.equal(track.albumId, 90); assert.equal(track.album, '专辑');
  assert.equal(track.fee, 8); assert.deepEqual(track.artists, [{ id: 7, name: '歌手' }]);
  const missing = createBridge(async () => ({ body: { code: 200, songs: [{ id: 99 }] } }));
  await assert.rejects(missing('trackMetadata', { id: 42 }));
  await assert.rejects(missing('trackMetadata', { id: -1 }), error => error.code === 'INVALID_ARGUMENT');
});

test('album IDs retain current and legacy shapes, while absent or invalid IDs stay unknown', async () => {
  const bridge = createBridge(async () => ({ body: { code: 200, result: { songs: [
    { id: 1, al: { id: 10 } }, { id: 2, album: { id: 20 } },
    { id: 3 }, { id: 4, al: { id: -1 } }, { id: 5, al: { id: 9007199254740992 } },
  ] } } }));
  assert.deepEqual((await bridge('search', { keywords: 'test' })).tracks.map(track => track.albumId), [10, 20, 0, 0, 0]);
});
