'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createBridge, parseLyrics, parseYrc, addLyricCompanions, normalizeCookie } = require('../bridge.cjs');

test('search normalizes NetEase track shapes and preserves paging', async () => {
  const bridge = createBridge(async (method, params) => {
    assert.equal(method, 'cloudsearch');
    assert.equal(params.offset, 30);
    assert.equal(params.keywords, '周杰伦');
    return { body: { code: 200, result: { songCount: 123, songs: [{ id: 42, name: '晴天', ar: [{ name: '周杰伦' }], al: { name: '叶惠美', picUrl: 'http://example.com/a.jpg' }, dt: 269000 }] } } };
  });
  const result = await bridge('search', { keywords: ' 周杰伦 ', offset: 30, limit: 30 });
  assert.equal(result.total, 123);
  assert.deepEqual(result.tracks[0], { id: 42, title: '晴天', artist: '周杰伦', artists: [], album: '叶惠美', artworkUrl: 'https://example.com/a.jpg', durationMs: 269000 });
});

test('LRC supports multiple timestamps, fractional seconds and offset', () => {
  assert.deepEqual(parseLyrics('[offset:100]\n[00:02.3][00:04.56] 再见 \n[00:01.001]开始\n[ar:artist]'), [
    { timeMs: 1101, text: '开始' }, { timeMs: 2400, text: '再见' }, { timeMs: 4660, text: '再见' },
  ]);
});

test('YRC keeps absolute word timestamps, durations, punctuation and spacing', () => {
  const lines = parseYrc('[16210,3460](16210,670,0)还(16880,410,0)没， (17290,800,0)Hello world!');
  assert.deepEqual(lines, [{
    timeMs: 16210, durationMs: 3460, text: '还没， Hello world!',
    words: [
      { startMs: 16210, durationMs: 670, text: '还' },
      { startMs: 16880, durationMs: 410, text: '没， ' },
      { startMs: 17290, durationMs: 800, text: 'Hello world!' },
    ],
  }]);
});

test('YRC skips metadata and malformed headers; unsegmented lines get no fabricated words', () => {
  const lines = parseYrc('{"t":0,"c":[{"tx":"作曲"}]}\n{"broken metadata\n[not,a,time]ignored\n[-2,10]invalid\n[9007199254740992,1]overflow\n[1000,2000]  Untimed (vocal) line  \n[4000,500](4000,500,0)好');
  assert.equal(lines.length, 2);
  assert.deepEqual(lines[0], { timeMs: 1000, durationMs: 2000, text: '  Untimed (vocal) line  ', words: [] });
  assert.equal(lines[1].words[0].startMs, 4000);
  assert.deepEqual(parseYrc(null), []);
});

test('lyrics prefer supplied YRC and fall back to LRC when unavailable or endpoint fails', async () => {
  const calls = [];
  const yrc = createBridge(async method => {
    calls.push(method);
    return { body: { code: 200, yrc: { lyric: '[1000,500](1000,500,0)好' }, lrc: { lyric: '[00:01]old' } } };
  });
  assert.equal((await yrc('lyrics', { id: 1 })).lines[0].words[0].text, '好');
  assert.deepEqual(calls, ['lyric_new']);

  const noYrc = createBridge(async method => {
    assert.equal(method, 'lyric_new');
    return { body: { code: 200, lrc: { lyric: '[00:02]fallback' } } };
  });
  assert.deepEqual((await noYrc('lyrics', { id: 1 })).lines, [{ timeMs: 2000, text: 'fallback' }]);

  const legacy = createBridge(async method => {
    if (method === 'lyric_new') throw new Error('Endpoint unavailable');
    assert.equal(method, 'lyric');
    return { body: { code: 200, lrc: { lyric: '[00:03]legacy' } } };
  });
  assert.deepEqual((await legacy('lyrics', { id: 1 })).lines, [{ timeMs: 3000, text: 'legacy' }]);
});

