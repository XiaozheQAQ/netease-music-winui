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
  if (request.method === 'userPlaylists') return reply({ playlists: [{ id: 456, title: '我的歌单', trackCount: 7 }], more: request.params.offset === 0 });
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
