// Mira's Jellyfin extension against a real Jellyfin, through its own API, without a browser: what Mira for Windows,
// Mac and iPhone ask of it. Installed from its repository (tools/web/setup.mjs), it must be loaded at its version;
// « Ta party » (the host's link, its page and its JSON, a guest opening it, the gateway's closed list, the host's group
// joined, the guest removed, a new link); Tailscale Funnel's guard; the TorLink relay with a fake agent (Mira for
// Windows on the PC of Jellyfin): search in the three categories, a download asked, paused and cancelled, a magnet,
// a non-administrator refused, errors in the language asked; « Le foyer » (a password for the family, its profiles, their PIN, its devices, away from home before Tailscale); the HLS playlists of the iPhone app (/Mira/hls); the
// settings of automatic subtitles; the poster relay of « Identifier… »; the extension's own loops, as they run on a
// server: automatic subtitles from a fake OpenSubtitles (tools/extension/fake-opensubtitles.mjs, web.yml points the
// extension at it) and TheTVDB's order of a series (tools/web/fake-tvdb-client), quickened by MIRA_CHECK_TICK_SECONDS.
// results.txt; exit 1 on failure. Jellyfin's log is read after, by tools/extension/log.mjs.
//   node tools/extension/check.mjs <server> <user> <password> <out folder>
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import { KEY, fakeOpenSubtitles } from './fake-opensubtitles.mjs';

