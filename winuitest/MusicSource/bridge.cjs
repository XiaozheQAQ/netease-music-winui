'use strict';

const REQUEST_TIMEOUT_MS = 22000;
const QUALITIES = ['standard', 'higher', 'exhigh', 'lossless', 'hires'];
const COOKIE_ATTRIBUTES = new Set(['path', 'domain', 'expires', 'max-age', 'samesite', 'secure', 'httponly', 'priority']);

function normalizeCookie(value) {
  if (typeof value !== 'string' || value.length > 16384 || /[\r\n]/.test(value)) return '';
  const entries = new Map();
  for (const part of value.split(';')) {
    const index = part.indexOf('=');
    if (index <= 0) continue;
    const name = part.slice(0, index).trim();
    const content = part.slice(index + 1).trim();
    if (!COOKIE_ATTRIBUTES.has(name.toLowerCase()) && /^[\w-]+$/.test(name) && content) entries.set(name, content);
  }
  return [...entries].map(([key, content]) => `${key}=${content}`).join('; ');
}

class SourceError extends Error {
  constructor(message, code = 'SOURCE_ERROR') {
    super(message);
    this.code = code;
  }
}

function positiveId(value) {
  const id = Number(value);
  if (!Number.isSafeInteger(id) || id <= 0) throw new SourceError('无效的音乐 ID。', 'INVALID_ARGUMENT');
  return id;
}

function artwork(value) {
  return typeof value === 'string' ? value.replace(/^http:/, 'https:') : '';
}

function friendlyError(message, code = '') {
  const detail = `${code} ${typeof message === 'string' ? message : ''}`;
  if (/ETIMEDOUT|ESOCKETTIMEDOUT|ECONNABORTED|timeout|timed out/i.test(detail)) {
    return '网易云音乐响应超时，请稍后重试。';
  }
  if (/ENOTFOUND|EAI_AGAIN|getaddrinfo|ECONNREFUSED|ECONNRESET|ENETUNREACH|EHOSTUNREACH|ERR_NETWORK|socket hang up|network error|CERT_|certificate|TLS/i.test(detail)) {
    return '无法连接网易云音乐，请检查网络后重试。';
  }
  return typeof message === 'string' && message ? message : '暂时无法连接网易云音乐，请检查网络后重试。';
}

function feeOf(value) {
  // SDK 4.41.1 /song/detail: 0 free/no copyright; 1 VIP; 4 purchased
  // album; 8 free standard audio with member-only higher quality/downloads.
  // Missing/unknown values must not turn into fee=0 through Number(null).
  if (typeof value !== 'number' && !(typeof value === 'string' && /^[0148]$/.test(value))) return null;
  const fee = Number(value);
  return [0, 1, 4, 8].includes(fee) ? fee : null;
}

function trackOf(value, matchingPrivilege) {
  const song = value.song || value;
  const album = song.al || song.album || value.album || {};
  const artists = song.ar || song.artists || value.artists || [];
  const privilege = { ...value.privilege, ...song.privilege, ...matchingPrivilege };
  return {
    id: Number(song.id || value.id),
    title: song.name || value.name || '未命名歌曲',
    artist: artists.map(item => item.name).filter(Boolean).join(' / ') || '未知歌手',
    artists: artistReferences(artists),
    album: album.name || '未知专辑',
    albumId: Number.isSafeInteger(Number(album.id)) && Number(album.id) > 0 ? Number(album.id) : 0,
    artworkUrl: artwork(album.picUrl || value.picUrl),
    durationMs: Math.trunc(Number(song.dt || song.duration || value.duration || 0)),
    fee: feeOf(song.fee) ?? feeOf(privilege.fee) ?? feeOf(value.fee),
    isCloudTrack: song.t === 1 || song.t === 2 || song.pc != null || privilege.cs === true,
    isUnavailable: Number(privilege.st) < 0 || privilege.toast === true,
  };
}

function tracksOf(values = [], privileges = []) {
  const byId = new Map((Array.isArray(privileges) ? privileges : []).map(item => [Number(item.id), item]));
  return values.map(value => trackOf(value, byId.get(Number((value.song || value).id))));
}