test('unavailable streams become actionable errors, preview is preserved', async () => {
  await assert.rejects(createBridge(async () => ({ body: { code: 200, data: [{ id: 1, url: null }] } }))('stream', { id: 1 }), { code: 'UNAVAILABLE' });
  const bridge = createBridge(async (method, params) => {
    assert.equal(method, 'song_url_v1');
    assert.equal(params.unblock, 'false');
    return { body: { code: 200, data: [{ id: 1, url: 'https://m.music.126.net/a.mp3', freeTrialInfo: { start: 0, end: 30 } }] } };
  });
  assert.equal((await bridge('stream', { id: 1 })).isPreview, true);
});

test('preview seconds map to the full-song lyric offset while full tracks remain at zero', async () => {
  const stream = async freeTrialInfo => createBridge(async () => ({ body: { code: 200, data: [{ id: 1, url: 'https://m.music.126.net/a.mp3', freeTrialInfo }] } }))('stream', { id: 1 });
  assert.equal((await stream({ start: 72.5, end: 102.5 })).startOffsetMs, 72500);
  assert.equal((await stream({ fragmentType: 6, start: 0, end: 30 })).startOffsetMs, 0);
  assert.deepEqual(await stream(null), { url: 'https://m.music.126.net/a.mp3', isPreview: false, startOffsetMs: 0, quality: '', requestedQuality: 'exhigh', fellBack: false });
  assert.equal((await stream('null')).isPreview, false);
  assert.equal((await stream({ start: -4, end: 26 })).startOffsetMs, 0);
  assert.equal((await stream({ start: 10, end: 5 })).startOffsetMs, 0);
});

test('discovery preserves one working upstream section', async () => {
  const bridge = createBridge(async method => {
    if (method === 'personalized') throw new Error('offline');
    return { body: { code: 200, result: [{ id: 9, song: { id: 9, name: '新歌', album: { name: '专辑' }, artists: [{ name: '歌手' }], duration: 1000 } }] } };
  });
  const result = await bridge('discover');
  assert.deepEqual(result.playlists, []);
  assert.equal(result.newSongs[0].title, '新歌');
});

test('playlist resolves all permitted track metadata in order', async () => {
  const bridge = createBridge(async (method, params) => {
    if (method === 'playlist_detail') return { body: { code: 200, playlist: { id: 5, name: '歌单', tracks: [], trackIds: [{ id: 1 }, { id: 2 }] } } };
    assert.equal(method, 'song_detail');
    assert.equal(params.ids, '1,2');
    return { body: { code: 200, songs: [{ id: 1, name: '一' }, { id: 2, name: '二' }] } };
  });
  const result = await bridge('playlist', { id: 5 });
  assert.deepEqual(result.tracks.map(song => song.id), [1, 2]);
});

test('large playlists load beyond 500 tracks with bounded concurrency and preserve order', async () => {
  const ids = Array.from({ length: 723 }, (_, index) => index + 1);
  let active = 0;
  let peak = 0;
  const bridge = createBridge(async (method, params) => {
    if (method === 'playlist_detail') return { body: { code: 200, playlist: { id: 5, name: '大歌单', tracks: [], trackIds: ids.map(id => ({ id })) } } };
    active++;
    peak = Math.max(peak, active);
    await new Promise(resolve => setTimeout(resolve, 1));
    active--;
    return { body: { code: 200, songs: params.ids.split(',').map(id => ({ id: Number(id), name: id })).reverse() } };
  });
  const result = await bridge('playlist', { id: 5 });
  assert.deepEqual(result.tracks.map(track => track.id), ids);
  assert.ok(peak <= 4);
  assert.ok(peak > 1);
});

test('bad arguments and upstream failure are not empty successes', async () => {
  const bridge = createBridge(async () => ({ body: { code: 301, msg: '需要登录' } }));
  await assert.rejects(bridge('search', { keywords: '' }), { code: 'INVALID_ARGUMENT' });
  await assert.rejects(bridge('playlist', { id: -1 }), { code: 'INVALID_ARGUMENT' });
  await assert.rejects(bridge('search', { keywords: 'x' }), { message: '需要登录' });
});