const [server = 'http://127.0.0.1:8096', user = 'mira', password = 'mira-check', out = '.artifacts/extension'] = process.argv.slice(2);
const base = server.replace(/\/$/, '');
fs.mkdirSync(out, { recursive: true });
const results = [];
const check = (name, ok, detail = '') => { results.push(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ' — ' + detail : ''}`); console.log(results.at(-1)); };
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const authorization = (device, token) => `MediaBrowser Client="Mira check", Device="check", DeviceId="${device}", Version="1"${token ? `, Token="${token}"` : ''}`;

// One request: its status, its type and its body (JSON when it is).
async function call(method, path, { token, device = 'check-host', body, headers = {} } = {}) {
  const response = await fetch(`${base}/${path.replace(/^\//, '')}`, {
    method, headers: { Authorization: authorization(device, token), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual',
  });
  const text = await response.text();
  const type = response.headers.get('content-type') ?? '';
  let json = null;
  if (/json/.test(type) && text) { try { json = JSON.parse(text); } catch { json = null; } }
  return { status: response.status, type, text, json };
}
const signIn = async (name, pw, device) => (await call('POST', 'Users/AuthenticateByName', { device, body: { Username: name, Pw: pw } })).json?.AccessToken ?? null;
// Each part runs on its own: one that throws is a failure of its own, the next ones still run.
async function part(name, run) {
  try { await run(); } catch (error) { check(`${name} : déroulé`, false, error.message.split('\n')[0]); }
}

const info = (await call('GET', 'System/Info/Public')).json ?? {};
const olderJellyfin = Number(String(info.Version ?? '').split('.')[0]) < 12;
console.log(`Jellyfin ${info.Version ?? 'inconnu'}`);
const host = await signIn(user, password, 'check-host');
if (!host) { console.error(`Sign-in refused for ${user}`); process.exit(1); }
const me = (await call('GET', 'Users/Me', { token: host })).json;
const items = async (query) => (await call('GET', `Items?userId=${me.Id}&recursive=true&${query}`, { token: host })).json?.Items ?? [];
// Another account, not an administrator: made when it is missing (an earlier run may have made it).
const friendName = 'Ami Extension', friendPassword = 'ami-extension-check';
let friend = await signIn(friendName, friendPassword, 'check-friend');
if (!friend) {
  await call('POST', 'Users/New', { token: host, body: { Name: friendName, Password: friendPassword } });
  friend = await signIn(friendName, friendPassword, 'check-friend');
}

await part('extension', async () => {
  const csproj = fs.readFileSync(new URL('../../src/Mira.Jellyfin/Mira.Jellyfin.csproj', import.meta.url), 'utf8');
  const version = /<Version>([^<]+)<\/Version>/.exec(csproj)?.[1];
  const plugin = ((await call('GET', 'Plugins', { token: host })).json ?? []).find((p) => p.Name === 'Mira');
  check('extension : chargée par Jellyfin, à la version du dépôt', plugin?.Status === 'Active' && plugin?.Version === `${version}.0`,
    plugin ? `${plugin.Version} ${plugin.Status}` : 'absente');
});

// « Ta party »: the host's one link; its page for anyone, its JSON for Mira's apps; a guest opens it (a name, an
// account made for the device), reaches the host's group through the gateway and nothing else; removed, out at once;
// a new link and the old one no longer opens.
const FUNNEL = { 'Tailscale-Funnel-Request': '?1' };
// One request as Tailscale Funnel hands it on, its path as written: its status, 101 for a WebSocket opened (closed at once).
const throughFunnel = (path, headers = {}, method = 'GET') => new Promise((resolve) => {
  const origin = new URL(base);
  const request = http.request({ host: origin.hostname, port: origin.port, path, method, headers: { ...FUNNEL, ...headers } });
  request.on('upgrade', (_, socket) => { socket.destroy(); resolve(101); });
  request.on('response', (response) => { response.resume(); resolve(response.statusCode); });
  request.on('error', () => resolve(0));
  request.setTimeout(10000, () => { request.destroy(); resolve(0); });
  request.end();
});
const SOCKET = { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': 'dGhlIHNhbXBsZSBub25jZQ==' };
const root = new URL(base).pathname.replace(/\/$/, '');

// The proof that a Jellyfin found at another address is the account's own (ServerProof.cs, ServerMove): each account
// its key, with its session; a nonce signed for the host asked, without one; nothing through Funnel.
await part('preuve', async () => {
  const [hostKey, friendKey, anonymous] = [await call('GET', 'Mira/proof/key', { token: host }), await call('GET', 'Mira/proof/key', { token: friend }), await call('GET', 'Mira/proof/key')];
  const key = Buffer.from(hostKey.json?.key ?? '', 'base64');
  const nonce = crypto.randomBytes(32).toString('base64url');
  const plain = String(me.Id).replace(/-/g, '').toLowerCase();
  const asked = await call('GET', `Mira/proof?user=${me.Id}&nonce=${nonce}`);
  const hostName = new URL(base).host.toLowerCase();
  const expected = key.length === 32 ? crypto.createHmac('sha256', key).update(`${nonce}\n${hostName}`).digest('base64') : '?';
  const [noNonce, funnel] = [await call('GET', `Mira/proof?user=${plain}&nonce=court`), await throughFunnel(`${root}/Mira/proof?user=${plain}&nonce=${nonce}`)];
  check('preuve : une clé par compte avec sa session, le nonce signé pour l’adresse demandée, rien par Funnel',
    hostKey.status === 200 && key.length === 32 && friendKey.status === 200 && friendKey.json?.key !== hostKey.json?.key && anonymous.status === 401
      && asked.status === 200 && asked.json?.proof === expected && noNonce.status === 400 && funnel === 404,
    `clé ${hostKey.status}/${friendKey.status}/${anonymous.status} ; preuve ${asked.status} ${asked.json?.proof === expected ? 'juste' : 'fausse'} ; nonce court ${noNonce.status} ; Funnel ${funnel}`);
});

// « Le foyer » (HomeController): one password for the family, then a profile, an account the administrator offered.
// The password and the accounts offered by an administrator only; a device joins with the password, never at a wrong
// one; an administrator's profile only with a PIN, which it always keeps; a profile opened is a real session of that account under the home's
// device id, a PIN checked by the extension (5 tries, then a wait), one profile at a time on a device; a device
// removed and a new password put every device out; nothing through Funnel.
await part('foyer', async () => {
  const HOME = 'X-Mira-Home';
  const homeCall = (method, path, token, body) => call(method, `Mira/home/${path}`, { device: 'check-home', body, headers: token ? { [HOME]: token } : {} });
  const account = async (name, pw) => {
    let token = await signIn(name, pw, 'check-home-setup');
    if (!token) { await call('POST', 'Users/New', { token: host, body: { Name: name, Password: pw } }); token = await signIn(name, pw, 'check-home-setup'); }
    return (await call('GET', 'Users/Me', { token, device: 'check-home-setup' })).json?.Id?.replace(/-/g, '');
  };
  const [lina, noa] = [await account('Profil Lina', 'profil-lina-check'), await account('Profil Noa', 'profil-noa-check')];
  const admin = String(me.Id).replace(/-/g, '');
  await call('PUT', 'Mira/home/password', { token: host, body: { password: null } });

  const [off, refusedSettings, refusedPassword, short] = [await call('GET', 'Mira/home'), await call('GET', 'Mira/home/settings', { token: friend, device: 'check-friend' }),
    await call('PUT', 'Mira/home/password', { token: friend, device: 'check-friend', body: { password: 'famille-check-1' } }),
    await call('PUT', 'Mira/home/password', { token: host, body: { password: 'court' } })];
  const set = await call('PUT', 'Mira/home/password', { token: host, body: { password: 'famille-check-1' } });
  const offers = [await call('PUT', `Mira/home/profiles/${lina}`, { token: host, body: { offered: true } }), await call('PUT', `Mira/home/profiles/${noa}`, { token: host, body: { offered: true } })];
  const adminOffer = await call('PUT', `Mira/home/profiles/${admin}`, { token: host, body: { offered: true } });
  const adminWithPin = await call('PUT', `Mira/home/profiles/${admin}`, { token: host, body: { offered: true, pin: '8642' } });
  const adminPinOff = await call('PUT', `Mira/home/profiles/${admin}/pin`, { token: host, body: { pin: null } });
  const settings = (await call('GET', 'Mira/home/settings', { token: host })).json;
  const row = (id) => settings?.profiles?.find((p) => p.id === id);
  check('foyer : réglé par un administrateur seulement (mot de passe d’au moins 8 caractères, profils cochés), un compte administrateur seulement avec un code qu’il garde',
    off.json?.home === false && refusedSettings.status === 403 && refusedPassword.status === 403 && short.status === 400 && set.status === 204
      && offers.every((o) => o.status === 204) && adminOffer.status === 409 && adminWithPin.status === 204 && adminPinOff.status === 409 && settings?.home === true
      && row(lina)?.offered && row(noa)?.offered && row(admin)?.admin && row(admin)?.offered && row(admin)?.pin && (await call('GET', 'Mira/home')).json?.home === true,
    `sans foyer ${off.json?.home} ; un autre compte ${refusedSettings.status}/${refusedPassword.status} ; court ${short.status} ; réglé ${set.status} ; profils ${offers.map((o) => o.status)} ; administrateur ${adminOffer.status}/${adminWithPin.status}, sans code ${adminPinOff.status}`);

  const wrong = await homeCall('POST', 'join', null, { password: 'pas-le-bon', device: 'check-home-tv-01', name: 'Salon' });
  const joined = await homeCall('POST', 'join', null, { password: 'famille-check-1', device: 'check-home-tv-01', name: 'Télé du salon' });
  const home = joined.json?.token;
  const listed = await homeCall('GET', 'profiles', home);
  const ids = (listed.json?.profiles ?? []).map((p) => p.id);
  const [noToken, badToken] = [await homeCall('GET', 'profiles'), await homeCall('GET', 'profiles', 'pas-un-jeton')];
  check('foyer : rejoint avec son mot de passe seulement, les profils cochés, l’administrateur avec son code',
    wrong.status === 401 && joined.status === 200 && home?.length >= 32 && listed.status === 200 && ids.includes(lina) && ids.includes(noa) && ids.includes(admin)
      && listed.json?.profiles?.find((p) => p.id === admin)?.pin === true
      && noToken.status === 401 && badToken.status === 401,
    `mauvais ${wrong.status} ; rejoint ${joined.status} ; profils ${listed.status} ${JSON.stringify(listed.json?.profiles?.map((p) => p.name))} ; sans jeton ${noToken.status}/${badToken.status}`);

  // A profile opened: Jellyfin's own session of the account, under the home's device id, used as any other.
  const openedLina = await homeCall('POST', 'open', home, { profile: lina, client: 'Mira check', version: '1.0' });
  const linaToken = openedLina.json?.accessToken;
  const deviceId = openedLina.json?.deviceId;
  const asLina = await call('GET', 'Users/Me', { token: linaToken, device: deviceId });
  const linaItems = await call('GET', `Items?userId=${lina}&recursive=true&includeItemTypes=Movie`, { token: linaToken, device: deviceId });
  const sessions = (await call('GET', `Sessions?deviceId=${deviceId}`, { token: host })).json ?? [];
  const adminOpen = await homeCall('POST', 'open', home, { profile: admin });
  check('foyer : un profil ouvert est une vraie séance Jellyfin de ce compte, sous l’appareil du foyer ; l’administrateur jamais sans son code',
    openedLina.status === 200 && openedLina.json?.userId === lina && openedLina.json?.serverId === info.Id && /^mira-home-[0-9a-f]{32}$/.test(deviceId ?? '')
      && asLina.json?.Name === 'Profil Lina' && (linaItems.json?.Items?.length ?? 0) > 0 && sessions.some((s) => s.UserName === 'Profil Lina' && s.DeviceName === 'Télé du salon')
      && adminOpen.status === 403 && adminOpen.json?.pin === true,
    `ouvert ${openedLina.status} ${openedLina.text.slice(0, 80)} ; Users/Me ${asLina.json?.Name} ; films ${linaItems.json?.Items?.length} ; séances ${sessions.map((s) => `${s.UserName}@${s.DeviceName}`)} ; administrateur ${adminOpen.status}`);

  // A PIN: asked, wrong (tries left), right; five wrong in a row, then a wait even with the right one. The profile's
  // own session changes it only with the current one.
  const pinSet = await call('PUT', `Mira/home/profiles/${noa}/pin`, { token: host, body: { pin: '2468' } });
  const badPin = await call('PUT', `Mira/home/profiles/${noa}/pin`, { token: host, body: { pin: '12a4' } });
  const asked = await homeCall('POST', 'open', home, { profile: noa });
  const wrongPin = await homeCall('POST', 'open', home, { profile: noa, pin: '0000' });
  const openedNoa = await homeCall('POST', 'open', home, { profile: noa, pin: '2468' });
  const noaToken = openedNoa.json?.accessToken;
  const [linaAfter, noaMe] = [await call('GET', 'Users/Me', { token: linaToken, device: deviceId }), await call('GET', 'Users/Me', { token: noaToken, device: deviceId })];
  const ownWithout = await call('PUT', 'Mira/home/pin', { token: noaToken, device: deviceId, body: { pin: '1357' } });
  const ownWith = await call('PUT', 'Mira/home/pin', { token: noaToken, device: deviceId, body: { pin: '1357', current: '2468' } });
  const pinsBefore = (await call('GET', 'Mira/home/settings', { token: host })).json?.profiles?.find((p) => p.id === noa)?.pin;
  check('foyer : le code d’un profil vérifié par l’extension, un seul profil ouvert par appareil, le code changé par son profil avec l’ancien',
    pinSet.status === 204 && badPin.status === 400 && asked.status === 403 && asked.json?.pin === true && wrongPin.status === 403 && wrongPin.json?.left === 4
      && openedNoa.status === 200 && noaMe.json?.Name === 'Profil Noa' && linaAfter.status === 401 && ownWithout.status === 403 && ownWith.status === 204 && pinsBefore === true,
    `réglé ${pinSet.status}/${badPin.status} ; demandé ${asked.status} ; faux ${wrongPin.status} reste ${wrongPin.json?.left} ; bon ${openedNoa.status} ; Lina après ${linaAfter.status} ; par son profil ${ownWithout.status}/${ownWith.status}`);
  const tries = [];
  for (let i = 0; i < 5; i++) tries.push((await homeCall('POST', 'open', home, { profile: noa, pin: '9999' })).status);
  const locked = await homeCall('POST', 'open', home, { profile: noa, pin: '1357' });
  check('foyer : cinq codes faux de suite, puis il faut attendre, même avec le bon', tries.slice(0, 4).every((s) => s === 403) && tries[4] === 429 && locked.status === 429 && locked.json?.wait > 0,
    `${tries.join(', ')} ; puis le bon ${locked.status} (${locked.json?.wait} s)`);
  await call('PUT', `Mira/home/profiles/${noa}/pin`, { token: host, body: { pin: null } });
  await call('PUT', `Mira/home/profiles/${admin}`, { token: host, body: { offered: false } });

  // Nothing of the home through Funnel.
  const funnel = [await throughFunnel(`${root}/Mira/home`), await throughFunnel(`${root}/Mira/home/profiles`, { [HOME]: home })];
  check('foyer : rien par Tailscale Funnel', funnel.every((s) => s === 404), funnel.join(', '));

  // Removed by the administrator: its home token and its profile's session end; a new password: every device out.
  const second = (await homeCall('POST', 'join', null, { password: 'famille-check-1', device: 'check-home-phone-02', name: 'Téléphone' })).json?.token;
  const tv = (await call('GET', 'Mira/home/settings', { token: host })).json?.devices?.find((d) => d.name === 'Télé du salon');
  const openedAgain = await homeCall('POST', 'open', home, { profile: lina });
  const removed = await call('DELETE', `Mira/home/devices/${tv?.id}`, { token: host });
  const [homeAfter, profileAfter, secondBefore] = [await homeCall('GET', 'profiles', home), await call('GET', 'Users/Me', { token: openedAgain.json?.accessToken, device: deviceId }), await homeCall('GET', 'profiles', second)];
  await call('PUT', 'Mira/home/password', { token: host, body: { password: 'famille-check-2' } });
  const [secondAfter, oldPassword] = [await homeCall('GET', 'profiles', second), await homeCall('POST', 'join', null, { password: 'famille-check-1', device: 'check-home-phone-02', name: 'Téléphone' })];
  check('foyer : un appareil retiré est dehors avec son profil ; un nouveau mot de passe met tous les appareils dehors',
    tv?.profile === noa && openedAgain.status === 200 && removed.status === 204 && homeAfter.status === 401 && profileAfter.status === 401
      && secondBefore.status === 200 && secondAfter.status === 401 && oldPassword.status === 401,
    `appareil ${tv?.profile === noa ? 'sur Noa' : tv?.profile} ; retiré ${removed.status} ; jeton ${homeAfter.status}, profil ${profileAfter.status} ; autre appareil ${secondBefore.status} puis ${secondAfter.status} ; ancien mot de passe ${oldPassword.status}`);

  // Away from home (Tailscale): never connected here (nothing is sent to Tailscale by this check), so what it says before.
  const [tailnet, tailnetFriend, badClient, linkBefore, tailnetFunnel] = [await call('GET', 'Mira/home/tailnet', { token: host }),
    await call('GET', 'Mira/home/tailnet', { token: friend, device: 'check-friend' }), await call('PUT', 'Mira/home/tailnet', { token: host, body: { clientId: 'k 1', clientSecret: 'court' } }),
    await call('POST', 'Mira/home/link', { token: host }), await throughFunnel(`${root}/Mira/home/tailnet`)];
  check('foyer : loin de la maison, rien sans client OAuth (réglage dit, administrateur seulement, pas de lien, rien par Funnel)',
    tailnet.status === 200 && tailnet.json?.connected === false && tailnet.json?.setup?.tag === 'tag:mira-home' && tailnet.json?.setup?.grant?.includes(`tcp:${tailnet.json?.port}`)
      && tailnetFriend.status === 403 && badClient.status === 400 && linkBefore.status === 409 && linkBefore.json?.policy === 'disconnected' && tailnetFunnel === 404,
    `réglages ${tailnet.status} ${tailnet.text.slice(0, 160)} ; autre compte ${tailnetFriend.status} ; client ${badClient.status} ; lien ${linkBefore.status} ${linkBefore.json?.policy} ; Funnel ${tailnetFunnel}`);
  await call('PUT', 'Mira/home/password', { token: host, body: { password: null } });

  // « Inviter un ami » by a link (/Mira/friend): never connected to Tailscale here, so what it says before; the
  // friend's side refuses what is not an invitation, counts wrong tries, and nothing goes through Funnel.
  const [friends, friendsOther, inviteBefore, inviteAdmin] = [await call('GET', 'Mira/friend', { token: host }), await call('GET', 'Mira/friend', { token: friend, device: 'check-friend' }),
    await call('POST', 'Mira/friend/invite', { token: host, body: { user: lina } }), await call('POST', 'Mira/friend/invite', { token: host, body: { user: admin } })];
  const redeems = [];
  for (let i = 0; i < 11; i++) redeems.push((await call('POST', 'Mira/friend/redeem', { device: 'check-friend-redeem', body: { invite: `pas-une-invitation-${i}`, device: 'check-friend-phone', name: 'Téléphone' } })).status);
  const [noDevice, friendFunnel, redeemFunnel] = [await call('POST', 'Mira/friend/redeem', { body: { invite: 'x' } }), await throughFunnel(`${root}/Mira/friend`), await throughFunnel(`${root}/Mira/friend/redeem`, {}, 'POST')];
  const [waitWrong, waitFunnel] = [await call('POST', 'Mira/friend/wait', { body: { wait: 'pas-une-attente' } }), await throughFunnel(`${root}/Mira/friend/wait`, {}, 'POST')];
  check('ami par lien : rien sans Tailscale relié, administrateur seulement, invitation fausse refusée puis essais comptés, rien par Funnel',
    friends.status === 200 && friends.json?.tailnet === 'disconnected' && Array.isArray(friends.json?.invites) && friendsOther.status === 403
      && inviteBefore.status === 409 && inviteBefore.json?.state === 'disconnected' && inviteAdmin.status === 409
      && redeems.slice(0, 10).every((s) => s === 404) && redeems[10] === 429 && noDevice.status === 400 && friendFunnel === 404 && redeemFunnel === 404
      && waitWrong.status === 410 && waitWrong.json?.state === 'refused' && waitFunnel === 404,
    `état ${friends.status} ${friends.json?.tailnet} ; autre compte ${friendsOther.status} ; invitation ${inviteBefore.status} ${inviteBefore.json?.state} ; administrateur ${inviteAdmin.status} ${inviteAdmin.json?.state} ; essais ${redeems.join(',')} ; sans appareil ${noDevice.status} ; attente ${waitWrong.status} ${waitWrong.json?.state} ; Funnel ${friendFunnel}/${redeemFunnel}/${waitFunnel}`);
});

await part('party', async () => {
  for (const old of (await call('GET', 'Mira/invites', { token: host })).json?.invites ?? []) await call('DELETE', `Mira/invites/${old.id}`, { token: host });
  const address = await call('PUT', 'Mira/invites/address', { token: host, body: { address: 'https://mira-check.tail0000.ts.net' } });
  const party = (await call('GET', 'Mira/party', { token: host })).json;
  const code = party?.code ?? '';
  check('party : le lien de l’hôte, sur l’adresse donnée, et le lien de l’app',
    address.status < 300 && /^[A-Za-z0-9_-]{22}$/.test(code) && party.link === `https://mira-check.tail0000.ts.net/Mira/party/${code}`
      && party.appLink?.startsWith(`mira://party/${code}?server=`) && new URL(party.appLink).searchParams.get('id') === info.Id,
    `adresse ${address.status} ; ${party?.link} ; ${party?.appLink}`);

  const page = await call('GET', `Mira/party/${code}`);
  const asJson = await call('GET', `Mira/party/${code}`, { headers: { Accept: 'application/json' } });
  const title = /<meta property="og:title" content="([^"]*)"/.exec(page.text)?.[1];
  check('party : la page pour tous (carte Open Graph, « Open in Mira »), le JSON pour les apps',
    page.status === 200 && /text\/html/.test(page.type) && title === 'Join the Mira party' && page.text.includes(`mira://party/${code}`) && !page.text.includes('#/party')
      && asJson.status === 200 && asJson.json?.host === me.Name && asJson.json?.serverId === info.Id,
    `page ${page.status} « ${title} » ; JSON ${asJson.status} ${asJson.text.slice(0, 120)}`);

  // The host's group, then the guest: a name, its own account, a session only through the gateway.
  const film = (await items('includeItemTypes=Movie&searchTerm=Lueur'))[0];
  await call('POST', 'SyncPlay/Leave', { token: host });
  await call('POST', 'SyncPlay/New', { token: host, body: { GroupName: `Groupe de ${me.Name}` } });
  const opened = await call('POST', 'Mira/guest/open', { device: 'check-guest-phone-01', body: { party: code, device: 'check-guest-phone-01', name: 'Téléphone de Léa', guest: 'Léa' } });
  const guest = opened.json?.token ?? '';
  const gateway = (method, path, body) => call(method, `Mira/guest/${path}`, { token: guest, device: 'check-guest-phone-01', body, headers: FUNNEL });
  const [meAsGuest, users, list, title2, groups] = [await gateway('GET', 'Users/Me'), await gateway('GET', 'Users'), await gateway('GET', `Items?userId=${opened.json?.userId}`),
    await gateway('GET', `Items/${film?.Id}?userId=${opened.json?.userId}`), await gateway('GET', 'SyncPlay/List')];
  const hostGroup = (Array.isArray(groups.json) ? groups.json : []).find((g) => g.GroupName === `Groupe de ${me.Name}`);
  const joined = hostGroup ? await gateway('POST', 'SyncPlay/Join', { GroupId: hostGroup.GroupId }) : { status: 0 };
  await sleep(1000);
  const device = ((await call('GET', 'Sessions?deviceId=check-guest-phone-01', { token: host })).json ?? [])[0]?.DeviceName;
  const members = ((await call('GET', 'SyncPlay/List', { token: host })).json ?? []).find((g) => g.GroupId === hostGroup?.GroupId)?.Participants ?? [];
  check('party : l’invité ouvre le lien (un prénom, sans compte, un appareil nommé avec des accents), dans le groupe de l’hôte par la passerelle, et rien d’autre',
    opened.status === 200 && opened.json?.userName === 'Léa' && opened.json?.host === me.Name && opened.json?.serverId === info.Id
      && meAsGuest.json?.Name === 'Léa' && device === 'Téléphone de Léa' && users.status === 404 && list.status === 404 && title2.status === 404 && !!hostGroup && joined.status < 300 && members.length >= 2,
    `ouverture ${opened.status} ${opened.json?.userName ?? opened.text.slice(0, 80)} sur « ${device} » ; Users/Me ${meAsGuest.json?.Name} ; Users ${users.status}, Items ${list.status}, le film hors du groupe ${title2.status} ; groupe ${hostGroup ? 'vu' : 'absent'}, rejoint ${joined.status}, ${members.length} membres`);

  // The title the host puts on, and nothing else: a film, then an episode (its series, its seasons and episodes with
  // it); another film, another series, a season of another series named in the query: 404. The guest's session used
  // straight on Jellyfin, not through the gateway (a guest at home, on Jellyfin's network): 404 too.
  const uid = opened.json?.userId;
  const queue = async (id) => { await call('POST', 'SyncPlay/SetNewQueue', { token: host, body: { PlayingQueue: [id], PlayingItemPosition: 0, StartPositionTicks: 0 } }); await sleep(1000); };
  const other = (await items('includeItemTypes=Movie&searchTerm=Aube'))[0];
  const [webSeries, hlsSeries] = [(await items('includeItemTypes=Series&searchTerm=Courte Web'))[0], (await items('includeItemTypes=Series&searchTerm=Courte HLS'))[0]];
  const episodesOf = async (series) => (await call('GET', `Shows/${series?.Id}/Episodes?userId=${me.Id}`, { token: host })).json?.Items ?? [];
  const [webEpisodes, hlsEpisodes] = [await episodesOf(webSeries), await episodesOf(hlsSeries)];
  await queue(film?.Id);
  const asFilm = {
    film: (await gateway('GET', `Items/${film?.Id}?userId=${uid}`)).status, info: (await gateway('POST', `Items/${film?.Id}/PlaybackInfo?userId=${uid}`, {})).status,
    other: (await gateway('GET', `Items/${other?.Id}?userId=${uid}`)).status, poster: (await gateway('GET', `Items/${other?.Id}/Images/Primary`)).status,
    stream: (await gateway('GET', `Videos/${other?.Id}/stream?static=true`)).status, episodes: (await gateway('GET', `Shows/${webSeries?.Id}/Episodes?userId=${uid}`)).status,
  };
  await queue(webEpisodes[1]?.Id);
  const asEpisode = {
    episode: (await gateway('GET', `Items/${webEpisodes[1]?.Id}?userId=${uid}`)).status, series: (await gateway('GET', `Items/${webSeries?.Id}?userId=${uid}`)).status,
    sibling: (await gateway('GET', `Items/${webEpisodes[0]?.Id}?userId=${uid}`)).status, episodes: (await gateway('GET', `Shows/${webSeries?.Id}/Episodes?userId=${uid}`)).status,
    film: (await gateway('GET', `Items/${film?.Id}?userId=${uid}`)).status, otherSeries: (await gateway('GET', `Items/${hlsEpisodes[0]?.Id}?userId=${uid}`)).status,
    otherSeason: (await gateway('GET', `Shows/${webSeries?.Id}/Episodes?userId=${uid}&seasonId=${hlsEpisodes[0]?.SeasonId}`)).status,
  };
  const direct = { me: (await call('GET', 'Users/Me', { token: guest, device: 'check-guest-phone-01' })).status, film: (await call('GET', `Items/${film?.Id}?userId=${uid}`, { token: guest, device: 'check-guest-phone-01' })).status,
    library: (await call('GET', `Items?userId=${uid}&recursive=true`, { token: guest, device: 'check-guest-phone-01' })).status };
  check('party : l’invité ne lit que le titre du groupe (un film ; un épisode, sa série et ses épisodes), jamais un autre, ni sa séance hors de la passerelle',
    !!film && !!other && webEpisodes.length >= 2 && hlsEpisodes.length >= 1
      && asFilm.film === 200 && asFilm.info === 200 && [asFilm.other, asFilm.poster, asFilm.stream, asFilm.episodes].every((s) => s === 404)
      && [asEpisode.episode, asEpisode.series, asEpisode.sibling, asEpisode.episodes].every((s) => s === 200)
      && [asEpisode.film, asEpisode.otherSeries, asEpisode.otherSeason].every((s) => s === 404)
      && Object.values(direct).every((s) => s === 404),
    `film ${JSON.stringify(asFilm)} ; épisode ${JSON.stringify(asEpisode)} ; sans la passerelle ${JSON.stringify(direct)}`);

  // The guest's session written as Jellyfin reads it but not as it is (a letter URL-encoded, a space before « = », an
  // empty value first), or in its other places: still the guest's for Jellyfin, so still a 404 outside the gateway.
  const encoded = `%${guest.charCodeAt(0).toString(16)}${guest.slice(1)}`;
  const disguised = guest ? [
    { Authorization: `MediaBrowser Client="Mira check", Device="check", DeviceId="check-guest-phone-01", Version="1", Token="${encoded}"` },
    { Authorization: `MediaBrowser Client="Mira check", DeviceId="check-guest-phone-01", Token ="${guest}"` },
    { Authorization: `MediaBrowser Client="Mira check", DeviceId="check-guest-phone-01", Token=,"${guest}"` },
    { 'X-Emby-Authorization': `MediaBrowser Client="Mira check", DeviceId="check-guest-phone-01", Token="${encoded}"` },
    { 'X-Emby-Token': guest }, { 'X-MediaBrowser-Token': guest }, 'ApiKey', 'api_key',
  ] : [];
  const sneaked = [];
  for (const way of disguised) {
    const query = typeof way === 'string' ? `&${way}=${guest}` : '';
    const response = await fetch(`${base}/Items?userId=${uid}&recursive=true${query}`, { headers: typeof way === 'string' ? {} : way, redirect: 'manual' });
    sneaked.push(response.status);
  }
  check('party : la séance de l’invité, encodée, espacée ou ailleurs qu’à sa place, toujours refusée hors de la passerelle',
    disguised.length > 0 && sneaked.every((s) => s === 404), sneaked.join(', '));

  // Through Funnel, nothing but the party's page and the gateway: not Jellyfin, not the extension's other parts, not
  // even by a path that Jellyfin's server reads back to its own (« /Mira/%2e%2e/… »), and no WebSocket of another session.
  const outside = [];
  for (const path of ['/System/Info/Public', '/Mira/%2e%2e/System/Info/Public', '/web/', '/Users/Public', '/Mira/invites', '/Mira/torlink', '/Mira/party', '/Mira/party/new', '/Mira/subtitles', '/Mira/hls/x/master.m3u8']) {
    outside.push([path, await throughFunnel(root + path)]);
  }
  const [door, partyPage, guestSocket, hostSocket, elsewhere] = [await throughFunnel(`${root}/Mira/guest/System/Info/Public`), await throughFunnel(`${root}/Mira/party/${code}`),
    await throughFunnel(`${root}/Mira/guest/socket?ApiKey=${guest}&deviceId=check-guest`, SOCKET), await throughFunnel(`${root}/Mira/guest/socket?ApiKey=${host}&deviceId=check-host`, SOCKET),
    await throughFunnel(`${root}/socket?ApiKey=${host}&deviceId=check-host`, SOCKET)];
  check('party : par Tailscale Funnel, seulement la page de la party et la passerelle',
    outside.every(([, status]) => status === 404) && door === 200 && partyPage === 200 && guestSocket === 101 && hostSocket === 404 && elsewhere === 404,
    `${outside.map(([path, status]) => `${path} ${status}`).join(', ')} ; passerelle ${door}, party ${partyPage} ; WebSocket de l’invité ${guestSocket}, de l’hôte ${hostSocket}, ailleurs ${elsewhere}`);

  // Removed by the host: the guest's session ends at once. A new link: the old one's page says the party is over.
  const invite = ((await call('GET', 'Mira/invites', { token: host })).json?.invites ?? []).find((x) => x.name === 'Léa');
  const removed = invite ? await call('DELETE', `Mira/invites/${invite.id}`, { token: host }) : { status: 0 };
  await sleep(500);
  const after = await gateway('GET', 'Users/Me');
  const fresh = (await call('POST', 'Mira/party/new', { token: host })).json;
  const [oldPage, oldJson] = [await call('GET', `Mira/party/${code}`), await call('GET', `Mira/party/${code}`, { headers: { Accept: 'application/json' } })];
  const reopened = await call('POST', 'Mira/guest/open', { device: 'check-guest-phone-02', body: { party: code, device: 'check-guest-phone-02', name: 'Autre', guest: 'Autre' } });
  check('party : l’invité retiré est dehors tout de suite ; un nouveau lien, l’ancien ne marche plus',
    invite?.opened && invite?.party && removed.status < 300 && after.status === 401 && fresh?.code && fresh.code !== code
      && oldPage.status === 404 && /data-state="gone"/.test(oldPage.text) && oldJson.status === 404 && reopened.status === 404,
    `${invite?.opened ? `ouvert sur ${invite.device}` : 'jamais ouvert'} ; retiré ${removed.status} ; ancienne session ${after.status} ; nouveau lien ${fresh?.code === code ? 'le même' : 'autre'} ; ancien : page ${oldPage.status}, JSON ${oldJson.status}, ouverture ${reopened.status}`);

  const refused = friend ? [await call('GET', 'Mira/invites', { token: friend, device: 'check-friend' }), await call('PUT', 'Mira/invites/address', { token: friend, device: 'check-friend', body: { address: 'https://x.ts.net' } })] : [];
  check('party : les liens d’invités et l’adresse refusés à un compte qui n’est pas administrateur', refused.length === 2 && refused.every((r) => r.status === 403),
    refused.map((r) => r.status).join(', ') || 'pas d’autre compte');
  await call('POST', 'SyncPlay/Leave', { token: host });
});

