'use strict';

// Offline process fixture: deliberately sends malformed/noisy output and delayed replies.
const readline = require('node:readline');
const input = readline.createInterface({ input: process.stdin });
let sessionCookie = '';
const profile = { id: 123, nickname: '测试账号', avatarUrl: 'https://example.test/avatar.jpg', vipType: 11 };
input.on('close', () => process.exit(0));
input.on('line', line => {
  const request = JSON.parse(line);
  const reply = result => process.stdout.write(JSON.stringify({ id: request.id, result }) + '\n');
  if (request.method === 'authResume') { sessionCookie = request.params.cookie; return reply({}); }
  if (request.method === 'authSetSession') { sessionCookie = request.params.cookie === 'fixture-session' ? request.params.cookie : ''; return reply({ profile: sessionCookie ? profile : null }); }
  if (request.method === 'authStatus') return reply({ profile: sessionCookie ? profile : null });
  if (request.method === 'authLogout') { sessionCookie = ''; return reply({}); }
  if (request.method === 'authQrCreate') return reply({ key: 'fixture-key', qrImage: 'data:image/png;base64,AA==', qrUrl: 'https://music.163.com/login?codekey=fixture-key' });
  if (request.method === 'authQrCheck') return reply({ code: 803, message: '授权成功', cookie: 'fixture-session' });
  if (request.method === 'userPlaylists') return reply({ playlists: [{ id: 456, creatorId: 123, title: '我的歌单', trackCount: 7 }], more: request.params.offset === 0 });
  if (request.method === 'playlist') return reply({ id: 456, creatorId: 123, title: '我的歌单', tracks: [] });
  if (request.method === 'playlistPage') return reply(request.params.trackIds.map(id => ({ id, title: '分页歌曲', albumId: 90, fee: 8 })));
  if (request.method === 'trackArtists') return reply([{ id: 7, name: '歌手' }, { id: 8, name: 'Guest' }]);
  if (request.method === 'trackMetadata') return reply({ id: request.params.id, title: '声', artist: '歌手', artists: [{ id: 7, name: '歌手' }], album: '专辑', albumId: 90, fee: 8 });
  if (request.method === 'artist') return reply({ id: request.params.id, name: '歌手', artworkUrl: 'https://example.test/artist.jpg', description: '介绍', tracks: [{ id: 42, title: '声', artist: '歌手', artists: [{ id: 7, name: '歌手' }] }] });
  if (request.method === 'artistSongs') return reply({ tracks: [{ id: 42 + request.params.offset, title: '声', artists: [{ id: 7, name: '歌手' }] }], total: 3, more: request.params.offset === 0 });
  if (request.method === 'artistAlbums') return reply({ albums: [{ id: 90, title: '作品集', artist: '歌手', artworkUrl: 'https://example.test/album.jpg', publishTime: 1234567890000, trackCount: 3 }], more: request.params.offset === 0 });
  if (request.method === 'album') return reply({ id: request.params.id, title: '作品集', artist: '歌手', artworkUrl: 'https://example.test/album.jpg', tracks: [{ id: 42, title: '声', artists: [{ id: 7, name: '歌手' }] }] });
  if (request.method === 'playlistTracks') {
    const { id, operation, trackIds } = request.params;
    if (!sessionCookie || !['add', 'del'].includes(operation) || id !== 456 || !Array.isArray(trackIds) ||
      trackIds.length > 500 || new Set(trackIds).size !== trackIds.length || trackIds.some(id => !Number.isSafeInteger(id) || id <= 0))
      return process.stdout.write(JSON.stringify({ id: request.id, error: { code: 'FIXTURE_INVALID', message: 'Invalid mutation request' } }) + '\n');
    if (trackIds.includes(9999)) return process.stdout.write(JSON.stringify({ id: request.id, error: { code: 'FIXTURE_FAILURE', message: 'Mutation rejected' } }) + '\n');
    if (trackIds.includes(8888)) return setTimeout(() => reply({ count: trackIds.length }), 150);
    return reply({ count: trackIds.length });
  }
  if (request.method === 'stream') return reply({ url: 'https://example.test/audio.flac', quality: request.params.quality, isPreview: !sessionCookie });
  if (request.method === 'lyrics') return reply({ lines: [{ timeMs: 1000, durationMs: 500, text: '声', translation: '声音', romanization: 'koe', words: [{ startMs: 1000, durationMs: 500, text: '声' }] }] });
  if (request.method === 'crash') process.exit(7);
  if (request.method === 'slow') {
    return setTimeout(() => process.stdout.write(JSON.stringify({ id: request.id, result: { late: true } }) + '\n'), 300);
  }
  if (request.method === 'malformed') {
    return process.stdout.write(JSON.stringify({ id: request.id, error: 'invalid error shape' }) + '\n');
  }
  process.stdout.write('not-json\n[1]\n{"id":null}\n{"id":"invalid"}\n');
  process.stdout.write(JSON.stringify({ id: request.id, result: { ok: true, echo: request.params?.value } }) + '\n');
});
