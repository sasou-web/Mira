// OpenSubtitles.com as the extension asks it (src/Mira.Jellyfin/Subtitles/OpenSubtitlesApi.cs), on this machine only:
// the checks never reach the real service (its quota, Mira's key). web.yml points the extension at it before Jellyfin
// starts (Mira.subtitles.json: openSubtitles.baseUrl, its own key); tools/extension/check.mjs runs it.
// Every search finds one subtitle made for the file (moviehash_match), in the language asked; a download gives its
// temporary link, then the file: an SRT of 8 cues up to 0:40, which the extension's check takes for any test video.
import http from 'node:http';

export const KEY = 'mira-check-opensubtitles';

export function fakeOpenSubtitles(port = 8767) {
  const seen = { searches: [], downloads: [], files: [], keys: new Set(), agents: new Set(), other: [] };
  const srt = Array.from({ length: 8 }, (_, i) => `${i + 1}\n00:00:${String(i * 5 + 1).padStart(2, '0')},000 --> 00:00:${String(i * 5 + 5).padStart(2, '0')},000\nMira check ${i + 1}.\n`).join('\n');
  const answer = (response, status, body, type = 'application/json') => {
    response.writeHead(status, { 'Content-Type': type });
    response.end(typeof body === 'string' ? body : JSON.stringify(body));
  };
  const server = http.createServer((request, response) => {
    const url = new URL(request.url, `http://127.0.0.1:${port}`);
    let body = '';
    request.on('data', (chunk) => { body += chunk; });
    request.on('end', () => {
      if (request.headers['api-key']) seen.keys.add(request.headers['api-key']);
      if (request.headers['user-agent']) seen.agents.add(request.headers['user-agent']);
      if (request.method === 'GET' && url.pathname === '/api/v1/subtitles') {
        const language = (url.searchParams.get('languages') ?? 'en').split(',')[0];
        seen.searches.push(url.search);
        const id = 7000 + seen.searches.length;
        return answer(response, 200, { total_pages: 1, total_count: 1, page: 1, data: [{ id: String(id), type: 'subtitle', attributes: {
          language, release: 'Mira.Check.WEB-DL', moviehash_match: true, from_trusted: true, hearing_impaired: false, foreign_parts_only: false,
          machine_translated: false, ai_translated: false, download_count: 1200, ratings: 8, fps: 24,
          files: [{ file_id: id, file_name: 'Mira.Check.WEB-DL.srt' }] } }] });
      }
      if (request.method === 'POST' && url.pathname === '/api/v1/download') {
        const id = (() => { try { return JSON.parse(body).file_id; } catch { return null; } })();
        seen.downloads.push(id);
        if (!id) return answer(response, 400, { message: 'file_id expected' });
        return answer(response, 200, { link: `http://127.0.0.1:${port}/files/${id}.srt`, file_name: 'Mira.Check.WEB-DL.srt', requests: seen.downloads.length,
          remaining: 100 - seen.downloads.length, reset_time_utc: new Date(Date.now() + 86400000).toISOString() });
      }
      if (request.method === 'GET' && /^\/files\/\d+\.srt$/.test(url.pathname)) {
        seen.files.push(url.pathname);
        response.writeHead(200, { 'Content-Type': 'application/x-subrip', 'Content-Disposition': 'attachment; filename="Mira.Check.WEB-DL.srt"' });
        return response.end(srt);
      }
      seen.other.push(`${request.method} ${url.pathname}`);
      return answer(response, 404, { message: 'not in the fake' });
    });
  });
  return {
    seen,
    start: () => new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); }),
    stop: () => new Promise((resolve) => server.close(() => resolve())),
  };
}