// « Télécharger sur le PC serveur »: Mira for Windows on the PC of Jellyfin is played by a fake agent visiting the
// relay as it does. The apps search (one search, three categories), ask for a result, pause and cancel it, send a
// magnet; another account is refused; errors come in the language asked.
await part('relais', async () => {
  const picked = 'c12fe1c06bba254a9dc9f519b335aa7c1367a88a', pasted = '0123456789abcdef0123456789abcdef01234567';
  const magnetOf = (hash, name) => `magnet:?xt=urn:btih:${hash}&dn=${encodeURIComponent(name)}`;
  const fake = { searches: [], requests: [], controls: [], downloads: [], seen: [], stop: false };
  const agent = await signIn(user, password, 'check-agent');
  const loop = (async () => {
    while (!fake.stop) {
      const report = { name: 'PC-CHECK', accepting: true, problem: null, downloads: fake.downloads, placed: [], searches: fake.searches.splice(0), requests: fake.requests.splice(0), controls: fake.controls.splice(0), replaces: true };
      const work = (await call('POST', 'Mira/torlink/agent', { token: agent, device: 'check-agent', body: report }).catch(() => null))?.json;
      for (const s of work?.searches ?? []) {
        fake.seen.push(`search ${s.category} ${s.query}`);
        const film = { id: picked, name: 'Mira Check 2026 1080p WEB-DL', size: Math.round(1.4 * 2 ** 30), seeders: 42, leechers: 3, source: 'check', magnet: magnetOf(picked, 'Mira Check 2026 1080p WEB-DL'), added: null };
        fake.searches.push({ id: s.id, state: 'done', results: s.category === 'movies' ? [film] : [], error: null });
      }
      for (const r of work?.requests ?? []) {
        fake.seen.push(`add ${r.name}`);
        fake.requests.push({ id: r.id, state: 'sent', error: null });
        fake.downloads.push({ id: /btih:([0-9a-f]{40})/i.exec(r.magnet)[1].toLowerCase(), name: r.name, status: 'downloading', progress: 37, speed: 2 ** 20, peers: 4, size: 2 ** 30, downloaded: 2 ** 29, eta: 380 });
      }
      for (const c of work?.controls ?? []) {
        fake.seen.push(`${c.action} ${c.target}`);
        if (c.action === 'delete') fake.downloads = fake.downloads.filter((d) => d.id !== c.target);
        else { const d = fake.downloads.find((x) => x.id === c.target); if (d) d.status = c.action === 'pause' ? 'paused' : 'downloading'; }
        fake.controls.push({ id: c.id, state: 'done', error: null });
      }
      await sleep(300);
    }
  })();
  const until = async (test, seconds = 15) => { for (const limit = Date.now() + seconds * 1000; Date.now() < limit; await sleep(300)) { const value = await test(); if (value) return value; } return null; };
  try {
    const ready = await until(async () => (await call('GET', 'Mira/torlink', { token: host })).json?.agent);
    const asked = await call('POST', 'Mira/torlink/search', { token: host, body: { query: '  --mira   check ', category: 'all' } });
    const search = await until(async () => { const s = (await call('GET', `Mira/torlink/search/${asked.json?.id}`, { token: host })).json; return s?.state === 'done' ? s : null; });
    const hit = search?.results?.find((x) => x.id === picked);
    check('relais : la recherche faite par le PC serveur, dans les films, les séries et les animes à la fois',
      ready?.name === 'PC-CHECK' && ['movies', 'tv', 'anime'].every((kind) => fake.seen.includes(`search ${kind} mira check`)) && hit?.category === 'movies' && hit.seeders === 42 && !('magnet' in hit),
      `agent ${ready?.name ?? 'absent'} ; ${fake.seen.filter((x) => x.startsWith('search')).join(', ')} ; ${hit ? `${hit.name} (${hit.category})` : 'rien trouvé'}`);

    const requested = await call('POST', 'Mira/torlink/requests', { token: host, body: { search: asked.json?.id, result: picked, name: hit?.name, size: hit?.size } });
    const listed = await until(async () => ((await call('GET', 'Mira/torlink', { token: host })).json?.downloads ?? []).find((d) => d.id === picked));
    await call('POST', 'Mira/torlink/control', { token: host, body: { id: picked, action: 'pause' } });
    const paused = await until(async () => ((await call('GET', 'Mira/torlink', { token: host })).json?.downloads ?? []).find((d) => d.id === picked && d.status === 'paused'));
    await call('POST', 'Mira/torlink/control', { token: host, body: { id: picked, action: 'delete' } });
    const gone = await until(async () => !((await call('GET', 'Mira/torlink', { token: host })).json?.downloads ?? []).some((d) => d.id === picked));
    const badMagnet = await call('POST', 'Mira/torlink/requests', { token: host, body: { magnet: 'https://example.test/film' } });
    const magnet = await call('POST', 'Mira/torlink/requests', { token: host, body: { magnet: magnetOf(pasted, 'Collé Check') } });
    const sent = await until(async () => ((await call('GET', 'Mira/torlink', { token: host })).json?.downloads ?? []).find((d) => d.id === pasted));
    check('relais : un résultat demandé, suivi, mis en pause, annulé ; un lien magnet refusé quand il n’en est pas un, puis envoyé',
      requested.status === 200 && listed?.progress === 37 && !!paused && !!gone && fake.seen.includes(`delete ${picked}`) && badMagnet.status === 400 && magnet.status === 200 && sent?.name === 'Collé Check',
      `demande ${requested.status} ; ${listed ? `${listed.progress} %` : 'pas listé'} ; ${paused ? 'en pause' : 'pas en pause'} ; ${gone ? 'annulé' : 'resté'} ; faux lien ${badMagnet.status} ; lien ${magnet.status} ${sent?.name ?? ''}`);

    const [english, french] = [await call('POST', 'Mira/torlink/search', { token: host, body: { query: ' ', category: 'all' }, headers: { 'Accept-Language': 'en-US,en;q=0.9' } }),
      await call('POST', 'Mira/torlink/search', { token: host, body: { query: ' ', category: 'all' }, headers: { 'Accept-Language': 'fr-FR,fr;q=0.9' } })];
    check('relais : erreurs dans la langue demandée', english.json?.error === 'Type what you’re looking for.' && french.json?.error === 'Écris ce que tu cherches.',
      `« ${english.json?.error ?? english.text.slice(0, 80)} » / « ${french.json?.error ?? french.text.slice(0, 80)} »`);
    const other = friend ? [await call('GET', 'Mira/torlink', { token: friend, device: 'check-friend' }), await call('POST', 'Mira/torlink/agent', { token: friend, device: 'check-friend', body: { name: 'X', accepting: true } })] : [];
    check('relais : refusé à un compte qui n’est pas administrateur, comme agent aussi', other.length === 2 && other.every((r) => r.status === 403), other.map((r) => r.status).join(', ') || 'pas d’autre compte');
    // « Can download »: that account let in by an administrator (TorLink, never as the agent), then out again.
    const friendId = friend ? String((await call('GET', 'Users/Me', { token: friend, device: 'check-friend' })).json?.Id).replace(/-/g, '') : null;
    const [refusedByFriend, allowed] = friendId ? [await call('PUT', `Mira/home/profiles/${friendId}/download`, { token: friend, device: 'check-friend', body: { download: true } }),
      await call('PUT', `Mira/home/profiles/${friendId}/download`, { token: host, body: { download: true } })] : [];
    const listedDownload = friendId ? (await call('GET', 'Mira/home/settings', { token: host })).json?.profiles?.find((p) => p.id === friendId)?.download : null;
    const downloader = friendId ? [await call('GET', 'Mira/torlink', { token: friend, device: 'check-friend' }),
      await call('POST', 'Mira/torlink/agent', { token: friend, device: 'check-friend', body: { name: 'X', accepting: true } })] : [];
    if (friendId) await call('PUT', `Mira/home/profiles/${friendId}/download`, { token: host, body: { download: false } });
    const after = friendId ? await call('GET', 'Mira/torlink', { token: friend, device: 'check-friend' }) : null;
    check('relais : « Can download » laisse un compte non administrateur utiliser TorLink, jamais comme agent, et se retire',
      refusedByFriend?.status === 403 && allowed?.status === 204 && listedDownload === true && downloader[0]?.status === 200 && downloader[1]?.status === 403 && after?.status === 403,
      `par lui-même ${refusedByFriend?.status} ; par l’administrateur ${allowed?.status} ; réglages ${listedDownload} ; TorLink ${downloader[0]?.status}, agent ${downloader[1]?.status} ; retiré ${after?.status}`);
  } finally {
    fake.stop = true; await loop;
  }
});