test('network and timeout diagnostics become user-facing messages while login messages survive', async () => {
  const dnsFailure = createBridge(async () => { throw Object.assign(new Error('getaddrinfo ENOTFOUND music.163.com'), { code: 'ENOTFOUND' }); });
  await assert.rejects(dnsFailure('search', { keywords: 'music' }), { message: '无法连接网易云音乐，请检查网络后重试。' });
  const timeout = createBridge(async () => { throw Object.assign(new Error('timeout of 22000ms exceeded'), { code: 'ECONNABORTED' }); });
  await assert.rejects(timeout('search', { keywords: 'music' }), { message: '网易云音乐响应超时，请稍后重试。' });
  const nestedFailure = createBridge(async () => { throw { body: { code: 502, msg: 'Error: connect ECONNREFUSED 1.2.3.4:443' } }; });
  await assert.rejects(nestedFailure('search', { keywords: 'music' }), { message: '无法连接网易云音乐，请检查网络后重试。' });
  const login = createBridge(async () => { throw { body: { code: 301, message: '需要登录' } }; });
  await assert.rejects(login('search', { keywords: 'music' }), { message: '需要登录' });
});

const loginBody = (id = 7) => ({ body: { data: { code: 200, account: { id }, profile: { userId: id, nickname: '音乐爱好者', avatarUrl: 'http://example.com/avatar.jpg', vipType: 11 } } } });

test('QR lifecycle allows non-200 statuses and returns credentials only at 803', async () => {
  let status = 801;
  const bridge = createBridge(async (method, params) => {
    assert.deepEqual(params.cookie, {});
    assert.ok(params.timestamp > 0);
    if (method === 'login_qr_key') return { body: { code: 200, data: { code: 200, unikey: 'qr-key' } } };
    if (method === 'login_qr_create') {
      assert.equal(params.key, 'qr-key');
      assert.equal(params.qrimg, true);
      return { body: { code: 200, data: { qrimg: 'data:image/png;base64,cG5n', qrurl: 'https://music.163.com/login?codekey=qr-key' } } };
    }
    assert.equal(method, 'login_qr_check');
    assert.equal(params.key, 'qr-key');
    return { body: { code: status, cookie: 'MUSIC_U=secret-login; Path=/; HttpOnly; __csrf=csrf-token' } };
  });
  const qr = await bridge('authQrCreate');
  assert.equal(qr.key, 'qr-key');
  assert.match(qr.qrImage, /^data:image\/png;base64,/);
  for (status of [800, 801, 802]) {
    const result = await bridge('authQrCheck', { key: qr.key });
    assert.equal(result.code, status);
    assert.equal(result.cookie, undefined);
  }
  status = 803;
  assert.equal((await bridge('authQrCheck', { key: qr.key })).cookie, 'MUSIC_U=secret-login; __csrf=csrf-token');
  assert.deepEqual(await bridge('authStatus'), { profile: null }, 'QR polling must not silently activate a session');
});