function artistReferences(values = []) {
  const unique = new Map();
  for (const value of Array.isArray(values) ? values : []) {
    const id = Number(value?.id);
    if (Number.isSafeInteger(id) && id > 0 && typeof value.name === 'string' && value.name.trim())
      unique.set(id, { id, name: value.name.trim() });
  }
  return [...unique.values()];
}

function albumOf(value) {
  return {
    id: Number(value.id), title: value.name || '未命名专辑',
    artist: (value.artists || [value.artist]).filter(Boolean).map(item => item.name).filter(Boolean).join(' / '),
    artworkUrl: artwork(value.picUrl || value.blurPicUrl),
    publishTime: Number.isSafeInteger(Number(value.publishTime)) ? Number(value.publishTime) : 0,
    trackCount: Math.max(0, Math.trunc(Number(value.size || value.songCount || 0))),
  };
}

function pageArguments(params, defaultLimit) {
  const offset = params.offset === undefined ? 0 : Number(params.offset);
  const limit = params.limit === undefined ? defaultLimit : Number(params.limit);
  if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(limit) || limit < 1)
    throw new SourceError('分页参数无效。', 'INVALID_ARGUMENT');
  return { offset, limit: Math.min(100, limit) };
}

function playlistOf(value) {
  const creatorId = Number(value.creator?.userId || value.userId || 0);
  return {
    id: Number(value.id),
    creatorId: Number.isSafeInteger(creatorId) && creatorId > 0 ? creatorId : 0,
    title: value.name || '未命名歌单',
    subtitle: value.copywriter || value.description || '网易云音乐精选歌单',
    artworkUrl: artwork(value.picUrl || value.coverImgUrl),
    trackCount: Math.trunc(Number(value.trackCount || 0)),
    playCount: Math.trunc(Number(value.playCount || 0)),
  };
}

function parseLyrics(lrc = '') {
  if (typeof lrc !== 'string') return [];
  let offset = 0;
  const offsetMatch = lrc.match(/\[offset:([+-]?\d+)\]/i);
  if (offsetMatch) offset = Number(offsetMatch[1]);
  const result = [];
  for (const line of lrc.split(/\r?\n/)) {
    const text = line.replace(/\[[^\]]*\]/g, '').trim();
    for (const match of line.matchAll(/\[(\d+):(\d{1,2})(?:[.:](\d{1,3}))?\]/g)) {
      const fraction = match[3] ? Number(match[3].padEnd(3, '0')) : 0;
      const timeMs = Math.max(0, Number(match[1]) * 60000 + Number(match[2]) * 1000 + fraction + offset);
      if (Number.isSafeInteger(timeMs)) result.push({ timeMs, text });
    }
  }
  return preserveExplicitGaps(result);
}

function preserveExplicitGaps(lines) {
  const result = [];
  let gapStart;
  for (const line of lines.sort((a, b) => a.timeMs - b.timeMs)) {
    if (!line.text.trim()) { gapStart ??= line.timeMs; continue; }
    if (gapStart !== undefined && gapStart < line.timeMs) line.gapStartMs = gapStart;
    gapStart = undefined;
    result.push(line);
  }
  return result;
}

function parseYrc(yrc = '') {
  if (typeof yrc !== 'string') return [];
  const result = [];
  for (const rawLine of yrc.split(/\r?\n/)) {
    // JSON credits/metadata and malformed headers are not sung lyric lines.
    const header = rawLine.match(/^\[(\d+),(\d+)\](.*)$/);
    if (!header) continue;
    const timeMs = Number(header[1]);
    const durationMs = Number(header[2]);
    if (![timeMs, durationMs, timeMs + durationMs].every(Number.isSafeInteger)) continue;
    const content = header[3];
    const markers = [...content.matchAll(/\((\d+),(\d+),(-?\d+)\)/g)];
    const text = content.replace(/\((\d+),(\d+),(-?\d+)\)/g, '');
    const prefix = content.slice(0, markers[0]?.index ?? content.length);
    let words = [];
    // Untimed text is kept as a normal line; never divide its duration into invented words.
    if (markers.length && !prefix.trim()) {
      words = markers.map((marker, index) => ({
        startMs: Number(marker[1]),
        durationMs: Number(marker[2]),
        text: (index === 0 ? prefix : '') + content.slice(marker.index + marker[0].length, markers[index + 1]?.index ?? content.length),
      }));
      if (!words.every(word => Number.isSafeInteger(word.startMs) && Number.isSafeInteger(word.durationMs) &&
        Number.isSafeInteger(word.startMs + word.durationMs))) words = [];
      else words = words.filter(word => word.text.length > 0);
    }
    result.push({ timeMs, durationMs, text, words });
  }
  return preserveExplicitGaps(result);
}