// The iPhone app's conversions: Jellyfin's master playlist through the extension, its subtitle playlists there too.
await part('hls', async () => {
  const film = (await items('includeItemTypes=Movie&searchTerm=Lueur&fields=MediaSources'))[0];
  const source = film?.MediaSources?.[0];
  const subtitle = source?.MediaStreams?.find((s) => s.Type === 'Subtitle' && !s.IsExternal) ?? source?.MediaStreams?.find((s) => s.Type === 'Subtitle');
  const query = new URLSearchParams({ MediaSourceId: source?.Id ?? '', DeviceId: 'check-host', PlaySessionId: 'check', VideoCodec: 'h264', AudioCodec: 'aac', SegmentContainer: 'ts',
    SubtitleMethod: 'Hls', TranscodingMaxAudioChannels: '2', RequireAvc: 'false', ApiKey: host });
  const master = await call('GET', `Mira/hls/${film?.Id}/master.m3u8?${query}`, { token: host });
  const subtitles = /URI="([^"]*\/Mira\/hls\/[^"]*subtitles\.m3u8[^"]*)"/.exec(master.text)?.[1];
  const playlist = subtitles ? await call('GET', subtitles.replace(/^\//, '').replace(new RegExp(`^${root.replace(/^\//, '')}/?`), ''), { token: host }) : { status: 0, text: '' };
  check('HLS de l’app iPhone : la liste de Jellyfin par l’extension, ses sous-titres par l’extension aussi',
    master.status === 200 && master.text.startsWith('#EXTM3U') && /\/videos\/[0-9a-f]{32}\//i.test(master.text) && !!subtitles && playlist.status === 200 && playlist.text.startsWith('#EXTM3U'),
    `${subtitle ? `sous-titre ${subtitle.Index}` : 'aucun sous-titre'} ; liste ${master.status} ; sous-titres ${subtitles ? playlist.status : 'pas dans la liste'} ${master.status === 200 ? '' : master.text.slice(0, 120)}`);
});