test('validated account cookies reach search, stream, and paginated user playlists', async () => {
  const cookie = 'MUSIC_U=verified-token; __csrf=verified-csrf';
  const bridge = createBridge(async (method, params) => {
    assert.equal(params.cookie, cookie);
    if (method === 'login_status') return loginBody();
    if (method === 'cloudsearch') return { body: { code: 200, result: { songs: [] } } };
    if (method === 'song_url_v1') {
      assert.equal(params.level, 'lossless');
      assert.equal(params.unblock, 'false');
      return { body: { code: 200, data: [{ id: 1, url: 'https://m.music.126.net/song.flac', level: 'lossless', freeTrialInfo: null }] } };
    }
    assert.equal(method, 'user_playlist');
    assert.equal(params.uid, 7);
    assert.equal(params.offset, 50);
    assert.equal(params.limit, 50);
    return { body: { code: 200, more: true, playlist: [{ id: 11, name: '我的歌单', coverImgUrl: 'http://example.com/cover.jpg', trackCount: 123 }] } };
  });
  assert.equal((await bridge('authSetSession', { cookie })).profile.vipType, 11);
  await bridge('search', { keywords: 'music' });
  const stream = await bridge('stream', { id: 1, quality: 'lossless' });
  assert.equal(stream.quality, 'lossless');
  assert.equal(stream.isPreview, false);
  assert.equal(stream.fellBack, false);
  const playlists = await bridge('userPlaylists', { uid: 7, offset: 50, limit: 50 });
  assert.equal(playlists.more, true);
  assert.equal(playlists.playlists[0].trackCount, 123);
  assert.equal(playlists.playlists[0].artworkUrl, 'https://example.com/cover.jpg');
});

test('logout clears locally even on remote failure and never reuses environment cookie', async () => {
  let loggedOut = false;
  const bridge = createBridge(async (method, params) => {
    if (method === 'login_status') return loginBody();
    if (method === 'logout') { loggedOut = true; assert.equal(params.cookie, 'MUSIC_U=old-token'); throw new Error('offline'); }
    assert.deepEqual(params.cookie, {});
    return { body: { code: 200, result: { songs: [] } } };
  }, { initialCookie: 'MUSIC_U=old-token' });
  assert.ok((await bridge('authStatus')).profile);
  assert.deepEqual(await bridge('authLogout'), {});
  assert.equal(loggedOut, true);
  assert.deepEqual(await bridge('authStatus'), { profile: null });
  await bridge('search', { keywords: 'x' });
  await assert.rejects(bridge('userPlaylists', { uid: 7 }), { code: 'LOGIN_REQUIRED' });
});

test('invalid, expired, and failed replacement sessions cannot retain the previous account', async () => {
  let mode = 'valid';
  const bridge = createBridge(async () => {
    if (mode === 'network') throw Object.assign(new Error('ECONNRESET'), { code: 'ECONNRESET' });
    if (mode === 'expired') return { body: { data: { code: 200, profile: null, account: null } } };
    if (mode === 'unauthorized') throw { body: { code: 301, message: '登录已失效' } };
    return loginBody();
  });
  assert.ok((await bridge('authSetSession', { cookie: 'MUSIC_U=old-token' })).profile);
  assert.deepEqual(await bridge('authSetSession', { cookie: 'bad input' }), { profile: null });
  assert.deepEqual(await bridge('authStatus'), { profile: null });
  for (mode of ['expired', 'unauthorized']) {
    assert.deepEqual(await bridge('authSetSession', { cookie: 'MUSIC_U=expired-token' }), { profile: null });
    await assert.rejects(bridge('userPlaylists', { uid: 7 }), { code: 'LOGIN_REQUIRED' });
  }
  mode = 'valid';
  await bridge('authSetSession', { cookie: 'MUSIC_U=old-token' });
  mode = 'network';
  await assert.rejects(bridge('authSetSession', { cookie: 'MUSIC_U=new-token' }));
  assert.deepEqual(await bridge('authStatus'), { profile: null });
});

test('a late account validation cannot revive a logged-out or replaced session', async () => {
  let complete;
  const bridge = createBridge(async () => new Promise(resolve => { complete = resolve; }));
  const pending = bridge('authSetSession', { cookie: 'MUSIC_U=old-token' });
  await bridge('authLogout');
  complete(loginBody());
  assert.deepEqual(await pending, { profile: null });
  assert.deepEqual(await bridge('authStatus'), { profile: null });
});

test('canceled login rollback via authResume wins over a late validation response', async () => {
  let complete;
  const bridge = createBridge(async method => {
    if (method === 'login_status') return new Promise(resolve => { complete = resolve; });
    throw new Error('Unexpected request');
  });
  const pending = bridge('authSetSession', { cookie: 'MUSIC_U=canceled-token' });
  await bridge('authResume', { cookie: '' });
  complete(loginBody());
  assert.deepEqual(await pending, { profile: null });
  assert.deepEqual(await bridge('authStatus'), { profile: null });
});

