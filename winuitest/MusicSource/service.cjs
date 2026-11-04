'use strict';

// stdout belongs exclusively to the JSON-lines protocol, including during SDK import.
// Some SDK error logs include Set-Cookie responses. Never forward those logs to the
// native process; the bridge supplies bounded, redacted user-facing errors instead.
const { Console } = require('node:console');
const diagnostics = new Console({ stdout: process.stderr, stderr: process.stderr });
global.console = new Console({ stdout: process.stderr, stderr: process.stderr });
for (const method of ['log', 'info', 'debug', 'warn', 'error', 'dir', 'trace', 'table']) global.console[method] = () => {};
process.env.DOTENV_CONFIG_QUIET = 'true';
process.env.ENABLE_GENERAL_UNBLOCK = 'false';
const initialCookie = process.env.NETEASE_COOKIE || '';
delete process.env.NETEASE_COOKIE;

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const readline = require('node:readline');
const crypto = require('node:crypto');
const { createBridge } = require('./bridge.cjs');

function loadApi() {
  const tokenFile = path.join(os.tmpdir(), 'anonymous_token');
  try { fs.writeFileSync(tokenFile, '', { flag: 'wx' }); } catch (error) { if (error.code !== 'EEXIST') throw error; }
  global.deviceId = crypto.randomBytes(16).toString('hex');
  const root = path.dirname(require.resolve('@neteasecloudmusicapienhanced/api/package.json'));
  // Load only required API modules. No Express, server, or port is created.
  const request = require(path.join(root, 'util/request.js'));
  const createOption = require(path.join(root, 'util/option.js'));
  const names = ['personalized', 'personalized_newsong', 'cloudsearch', 'playlist_detail', 'song_detail', 'song_url', 'song_url_v1', 'lyric', 'lyric_new',
    'login_qr_key', 'login_qr_create', 'login_qr_check', 'login_status', 'logout', 'user_playlist',
    'artists', 'artist_songs', 'artist_album', 'album'];
  const modules = new Map(names.map(name => [name, require(path.join(root, 'module', `${name}.js`))]));
  // Same endpoint/parameters as the SDK's playlist_tracks module, but issue only
  // one mutation. Its 512 workaround repeats track IDs and retries the write;
  // keep an uncertain result visible instead of silently editing twice.
  modules.set('playlist_tracks', params => request('/api/playlist/manipulate/tracks', {
    op: params.op, pid: params.pid,
    trackIds: JSON.stringify(params.tracks.split(',')), imme: 'true',
  }, createOption(params)));
  return (name, params) => {
    if (!modules.has(name)) throw new Error('Unsupported SDK module');
    return modules.get(name)(params, request);
  };
}

let dispatch;
try {
  if (Number(process.versions.node.split('.')[0]) < 22) throw new Error('音源需要 Node.js 22 或更新版本。');
  dispatch = createBridge(loadApi(), { initialCookie });
} catch (error) {
  diagnostics.error('音源初始化失败，请检查 Node.js 版本和音源依赖。');
  process.exitCode = 1;
  process.exit(1);
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
let active = 0;
input.on('line', async line => {
  let id = null;
  try {
    if (line.length > 65536) throw new Error('请求过长。');
    const request = JSON.parse(line);
    id = request.id;
    if (!Number.isSafeInteger(id) || typeof request.method !== 'string') throw new Error('请求格式无效。');
    if (active >= 16) throw new Error('音源正忙，请稍后重试。');
    active++;
    try {
      const result = await dispatch(request.method, request.params);
      process.stdout.write(JSON.stringify({ id, result }) + '\n');
    } finally {
      active--;
    }
  } catch (error) {
    process.stdout.write(JSON.stringify({ id, error: { code: error.code || 'SOURCE_ERROR', message: error.message || '音源请求失败。' } }) + '\n');
  }
});
// The native application's redirected stdin is our lifetime, including a parent crash.
input.on('close', () => process.exit(0));
