// Mira's Jellyfin extension against a real Jellyfin, through its own API, without a browser: what Mira for Windows,
// Mac and iPhone ask of it. Installed from its repository (tools/web/setup.mjs), it must be loaded at its version;
// « Ta party » (the host's link, its page and its JSON, a guest opening it, the gateway's closed list, the host's group
// joined, the guest removed, a new link); Tailscale Funnel's guard; the TorLink relay with a fake agent (Mira for
// Windows on the PC of Jellyfin): search in the three categories, a download asked, paused and cancelled, a magnet,
// a non-administrator refused, errors in the language asked; the HLS playlists of the iPhone app (/Mira/hls); the
// settings of automatic subtitles; the poster relay of « Identifier… ». results.txt; exit 1 on failure.
//   node tools/extension/check.mjs <server> <user> <password> <out folder>
import fs from 'node:fs';
import http from 'node:http';

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
const throughFunnel = (path, headers = {}) => new Promise((resolve) => {
  const origin = new URL(base);
  const request = http.request({ host: origin.hostname, port: origin.port, path, headers: { ...FUNNEL, ...headers } });
  request.on('upgrade', (_, socket) => { socket.destroy(); resolve(101); });
  request.on('response', (response) => { response.resume(); resolve(response.statusCode); });
  request.on('error', () => resolve(0));
  request.setTimeout(10000, () => { request.destroy(); resolve(0); });
  request.end();
});
const SOCKET = { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': 'dGhlIHNhbXBsZSBub25jZQ==' };
const root = new URL(base).pathname.replace(/\/$/, '');

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
    page.status === 200 && /text\/html/.test(page.type) && title === 'Join the Mira party' && page.text.includes(`mira://party/${code}`)
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
      && meAsGuest.json?.Name === 'Léa' && device === 'Téléphone de Léa' && users.status === 404 && list.status === 404 && title2.status === 200 && !!hostGroup && joined.status < 300 && members.length >= 2,
    `ouverture ${opened.status} ${opened.json?.userName ?? opened.text.slice(0, 80)} sur « ${device} » ; Users/Me ${meAsGuest.json?.Name} ; Users ${users.status}, Items ${list.status}, le film ${title2.status} ; groupe ${hostGroup ? 'vu' : 'absent'}, rejoint ${joined.status}, ${members.length} membres`);

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

if (olderJellyfin) console.log(`(Jellyfin ${info.Version} : même contrôle)`);
fs.writeFileSync(`${out}/results.txt`, results.join('\n') + '\n');
const failed = results.filter((r) => r.startsWith('FAIL')).length;
console.log(`${results.length - failed} réussis, ${failed} en échec`);
process.exit(failed ? 1 : 0);
