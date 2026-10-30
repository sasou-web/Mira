// « Suivre une série » (an administrator of Jellyfin): Mira for Windows on the PC of Jellyfin looks for the new episodes
// of a series every 6 hours, or at once, keeps each release it chooses 10 minutes for « Annuler », then sends it to
// TorLink and places it in the series' folder (the relay of the extension: Mira.Jellyfin TorLinkRelay.Follows). The line
// of a series' page (views/detail.js) and the section of Télécharger (views/torlink.js) open the same sheet.
import { api } from './api.js';
import { h, icon } from './dom.js';
import { tr } from './i18n.js';
import { sheet, toast } from './components.js';
import { session } from './session.js';
import { cleanQuery, followLine, followNote, pickLine, takenLine } from './torlink.js';
import { readableName } from './releases.js';

/** What following does, said before a series is followed. */
const ABOUT = tr('Le PC serveur cherche ses nouveaux épisodes toutes les 6 heures, avec TorLink. Chacun attend 10 minutes dans Télécharger, le temps de l’annuler, puis TorLink le télécharge et Mira le range avec les autres. Rien ne part ailleurs que là où TorLink cherche déjà.');

let asked = null;
/**
 * The series this account follows, each with its releases chosen and episodes taken; null when it may not (not an
 * administrator) or the extension has none (0.8.4 and before).
 */
export function follows() {
  const token = session.current?.token;
  if (asked?.token !== token) asked = { token, admin: api.me().then((me) => !!me?.Policy?.IsAdministrator, () => { asked = null; return false; }) };
  return asked.admin.then((admin) => (admin ? api.torlinkFollows().then((list) => (Array.isArray(list) ? list : null), () => null) : null));
}

/** Mira web's words for what the relay refused. */
function explain(error) {
  if (error?.status === 404) return tr('L’extension Mira de ton serveur ne connaît pas encore cette option : mets à jour Mira sur le PC serveur, puis redémarre Jellyfin.');
  return error?.message ?? tr('Une erreur inattendue est survenue.');
}

/** Runs one of the relay's calls, says how it went, then `done()` (the screen reads the series again). */
async function run(call, said, done) {
  try { await call(); toast(said); } catch (error) { toast(explain(error), { tone: 'error' }); }
  done?.();
}

/** Follows a series of Jellyfin ({ Id, Name }) after saying what it does. */
export function followSheet(series, done) {
  sheet({
    title: tr('Télécharger les nouveaux épisodes'),
    body: h('p', { class: 'sheet-note' }, ABOUT),
    items: [{
      symbol: 'download', label: tr('Suivre « {name} »', { name: series.Name }),
      sub: tr('Commence par chercher l’épisode qui suit le dernier de ta bibliothèque.'),
      run: () => run(() => api.torlinkFollow(series.Id, series.Name), tr('« {name} » suivie : le PC serveur cherche ses nouveaux épisodes.', { name: series.Name }), done),
    }],
  });
}

/** A series followed: what the PC found, the episodes waiting (« Annuler »), and what to do with it. */
export function followedSheet(follow, done, { downloads = true } = {}) {
  const now = Date.now();
  const lines = [followLine(follow, now), followNote(follow.note)].filter(Boolean);
  const picks = (follow.picks ?? []).map((pick) => {
    const name = readableName(pick.name).title || pick.name;
    // Waiting: a tap cancels it (the whole line says so); refused: a tap takes it off the list.
    if (pick.state === 'waiting') return { symbol: 'close', label: tr('Annuler {episode}', { episode: pick.episode }), sub: `${pickLine({ ...pick, episode: '' }, now)} · ${name}`, danger: true, run: () => cancelPick(pick, done) };
    return { symbol: pick.state === 'failed' ? 'warning' : 'clock', label: pickLine(pick, now), sub: name, run: pick.state === 'failed' ? () => cancelPick(pick, done) : null };
  });
  const taken = (follow.taken ?? []).slice(0, 3).map((x) => ({ symbol: 'check', label: takenLine(x, now), sub: readableName(x.name).title || x.name }));
  sheet({
    title: follow.name,
    body: h('div', {},
      ...lines.map((text) => h('p', { class: 'sheet-note' }, text)),
      follow.query ? h('p', { class: 'sheet-note' }, tr('Cherchée sous « {query} ».', { query: follow.query })) : null),
    items: [
      ...(picks.length ? [{ heading: tr('En attente') }, ...picks] : []),
      ...(taken.length ? [{ heading: tr('Déjà pris') }, ...taken] : []),
      { heading: tr('Série suivie') },
      { symbol: 'refresh', label: tr('Chercher maintenant'), run: () => run(() => api.torlinkCheckFollow(follow.id), tr('Le PC serveur cherche les nouveaux épisodes de « {name} ».', { name: follow.name }), done) },
      { symbol: 'search', label: tr('Changer le nom cherché'), sub: tr('Pour une série qui sort sous un autre nom, son nom japonais par exemple.'), run: () => askQuery(follow, done) },
      downloads ? { symbol: 'download', label: tr('Voir dans Télécharger'), run: () => { location.hash = '#/telecharger'; } } : null,
      { symbol: 'close', label: tr('Ne plus suivre'), danger: true, run: () => run(() => api.torlinkUnfollow(follow.id), tr('« {name} » n’est plus suivie.', { name: follow.name }), done) },
    ].filter(Boolean),
  });
}

async function cancelPick(pick, done) {
  try {
    await api.torlinkCancel(pick.id);
    toast(pick.state === 'waiting' ? tr('{episode} annulé : ce fichier ne sera plus pris, un autre de l’épisode pourra l’être.', { episode: pick.episode }) : tr('Retiré de la liste.'));
  } catch (error) {
    toast(error.status === 409 ? tr('Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.') : error.status === 404 ? tr('Cet épisode n’est plus en attente.') : explain(error));
  }
  done?.();
}

/** The name TorLink searches for a series: empty for its own. */
function askQuery(follow, done) {
  const field = h('input', {
    class: 'input', type: 'search', autocomplete: 'off', autocorrect: 'off', autocapitalize: 'none', spellcheck: false, enterkeyhint: 'done',
    maxlength: 120, value: follow.query ?? follow.name, 'aria-label': tr('Nom cherché'), 'aria-describedby': 'follow-query-note',
  });
  let close = () => {};
  const box = h('form', {
    class: 'sheet-form',
    on: { submit: (e) => {
      e.preventDefault();
      const typed = cleanQuery(field.value);
      // Its own name again: no name of its own to search.
      const query = typed && typed.toLowerCase() !== follow.name.toLowerCase() ? typed : null;
      close();
      run(() => api.torlinkFollow(follow.seriesId, follow.name, query), tr('Nom cherché changé : le PC serveur cherche de nouveau.'), done);
    } },
  }, field,
  h('p', { class: 'group-note', id: 'follow-query-note' }, tr('Le nom sous lequel ses épisodes sortent, son nom japonais par exemple. Vide : son nom dans Jellyfin.')),
  h('button', { class: 'btn primary block', type: 'submit' }, icon('check', { size: 18 }), tr('Enregistrer')));
  close = sheet({ title: tr('Nom cherché'), body: box, focus: field });
}
