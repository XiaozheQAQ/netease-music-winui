'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createBridge } = require('../bridge.cjs');

const now = 1800000000000;
const cookie = 'MUSIC_U=membership-fixture';
const login = (vipType = 11, vipRights) => ({ body: { code: 200, data: {
  account: { id: 7 }, profile: { userId: 7, nickname: '会员测试', vipType, vipRights }
} } });

async function profileFor(details, vipType = 11, vipRights) {
  const bridge = createBridge(async (method, params) => {
    assert.equal(params.cookie, cookie);
    if (method === 'login_status') return login(vipType, vipRights);
    assert.equal(method, 'vip_info_v2');
    assert.equal(params.uid, 7);
    assert.equal(params.timeout, 1200);
    return { body: { code: 200, data: { userId: 7, now, ...details } } };
  });
  return (await bridge('authSetSession', { cookie })).profile;
}

test('membership uses active packages with SVIP > black VIP > music package precedence', async () => {
  const active = { expireTime: now + 10000 };
  for (const [details, expected] of [
    [{ redplus: active, associator: active, musicPackage: active }, 'black-svip'],
    [{ redplus: null, associator: active, musicPackage: active }, 'black-vip'],
    [{ redplus: null, associator: null, musicPackage: active }, 'music-package'],
    [{ redplus: null, associator: null, musicPackage: null }, 'standard']
  ]) {
    const profile = await profileFor(details);
    assert.equal(profile.membership, expected);
    assert.equal(profile.membershipExpiresAt, expected === 'standard' ? 0 : now + 10000);
  }
});

test('historical VIP codes, growth level, expired and denied entitlements cannot confer membership', async () => {
  const profile = await profileFor({
    redVipLevel: 7,
    redplus: { vipCode: 300, expireTime: now - 1, rights: true },
    associator: { vipCode: 100, expireTime: now },
    musicPackage: { vipCode: 230, expireTime: now + 10000, rights: false }
  });
  assert.equal(profile.membership, 'standard');
  assert.equal((await profileFor({ associator: { vipCode: 100 } })).membership, 'standard');
});

test('membership uses server time and rejects a foreign user response', async () => {
  assert.equal((await profileFor({ now: 1000, associator: { expireTime: 1500 } })).membership, 'black-vip');
  const foreign = await profileFor({ userId: 8, redplus: { expireTime: now + 1000 } });
  assert.equal(foreign.membership, undefined);
  assert.equal(foreign.vipType, 11);
});

test('login profile rights remain available if optional membership endpoint is unavailable', async () => {
  const bridge = createBridge(async method => {
    if (method === 'login_status') return login(11, { redplus: { vipCode: 300, rights: true } });
    throw Object.assign(new Error('Temporary outage'), { code: 'ETIMEDOUT' });
  });
  assert.equal((await bridge('authSetSession', { cookie })).profile.membership, 'black-svip');
});

test('optional membership failure preserves verified basic tier without guessing an upgrade', async () => {
  for (const vipType of [0, 10, 11, 99]) {
    const bridge = createBridge(async method => {
      if (method === 'login_status') return login(vipType);
      throw Object.assign(new Error('Temporary outage'), { code: 'ETIMEDOUT' });
    });
    const profile = (await bridge('authSetSession', { cookie })).profile;
    assert.equal(profile.vipType, vipType);
    assert.equal(profile.membership, undefined);
  }
});

test('authentication expiry from the membership endpoint invalidates the session', async () => {
  const bridge = createBridge(async method => method === 'login_status'
    ? login() : { body: { code: 301, message: '登录已过期' } });
  assert.deepEqual(await bridge('authSetSession', { cookie }), { profile: null });
  assert.deepEqual(await bridge('authStatus'), { profile: null });
});

test('logout and canceled-login rollback discard late membership responses', async () => {
  for (const action of ['authLogout', 'authResume']) {
    let resolve;
    let started;
    const ready = new Promise(done => { started = done; });
    const bridge = createBridge(async method => {
      if (method === 'login_status') return login();
      assert.equal(method, 'vip_info_v2');
      started();
      return new Promise(done => { resolve = done; });
    });
    const pending = bridge('authSetSession', { cookie });
    await ready;
    await bridge(action, { cookie: '' });
    resolve({ body: { code: 200, data: { userId: 7, now, redplus: { expireTime: now + 10000 } } } });
    assert.deepEqual(await pending, { profile: null });
    assert.deepEqual(await bridge('authStatus'), { profile: null });
  }
});

test('normal music requests do not repeatedly request membership', async () => {
  let membershipRequests = 0;
  const bridge = createBridge(async method => {
    if (method === 'login_status') return login();
    if (method === 'vip_info_v2') { membershipRequests++; return { body: { code: 200, data: { userId: 7, associator: null } } }; }
    assert.equal(method, 'cloudsearch');
    return { body: { code: 200, result: { songs: [] } } };
  });
  await bridge('authSetSession', { cookie });
  for (let index = 0; index < 4; index++) await bridge('search', { keywords: '音乐' });
  assert.equal(membershipRequests, 1);
});