function addLyricCompanions(lines, body, useYrc = false) {
  // The y-prefixed fields follow the YRC timeline. Legacy translations can differ
  // slightly, so use a bounded nearest match, never align unrelated lines by index.
  for (const [property, preferred, fallback] of [
    ['translation', 'ytlrc', 'tlyric'], ['romanization', 'yromalrc', 'romalrc'],
  ]) {
    const primary = useYrc ? parseLyrics(body[preferred]?.lyric) : [];
    const companion = primary.length ? primary : parseLyrics(body[fallback]?.lyric);
    const candidates = [];
    for (let i = 0; i < lines.length; i++) for (let j = 0; j < companion.length; j++) {
      const distance = Math.abs(companion[j].timeMs - lines[i].timeMs);
      if (distance <= 1200) candidates.push({ i, j, distance });
      if (companion[j].timeMs > lines[i].timeMs + 1200) break;
    }
    // Exact timestamp matches win before nearby pairs; an early metadata line
    // must not steal the translation intended for the next sung line.
    candidates.sort((a, b) => a.distance - b.distance);
    const usedLines = new Set();
    const usedCompanions = new Set();
    for (const { i, j } of candidates) {
      if (usedLines.has(i) || usedCompanions.has(j)) continue;
      lines[i][property] = companion[j].text;
      usedLines.add(i);
      usedCompanions.add(j);
    }
  }
  return lines;
}

function membershipOf(value, now = Date.now()) {
  if (!value || typeof value !== 'object' || !['redplus', 'associator', 'musicPackage'].some(key => Object.hasOwn(value, key))) return null;
  const serverNow = Number(value.now);
  if (Number.isFinite(serverNow) && serverNow > 0) now = serverNow;
  // These are current entitlements, not historical VIP level/growth points.
  // An expired package keeps its vipCode, so code/level alone proves nothing.
  for (const [key, membership] of [['redplus', 'black-svip'], ['associator', 'black-vip'], ['musicPackage', 'music-package']]) {
    const plan = value[key];
    if (!plan || typeof plan !== 'object' || plan.rights === false) continue;
    const expiry = Number(plan.expireTime);
    const active = Object.hasOwn(plan, 'expireTime') ? Number.isFinite(expiry) && expiry > now : plan.rights === true;
    if (active) return { membership, membershipExpiresAt: Number.isFinite(expiry) && expiry > 0 ? expiry : 0 };
  }
  return { membership: 'standard', membershipExpiresAt: 0 };
}

function profileOf(body) {
  const data = body?.data || body;
  const value = data?.profile;
  const id = Number(value?.userId || value?.uid || data?.account?.id);
  if (!value || !Number.isSafeInteger(id) || id <= 0) return null;
  return { id, nickname: value.nickname || '网易云用户', avatarUrl: artwork(value.avatarUrl), vipType: Number(value.vipType || 0),
    ...(membershipOf(value.vipRights) || {}) };
}

