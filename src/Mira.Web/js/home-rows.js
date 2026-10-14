// « Accueil à ta main »: which rows the home page shows, in which order, as Mira on Windows and Mac (HomeLayout.cs,
// cases in tests/fixtures/home.json). Saved as one line, « continue,favorites,-latest,library:id »: the order, a « - »
// before a row put away. Rows not in it yet (a new library) come after: the built-in rows shown, a library's own row put away.
// Imported by Node too (tools/web/unit): nothing of the browser here.

export const CONTINUE = 'continue', LATEST = 'latest', FAVORITES = 'favorites', LIBRARY = 'library:';

/** The libraries that get a row of their own: films, series and mixed ones, not Jellyfin's collections. */
export const offered = (library) => [undefined, null, 'movies', 'tvshows', 'mixed'].includes(library.CollectionType);

/** Every row, in order: { key, title, shown, library } (library: the id of « Nouveau dans … »). */
export function homeRows(saved, libraries = []) {
  const all = [
    { key: CONTINUE, title: 'Continuer à regarder', shown: true, library: null },
    { key: LATEST, title: 'Ajouts récents', shown: true, library: null },
    { key: FAVORITES, title: 'Tes favoris', shown: true, library: null },
    ...libraries.filter(offered).map((x) => ({ key: LIBRARY + x.Id, title: `Nouveau dans ${x.Name}`, shown: false, library: x.Id })),
  ];
  const rows = [];
  for (const part of String(saved ?? '').split(',').map((x) => x.trim()).filter(Boolean)) {
    const hidden = part.startsWith('-'), key = hidden ? part.slice(1) : part;
    // A library gone from Jellyfin, a row of a later version: left out, not kept as an empty place.
    const row = all.find((x) => x.key === key);
    if (row && !rows.some((x) => x.key === key)) rows.push({ ...row, shown: !hidden });
  }
  rows.push(...all.filter((x) => !rows.some((r) => r.key === x.key)));
  return rows;
}
export const saveRows = (rows) => rows.map((x) => (x.shown ? '' : '-') + x.key).join(',');
/** The row moved up (-1) or down (+1); as it was at either end. */
export function moveRow(saved, libraries, key, step) {
  const rows = homeRows(saved, libraries), index = rows.findIndex((x) => x.key === key), target = index + Math.sign(step);
  if (index < 0 || target < 0 || target >= rows.length) return saveRows(rows);
  [rows[index], rows[target]] = [rows[target], rows[index]];
  return saveRows(rows);
}
export const showRow = (saved, libraries, key, shown) => saveRows(homeRows(saved, libraries).map((x) => (x.key === key ? { ...x, shown } : x)));