// Automatic subtitles: the settings the apps show, changed and put back; refused to another account.
await part('sous-titres', async () => {
  const before = (await call('GET', 'Mira/subtitles', { token: host })).json?.settings;
  const changed = await call('PUT', 'Mira/subtitles/settings', { token: host, body: { languages: ['fre'] } });
  const now = (await call('GET', 'Mira/subtitles', { token: host })).json?.settings;
  const restored = await call('PUT', 'Mira/subtitles/settings', { token: host, body: { languages: before?.languages ?? [] } });
  const other = friend ? await call('GET', 'Mira/subtitles', { token: friend, device: 'check-friend' }) : { status: 0 };
  check('sous-titres automatiques : réglages lus, changés, remis ; refusés à un compte qui n’est pas administrateur',
    !!before && changed.status < 300 && now?.languages?.join() === 'fre' && restored.status < 300 && other.status === 403,
    `avant ${before?.languages?.join() || 'aucune langue'} ; après ${now?.languages?.join()} ; remis ${restored.status} ; autre compte ${other.status}`);
});

// « Identifier… »: the posters of the proposals through the extension, from a closed list of hosts only.
await part('identifier', async () => {
  const [elsewhere, local, other] = [await call('GET', `Mira/identify/image?url=${encodeURIComponent('https://example.com/poster.jpg')}`, { token: host }),
    await call('GET', `Mira/identify/image?url=${encodeURIComponent(`${base}/System/Info/Public`)}`, { token: host }),
    friend ? await call('GET', `Mira/identify/image?url=${encodeURIComponent('https://image.tmdb.org/t/p/w500/x.jpg')}`, { token: friend, device: 'check-friend' }) : { status: 0 }];
  check('identifier : affiches seulement des hôtes permis, et pour un administrateur',
    elsewhere.status === 400 && local.status === 400 && other.status === 403, `autre site ${elsewhere.status}, ce serveur ${local.status}, autre compte ${other.status}`);
});