function createBridge(api, options = {}) {
  let sessionCookie = normalizeCookie(options.initialCookie || '');
  let sessionVersion = 0;
  function redact(message, cookie = sessionCookie) {
    let result = typeof message === 'string' ? message : '';
    for (const part of cookie.split(';')) {
      const value = part.slice(part.indexOf('=') + 1).trim();
      if (value.length >= 8) result = result.split(value).join('[redacted]');
    }
    return result.replace(/(MUSIC_U|MUSIC_A|__csrf)\s*[=:]\s*[^\s;,]+/gi, '$1=[redacted]');
  }
  async function invoke(name, params = {}, settings = {}) {
    let response;
    const cookie = settings.cookie ?? sessionCookie;
    const version = sessionVersion;
    try {
      // {} is intentional: SDK createOption otherwise falls back to NETEASE_COOKIE.
      response = await api(name, { ...params, cookie: cookie || {}, timeout: settings.timeout ?? REQUEST_TIMEOUT_MS });
    } catch (error) {
      const code = String(error?.body?.code || error?.code || 'SOURCE_ERROR');
      if (['301', '401'].includes(code) && !settings.ignoreAuthFailure && version === sessionVersion) { sessionCookie = ''; sessionVersion++; }
      const message = redact(error?.body?.message || error?.body?.msg || error?.message, cookie);
      throw new SourceError(friendlyError(message, code), code);
    }
    const body = response?.body || response;
    const code = Number(body?.code ?? body?.data?.code ?? 200);
    if (!body || !(settings.acceptCodes || [200]).includes(code)) {
      if ([301, 401].includes(code) && !settings.ignoreAuthFailure && version === sessionVersion) { sessionCookie = ''; sessionVersion++; }
      throw new SourceError(friendlyError(redact(body?.message || body?.msg || '音源返回了异常响应。', cookie)), String(code || 'SOURCE_ERROR'));
    }
    return body;
  }

  async function accountStatus(cookie, version, includeMembership = false) {
    try {
      const body = await invoke('login_status', {}, { cookie, ignoreAuthFailure: true });
      const profile = profileOf(body);
      if (!profile && sessionVersion === version) { sessionCookie = ''; sessionVersion++; }
      if (sessionVersion !== version) return { profile: null };
      if (profile && includeMembership) {
        try {
          // Only login/session verification asks for VIP details. Never do this
          // during playback ticks or the ownership check for playlist mutations.
          const details = await invoke('vip_info_v2', { uid: profile.id }, { cookie, ignoreAuthFailure: true, timeout: 1200 });
          const data = details.data;
          if (data && (data.userId == null || Number(data.userId) === profile.id))
            Object.assign(profile, membershipOf(data) || {});
        } catch (error) {
          if (['301', '401'].includes(error.code)) throw error;
          // A temporary VIP endpoint outage must not reject a verified login.
          // Retain only the tier supported by login_status in that case.
        }
        if (sessionVersion !== version) return { profile: null };
      }
      return { profile };
    } catch (error) {
      if (!['301', '401'].includes(error.code)) throw error;
      if (sessionVersion === version) { sessionCookie = ''; sessionVersion++; }
      return { profile: null };
    }
  }

  return async function dispatch(method, params = {}) {
    if (!params || typeof params !== 'object' || Array.isArray(params)) throw new SourceError('请求参数无效。', 'INVALID_ARGUMENT');
    switch (method) {
      case 'health':
        return { provider: '网易云音乐', apiVersion: '4.41.1', nodeVersion: process.version, transport: 'stdio' };
      case 'authQrCreate': {
        const keyResponse = await invoke('login_qr_key', { timestamp: Date.now() }, { cookie: '', ignoreAuthFailure: true });
        const key = keyResponse.data?.unikey || keyResponse.unikey;
        if (typeof key !== 'string' || !key) throw new SourceError('暂时无法生成登录二维码，请重试。');
        const image = await invoke('login_qr_create', { key, qrimg: true, platform: 'pc', timestamp: Date.now() }, { cookie: '', ignoreAuthFailure: true });
        const qrImage = image.data?.qrimg;
        const qrUrl = image.data?.qrurl;
        if (typeof qrImage !== 'string' || !qrImage.startsWith('data:image/png;base64,') || typeof qrUrl !== 'string') throw new SourceError('登录二维码无效，请重试。');
        return { key, qrImage, qrUrl };
      }
      case 'authQrCheck': {
        const key = typeof params.key === 'string' ? params.key.trim() : '';
        if (!key || key.length > 256) throw new SourceError('登录二维码无效，请刷新。', 'INVALID_ARGUMENT');
        const body = await invoke('login_qr_check', { key, timestamp: Date.now(), noCookie: true }, { cookie: '', ignoreAuthFailure: true, acceptCodes: [800, 801, 802, 803] });
        const code = Number(body.code);
        const messages = { 800: '二维码已过期', 801: '使用网易云音乐扫码', 802: '请在手机上确认登录', 803: '登录成功' };
        if (code === 803) {
          const cookie = normalizeCookie(body.cookie);
          if (!cookie || !/(?:^|;\s*)MUSIC_U=/.test(cookie)) throw new SourceError('登录凭据无效，请刷新二维码重试。');
          return { code, message: messages[code], cookie };
        }
        return { code, message: messages[code] };
      }
      case 'authSetSession': {
        // Switching account first invalidates the old session, including on bad input.
        sessionCookie = '';
        const version = ++sessionVersion;
        const cookie = normalizeCookie(params.cookie);
        if (!cookie || !/(?:^|;\s*)MUSIC_U=/.test(cookie)) return { profile: null };
        const result = await accountStatus(cookie, version, true);
        if (sessionVersion !== version) return { profile: null };
        if (result.profile) sessionCookie = cookie;
        return result;
      }
      case 'authResume': {
        // Private parent-process bootstrap after a child restart. The native host
        // supplies only its previously verified credential; no network round trip
        // may delay the immediately following request on this ordered stdio pipe.
        const cookie = normalizeCookie(params.cookie);
        sessionCookie = /(?:^|;\s*)MUSIC_U=/.test(cookie) ? cookie : '';
        sessionVersion++;
        return {};
      }
      case 'authStatus': {
        if (!sessionCookie) return { profile: null };
        const version = sessionVersion;
        const result = await accountStatus(sessionCookie, version, true);
        return version === sessionVersion ? result : { profile: null };
      }
      case 'authLogout': {
        const cookie = sessionCookie;
        sessionCookie = '';
        sessionVersion++;
        if (cookie) { try { await invoke('logout', {}, { cookie, ignoreAuthFailure: true }); } catch { /* Local logout always completes. */ } }
        return {};
      }
      case 'userPlaylists': {
        if (!sessionCookie) throw new SourceError('请先登录网易云音乐。', 'LOGIN_REQUIRED');
        const uid = positiveId(params.uid);
        const limit = Math.trunc(Math.min(100, Math.max(1, Number(params.limit) || 50)));
        const offset = Math.trunc(Math.max(0, Number(params.offset) || 0));
        const body = await invoke('user_playlist', { uid, offset, limit });
        return { playlists: (body.playlist || []).map(playlistOf), more: body.more === true };
      }
      case 'playlistTracks': {
        if (!sessionCookie) throw new SourceError('请先登录网易云音乐。', 'LOGIN_REQUIRED');
        const id = positiveId(params.id);
        if (!['add', 'del'].includes(params.operation) || !Array.isArray(params.trackIds) ||
            params.trackIds.length < 1 || params.trackIds.length > 500 ||
            params.trackIds.some(value => !Number.isSafeInteger(value) || value <= 0))
          throw new SourceError('请选择 1–500 首有效歌曲。', 'INVALID_ARGUMENT');
        const ids = [...new Set(params.trackIds.map(positiveId))];
        const version = sessionVersion;
        const cookie = sessionCookie;
        const ensureSession = () => {
          if (version !== sessionVersion || cookie !== sessionCookie)
            throw new SourceError('登录账号已改变，请刷新歌单确认结果。', 'SESSION_CHANGED');
        };
        // Resolve the current owner again rather than trusting a stale sidebar or
        // a caller-provided user ID. No write happens before both checks finish.
        const account = await accountStatus(cookie, version);
        ensureSession();
        if (!account.profile) throw new SourceError('登录已过期，请重新登录。', 'LOGIN_REQUIRED');
        const detail = await invoke('playlist_detail', { id }, { cookie });
        ensureSession();
        if (!detail.playlist || playlistOf(detail.playlist).creatorId !== account.profile.id)
          throw new SourceError('只能修改自己创建的歌单。', 'PLAYLIST_READ_ONLY');
        const result = await invoke('playlist_tracks', { op: params.operation, pid: id, tracks: ids.join(',') }, { cookie });
        ensureSession();
        // A write needs an explicit acknowledgement. The generic reader also
        // supports endpoints without a code field; that is unsafe for mutations.
        if (Number(result?.code) !== 200)
          throw new SourceError('歌单操作未得到确认，请刷新歌单检查结果。', 'MUTATION_UNCONFIRMED');
        return { count: ids.length };
      }
      case 'discover': {
        const results = await Promise.allSettled([
          invoke('personalized', { limit: 12 }),
          invoke('personalized_newsong', { limit: 12 }),
        ]);
        if (results.every(result => result.status === 'rejected')) throw results[0].reason;
        return {
          playlists: results[0].status === 'fulfilled' ? (results[0].value.result || []).map(playlistOf) : [],
          newSongs: results[1].status === 'fulfilled' ? tracksOf(results[1].value.result || [], results[1].value.privileges) : [],
        };
      }
      case 'search': {
        const keywords = String(params.keywords || '').trim();
        if (!keywords || keywords.length > 200) throw new SourceError('请输入 1–200 个字符的搜索内容。', 'INVALID_ARGUMENT');
        const limit = Math.min(100, Math.max(1, Number(params.limit) || 30));
        const offset = Math.max(0, Number(params.offset) || 0);
        const body = await invoke('cloudsearch', { keywords, limit, offset, type: 1 });
        return { tracks: tracksOf(body.result?.songs || [], body.result?.privileges || body.privileges), total: Number(body.result?.songCount || 0) };
      }
      case 'playlist': {
        const id = positiveId(params.id);
        const body = await invoke('playlist_detail', { id });
        const playlist = body.playlist;
        if (!playlist) throw new SourceError('这个歌单暂时无法访问。');
        let tracks = playlist.tracks || [];
        let privileges = body.privileges || playlist.privileges || [];
        // Keep the complete lightweight ordering, but resolve only the initial
        // viewport. Subsequent pages (and direct jumps) request their own IDs.
        const ids = [...new Set((playlist.trackIds?.length ? playlist.trackIds : tracks)
          .map(item => Number(item.id)).filter(id => Number.isSafeInteger(id) && id > 0))];
        if (ids.length > 20000) throw new SourceError('这个歌单超过 20000 首，暂时无法完整加载。', 'PLAYLIST_TOO_LARGE');
        const initialIds = ids.slice(0, 60);
        const byId = new Map(tracks.map(song => [Number(song.id), song]));
        if (initialIds.some(id => !byId.has(id))) {
          const response = await invoke('song_detail', { ids: initialIds.join(',') });
          for (const song of response.songs || []) byId.set(Number(song.id), song);
          privileges = [...privileges, ...(response.privileges || [])];
        }
        tracks = initialIds.map(id => byId.get(id)).filter(Boolean);
        return { ...playlistOf(playlist), trackIds: ids, tracks: tracksOf(tracks, privileges) };
      }
      case 'playlistPage': {
        if (!Array.isArray(params.trackIds) || params.trackIds.length < 1 || params.trackIds.length > 500)
          throw new SourceError('请选择 1–500 首有效歌曲。', 'INVALID_ARGUMENT');
        const ids = [...new Set(params.trackIds.map(positiveId))];
        const response = await invoke('song_detail', { ids: ids.join(',') });
        const byId = new Map(tracksOf(response.songs || [], response.privileges).map(track => [track.id, track]));
        return ids.map(id => byId.get(id)).filter(Boolean);
      }
      case 'trackMetadata': {
        const id = positiveId(params.id);
        const body = await invoke('song_detail', { ids: String(id) });
        const song = (body.songs || []).find(value => Number(value.id) === id);
        if (!song) throw new SourceError('暂时无法获取这首歌曲的信息。');
        return tracksOf([song], body.privileges)[0];
      }
      case 'trackArtists': {
        const id = positiveId(params.id);
        const body = await invoke('song_detail', { ids: String(id) });
        // Old saved tracks have only display names. Resolve the original song
        // ID; never guess which same-named artist owns it through text search.
        const song = (body.songs || []).find(value => Number(value.id) === id);
        return song ? artistReferences(song.ar || song.artists) : [];
      }
      case 'artist': {
        const id = positiveId(params.id);
        const body = await invoke('artists', { id });
        const artist = body.artist;
        if (!artist || Number(artist.id) !== id) throw new SourceError('暂时无法获取这位歌手的信息。');
        return { id, name: artist.name || '未知歌手', artworkUrl: artwork(artist.picUrl || artist.img1v1Url),
          description: artist.briefDesc || '', tracks: tracksOf(body.hotSongs || [], body.privileges) };
      }
      case 'artistSongs': {
        const id = positiveId(params.id);
        const { offset, limit } = pageArguments(params, 50);
        const body = await invoke('artist_songs', { id, offset, limit, order: 'time' });
        if (!Array.isArray(body.songs)) throw new SourceError('暂时无法获取歌手的单曲。');
        const total = Math.max(0, Math.trunc(Number(body.total || 0)));
        const more = body.more === true || body.more === undefined && (total > 0 ? offset + body.songs.length < total : body.songs.length === limit);
        return { tracks: tracksOf(body.songs, body.privileges), total, more: body.songs.length > 0 && more };
      }
      case 'artistAlbums': {
        const id = positiveId(params.id);
        const { offset, limit } = pageArguments(params, 30);
        const body = await invoke('artist_album', { id, offset, limit });
        if (!Array.isArray(body.hotAlbums)) throw new SourceError('暂时无法获取歌手的专辑。');
        const albums = body.hotAlbums.map(albumOf).filter(album => Number.isSafeInteger(album.id) && album.id > 0);
        return { albums, more: body.hotAlbums.length > 0 && (body.more === true || body.more === undefined && body.hotAlbums.length === limit) };
      }
      case 'album': {
        const id = positiveId(params.id);
        const body = await invoke('album', { id });
        if (!body.album || Number(body.album.id) !== id || !Array.isArray(body.songs))
          throw new SourceError('这个专辑暂时无法访问。');
        // Some album tracks omit artwork/album fields; use their parent metadata.
        return { ...albumOf(body.album), tracks: tracksOf(body.songs.map(song => ({ ...song, al: { ...body.album, ...song.al } })), body.privileges) };
      }
      case 'stream': {
        const id = positiveId(params.id);
        const requestedQuality = QUALITIES.includes(params.quality) ? params.quality : 'exhigh';
        // Only official entitlement responses are used. Upstream may supply a lower
        // quality or a preview; those are preserved instead of claiming VIP access.
        let stream;
        let v1Failed = false;
        try {
          const body = await invoke('song_url_v1', { id, level: requestedQuality, unblock: 'false' });
          stream = body.data?.find(item => Number(item.id) === id) || body.data?.[0];
        } catch { v1Failed = true; }
        if (!stream?.url) {
          const body = await invoke('song_url', { id, br: 320000 });
          stream = body.data?.find(item => Number(item.id) === id) || body.data?.[0];
          v1Failed = true;
        }
        if (!stream?.url) throw new SourceError('此歌曲暂不可播放，可能需要网易云账号权限或受地区限制。', 'UNAVAILABLE');
        const url = new URL(stream.url);
        if (!['https:', 'http:'].includes(url.protocol)) throw new SourceError('音源返回了无效的播放地址。');
        const trial = stream.freeTrialInfo;
        const isPreview = trial !== null && typeof trial === 'object' && !Array.isArray(trial);
        // NetEase freeTrialInfo start/end are seconds; `time` and lyric timestamps are ms.
        // Preserve the upstream fragment offset rather than guessing where a preview begins.
        const start = Number(trial?.start);
        const end = Number(trial?.end);
        const startOffsetMs = isPreview && Number.isFinite(start) && start >= 0 && end > start &&
          Number.isSafeInteger(Math.round(start * 1000)) ? Math.round(start * 1000) : 0;
        const quality = typeof stream.level === 'string' && stream.level ? stream.level :
          String(stream.type).toLowerCase() === 'flac' ? 'lossless' : Number(stream.br) >= 320000 ? 'exhigh' : Number(stream.br) >= 192000 ? 'higher' : Number(stream.br) > 0 ? 'standard' : '';
        return { url: url.href, isPreview, startOffsetMs, quality, requestedQuality, fellBack: v1Failed || !!quality && quality !== requestedQuality };
      }
      case 'lyrics': {
        const id = positiveId(params.id);
        let enhanced;
        try { enhanced = await invoke('lyric_new', { id }); } catch { /* Older upstreams may not expose YRC. */ }
        if (enhanced) {
          const yrc = parseYrc(enhanced.yrc?.lyric);
          if (yrc.some(line => line.words.length > 0)) return { lines: addLyricCompanions(yrc, enhanced, true) };
          const lrc = parseLyrics(enhanced.lrc?.lyric);
          if (lrc.length) return { lines: addLyricCompanions(lrc, enhanced) };
          if (yrc.length) return { lines: addLyricCompanions(yrc, enhanced, true) };
        }
        const body = await invoke('lyric', { id });
        return { lines: addLyricCompanions(parseLyrics(body.lrc?.lyric), body) };
      }
      default:
        throw new SourceError('不支持的音源操作。', 'UNKNOWN_METHOD');
    }
  };
}

module.exports = { createBridge, parseLyrics, parseYrc, addLyricCompanions, normalizeCookie, trackOf, playlistOf, SourceError };