test('native host can synchronously bootstrap its verified session after a Node restart', async () => {
  const bridge = createBridge(async (method, params) => {
    assert.equal(method, 'user_playlist');
    assert.equal(params.cookie, 'MUSIC_U=retained-token');
    return { body: { code: 200, more: false, playlist: [] } };
  });
  const resume = bridge('authResume', { cookie: 'MUSIC_U=retained-token' });
  const playlists = bridge('userPlaylists', { uid: 7 });
  assert.deepEqual(await resume, {});
  assert.equal((await playlists).more, false);
});

test('upstream errors redact cookie values and authenticated expiry clears the session', async () => {
  const bridge = createBridge(async method => {
    if (method === 'login_status') return loginBody();
    throw { body: { code: 301, message: 'Invalid MUSIC_U=never-print-this-token' } };
  });
  await bridge('authSetSession', { cookie: 'MUSIC_U=never-print-this-token' });
  await assert.rejects(bridge('search', { keywords: 'x' }), error => error.code === '301' && !error.message.includes('never-print-this-token'));
  assert.deepEqual(await bridge('authStatus'), { profile: null });
  assert.equal(normalizeCookie('MUSIC_U=x\r\nInjected: header'), '');
});

test('requested audio quality preserves official downgrade and preview, then falls back to legacy only if needed', async () => {
  const downgraded = createBridge(async (method, params) => {
    assert.equal(method, 'song_url_v1');
    assert.equal(params.level, 'hires');
    return { body: { code: 200, data: [{ id: 1, url: 'https://m.music.126.net/a.mp3', level: 'exhigh', freeTrialInfo: { start: 40, end: 70 } }] } };
  });
  const preview = await downgraded('stream', { id: 1, quality: 'hires' });
  assert.equal(preview.requestedQuality, 'hires');
  assert.equal(preview.quality, 'exhigh');
  assert.equal(preview.fellBack, true);
  assert.equal(preview.startOffsetMs, 40000);
  assert.equal(preview.isPreview, true);
  const calls = [];
  const legacy = createBridge(async method => {
    calls.push(method);
    if (method === 'song_url_v1') throw new Error('Endpoint unavailable');
    return { body: { code: 200, data: [{ id: 1, url: 'https://m.music.126.net/a.mp3', br: 320000 }] } };
  });
  assert.equal((await legacy('stream', { id: 1, quality: 'lossless' })).quality, 'exhigh');
  assert.deepEqual(calls, ['song_url_v1', 'song_url']);
});

test('YRC translations and romanization use their own timestamps and never invent missing content', async () => {
  const bridge = createBridge(async () => ({ body: {
    code: 200,
    yrc: { lyric: '[1000,500](1000,500,0)君\n[4000,500](4000,500,0)声\n[8000,500](8000,500,0)夢' },
    ytlrc: { lyric: '[00:01]你\n[00:04]声音' },
    yromalrc: { lyric: '[00:01.050]kimi\n[00:04]koe' },
    tlyric: { lyric: '[00:01]wrong legacy timeline' },
  } }));
  const { lines } = await bridge('lyrics', { id: 1 });
  assert.equal(lines[0].translation, '你');
  assert.equal(lines[0].romanization, 'kimi');
  assert.equal(lines[1].translation, '声音');
  assert.equal(lines[2].translation, undefined);
  assert.equal(lines[2].romanization, undefined);
  const collision = addLyricCompanions([{ timeMs: 1000, text: 'credit' }, { timeMs: 1500, text: 'sung' }], { tlyric: { lyric: '[00:01.500]exact' } });
  assert.equal(collision[0].translation, undefined);
  assert.equal(collision[1].translation, 'exact');
});