// « Actualiser les métadonnées » of the apps: through the extension, which never reads all of a title's extras again at
// once (TitleRefresh); for an administrator. Queued: the answer comes at once.
await part('actualiser', async () => {
  const series = (await items('includeItemTypes=Series&searchTerm=Courte Web'))[0];
  // An id no item has, drawn each time: a fixed one can be a real item (00…01 is a placeholder row of Jellyfin 10.11).
  const [asked, unknown, other] = [await call('POST', `Mira/refresh/${series?.Id}?images=false`, { token: host }),
    await call('POST', `Mira/refresh/${crypto.randomBytes(16).toString('hex')}`, { token: host }),
    friend ? await call('POST', `Mira/refresh/${series?.Id}`, { token: friend, device: 'check-friend' }) : { status: 0 }];
  check('actualiser : un titre relu par l’extension, pour un administrateur seulement', !!series && asked.status === 204 && unknown.status === 404 && other.status === 403,
    `demande ${asked.status}, inconnu ${unknown.status}, autre compte ${other.status}`);
});

// The loops the extension runs by itself on a server, which no request of the apps reaches: a failure there shows only
// in Jellyfin's log, hours later (TvdbOrders on 12.4, subtitles on read-only media). Started every few seconds here
// (MIRA_CHECK_TICK_SECONDS, web.yml).
const until = async (test, seconds) => { for (const limit = Date.now() + seconds * 1000; Date.now() < limit; await sleep(1000)) { const value = await test(); if (value) return value; } return null; };

// « Sous-titres automatiques » for the whole library, in a language no test title has (German), from the fake
// OpenSubtitles: each title searched, its subtitle downloaded, written by Jellyfin where the library keeps them and
// listed with the title. Nothing reaches the real service.
await part('sous-titres automatiques', async () => {
  const fake = fakeOpenSubtitles(8767);
  await fake.start();
  const before = (await call('GET', 'Mira/subtitles', { token: host })).json?.settings;
  try {
    const set = await call('PUT', 'Mira/subtitles/settings', { token: host, body: { enabled: true, languages: ['ger'] } });
    const tested = (await call('POST', 'Mira/subtitles/test', { token: host })).json?.sources ?? [];
    const opensubtitles = tested.find((x) => x.source === 'OpenSubtitles');
    check('sous-titres automatiques : la source essayée depuis les Réglages (la fausse OpenSubtitles)', set.status < 300 && opensubtitles?.ok === true && opensubtitles.found >= 1,
      `réglages ${set.status} ; ${JSON.stringify(tested).slice(0, 200)}`);
    const queued = (await call('POST', 'Mira/subtitles/library', { token: host })).json;
    const titles = await items('includeItemTypes=Movie,Episode&isMissing=false');
    // Every title tried once: an entry of the journal for each. The media are read-only (web.yml): Jellyfin 10.x writes
    // subtitles in its own folder and keeps them; 12.x only beside the video, refused, which the extension must say
    // title by title in its journal, without spending another download on it, never throw (night audit E1). Titles of
    // the backlog are asked for once only: none waits after.
    const journalOf = async () => (await call('GET', 'Mira/subtitles', { token: host })).json?.journal ?? [];
    const done = await until(async () => new Set((await journalOf()).map((x) => x.itemId)).size >= titles.length, 240);
    const journal = await journalOf();
    const waiting = (await call('GET', 'Mira/subtitles', { token: host })).json?.waiting;
    const kinds = journal.reduce((count, entry) => ({ ...count, [entry.kind]: (count[entry.kind] ?? 0) + 1 }), {});
    const odd = journal.find((x) => x.kind !== (olderJellyfin ? 'kept' : 'failed'));
    const listed = olderJellyfin ? await until(async () => {
      const read = await items('includeItemTypes=Movie,Episode&isMissing=false&fields=MediaStreams');
      const german = read.filter((x) => (x.MediaStreams ?? []).some((m) => m.Type === 'Subtitle' && /^(ger|deu|de)$/i.test(m.Language ?? '')));
      return german.length >= titles.length ? german : null;
    }, 60) : null;
    const summary = `${queued?.added} en attente pour ${titles.length} titres ; ${done ? 'tous essayés' : 'pas tous essayés en 4 min'} ; journal ${JSON.stringify(kinds)} ; encore en attente ${waiting}`
      + (odd ? ` ; ex. ${JSON.stringify(odd).slice(0, 240)}` : '');
    if (olderJellyfin) {
      check('sous-titres automatiques : chaque film et épisode de la bibliothèque trouve, garde et liste son sous-titre (Jellyfin 10, dans son dossier à lui)',
        queued?.added >= titles.length && !!done && (kinds.kept ?? 0) >= titles.length && !odd && !!listed, `${summary} ; ${listed?.length ?? 'pas tous'} listés avec leur sous-titre`);
    } else {
      check('sous-titres automatiques : médias en lecture seule, chaque titre dit « pas écrit » dans le journal, un seul téléchargement, sans exception (Jellyfin 12)',
        queued?.added >= titles.length && !!done && (kinds.failed ?? 0) >= titles.length && !odd && fake.seen.downloads.length <= titles.length, summary);
    }
    check('sous-titres automatiques : rien vers le vrai OpenSubtitles, la clé des Réglages et le nom de Mira envoyés',
      fake.seen.searches.length >= titles.length && fake.seen.files.length >= titles.length && [...fake.seen.keys].every((key) => key === KEY) && fake.seen.keys.size === 1
        && [...fake.seen.agents].every((agent) => /^Mira/.test(agent)) && fake.seen.other.length === 0,
      `${fake.seen.searches.length} recherches, ${fake.seen.downloads.length} téléchargements, ${fake.seen.files.length} fichiers ; clés ${[...fake.seen.keys].join(', ')} ; ${[...fake.seen.agents].join(', ')}${fake.seen.other.length ? ` ; autre : ${fake.seen.other.join(', ')}` : ''}`);
  } finally {
    await call('PUT', 'Mira/subtitles/settings', { token: host, body: { enabled: before?.enabled ?? false, languages: before?.languages ?? [] } });
    await fake.stop();
  }
});

// TheTVDB's order: « Ordre Test » (its id in its folder's name) has files that fit the DVD order of the stand-in for
// TheTVDB's plugin, not the aired one: shown in the DVD order. A series without an id stays as it was.
await part('ordre TheTVDB', async () => {
  const [ordered, courte] = [(await items('includeItemTypes=Series&searchTerm=Ordre Test'))[0], (await items('includeItemTypes=Series&searchTerm=Courte Web'))[0]];
  const order = async (id) => (await call('GET', `Items/${id}?userId=${me.Id}`, { token: host })).json?.DisplayOrder ?? '';
  const dvd = await until(async () => (await order(ordered?.Id)) === 'dvd', 90);
  const other = await order(courte?.Id);
  check('ordre TheTVDB : une série montrée dans l’ordre des DVD où ses fichiers tombent juste, une série sans id laissée telle quelle',
    !!ordered && !!dvd && !!courte && other === '', `Ordre Test « ${await order(ordered?.Id)} » ; Courte Web « ${other} »`);
});

// /Mira and a party's page, since Mira web is gone: where to get Mira, never the web app; their files served by the
// revision; the manifest Mira for Windows 0.9.3 and before looks for (without it, it installs the extension again).
await part('pages', async () => {
  const [page, deep, manifest] = [await call('GET', 'Mira/', { token: null }), await call('GET', 'Mira/#/titre/abc', { token: null }),
    await call('GET', 'Mira/manifest.webmanifest', { token: null })];
  const assets = [...page.text.matchAll(/(?:href|src)="(v\/[^"]+)"/g)].map((m) => m[1]);
  const served = await Promise.all(assets.map(async (asset) => (await call('GET', `Mira/${asset}`, { token: null })).status));
  check('pages : /Mira dit où prendre Mira, ses fichiers servis, plus de Mira web',
    page.status === 200 && /text\/html/.test(page.type) && page.text.includes('https://miracinema.app') && !/app\.js|hls/.test(page.text)
      && deep.status === 200 && assets.some((a) => a.endsWith('pages.js')) && assets.some((a) => a.endsWith('pages.css')) && served.every((s) => s === 200),
    `page ${page.status}, ${assets.join(' ')} ${served.join(' ')}`);
  check('pages : manifeste encore servi (Mira pour Windows 0.9.3 et avant)', manifest.status === 200 && manifest.json?.name === 'Mira', `${manifest.status} ${manifest.text.slice(0, 80)}`);
});

if (olderJellyfin) console.log(`(Jellyfin ${info.Version} : même contrôle)`);
fs.writeFileSync(`${out}/results.txt`, results.join('\n') + '\n');
const failed = results.filter((r) => r.startsWith('FAIL')).length;
console.log(`${results.length - failed} réussis, ${failed} en échec`);
process.exit(failed ? 1 : 0);
