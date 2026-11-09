// node --test tools/virustotal.test.mjs
// tools/virustotal.sh against a fake curl: whatever VirusTotal answers, the script ends in 0 (it runs in the release
// job), and each file's fate is said. Audit of 2026-11-09, point 5: a refused upload address stopped the job.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdtempSync, readFileSync, rmSync, truncateSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(new URL('./virustotal.sh', import.meta.url));

// The fake curl: the URL is the last argument; each kind of request answers the next code of its list (FAKE_LOOKUP,
// FAKE_UPLOAD_URL, FAKE_UPLOAD; the last one repeats), 000 = no answer (curl's own failure, exit 28).
const fakeCurl = `#!/usr/bin/env bash
out=/dev/null; for ((i = 1; i <= $#; i++)); do [ "\${!i}" = -o ] && { j=$((i + 1)); out=\${!j}; }; done
url=\${!#}
case "$url" in
  */files/upload_url) kind=UPLOAD_URL ;;
  https://upload.example/*|*/api/v3/files) kind=UPLOAD ;;
  *) kind=LOOKUP ;;
esac
echo "$kind $url" >> "$FAKE_LOG"
n=$(grep -c "^$kind " "$FAKE_LOG")
list=FAKE_$kind; IFS=, read -ra codes <<< "\${!list}"
code=\${codes[$((n - 1))]:-\${codes[-1]}}
[ "$kind" = UPLOAD_URL ] && [ "$code" = 200 ] && printf '{"data": "https://upload.example/big"}' > "$out"
[ "$code" = 000 ] && { printf 000; exit 28; }
printf '%s' "$code"
`;

function run(env, sizes = [10]) {
  const dir = mkdtempSync(join(tmpdir(), 'virustotal-'));
  try {
    writeFileSync(join(dir, 'curl'), fakeCurl);
    chmodSync(join(dir, 'curl'), 0o755);
    const files = sizes.map((size, i) => {
      const file = join(dir, `Mira-${i}.bin`);
      writeFileSync(file, `file ${i}`);
      truncateSync(file, size);
      return file;
    });
    writeFileSync(join(dir, 'log'), '');
    const summary = join(dir, 'summary.md');
    const result = spawnSync('bash', [script, ...files], {
      encoding: 'utf8',
      env: {
        ...process.env, PATH: `${dir}:${process.env.PATH}`, FAKE_LOG: join(dir, 'log'), GITHUB_STEP_SUMMARY: summary,
        VIRUSTOTAL_API_KEY: 'key', VIRUSTOTAL_PACE: '0', FAKE_LOOKUP: '404', FAKE_UPLOAD_URL: '200', FAKE_UPLOAD: '200', ...env,
      },
    });
    const log = readFileSync(join(dir, 'log'), 'utf8').trim().split('\n').filter(Boolean);
    let said = '';
    try { said = readFileSync(summary, 'utf8'); } catch { /* no summary written */ }
    return { status: result.status, out: result.stdout, err: result.stderr, log, said };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const big = 33 * 1024 * 1024;

test('Sans clé : rien d’envoyé, la release continue', () => {
  const r = run({ VIRUSTOTAL_API_KEY: '' });
  assert.equal(r.status, 0);
  assert.deepEqual(r.log, []);
  assert.match(r.out, /VIRUSTOTAL_API_KEY is not set/);
});

test('Un fichier déjà connu est seulement lié', () => {
  const r = run({ FAKE_LOOKUP: '200' });
  assert.equal(r.status, 0);
  assert.deepEqual(r.log.map((l) => l.split(' ')[0]), ['LOOKUP']);
  assert.match(r.out, /already on VirusTotal, https:\/\/www\.virustotal\.com\/gui\/file\/[0-9a-f]{64}/);
});

test('Petit fichier envoyé directement, gros par l’adresse donnée', () => {
  const r = run({}, [10, big]);
  assert.equal(r.status, 0, r.err);
  assert.deepEqual(r.log.map((l) => l.split(' ')[0]), ['LOOKUP', 'UPLOAD', 'LOOKUP', 'UPLOAD_URL', 'UPLOAD']);
  assert.ok(r.log[4].endsWith('https://upload.example/big'));
  assert.equal((r.said.match(/: sent, /g) ?? []).length, 2);
});

test('Adresse d’envoi refusée : avertissement, fichier suivant quand même, sortie 0', () => {
  const r = run({ FAKE_UPLOAD_URL: '403' }, [big, 10]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.log.filter((l) => l.startsWith('UPLOAD_URL')).length, 3);
  assert.match(r.err, /no upload address for Mira-0\.bin/);
  assert.match(r.out, /Mira-1\.bin: sent/);
});

test('Trop de requêtes puis réponse : réessayé', () => {
  const r = run({ FAKE_UPLOAD_URL: '429,200', FAKE_UPLOAD: '000,200' }, [big]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.log.filter((l) => l.startsWith('UPLOAD_URL')).length, 2);
  assert.equal(r.log.filter((l) => l.startsWith('UPLOAD ')).length, 2);
  assert.match(r.out, /Mira-0\.bin: sent/);
});

test('VirusTotal muet partout : trois essais par requête, sortie 0', () => {
  const r = run({ FAKE_LOOKUP: '000', FAKE_UPLOAD: '000' }, [10]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.log.length, 6);
  assert.match(r.err, /could not be sent to VirusTotal \(HTTP 000\)/);
  assert.match(r.said, /not sent/);
});

test('Fichier absent : dit, sortie 0', () => {
  const r = spawnSync('bash', [script, '/nonexistent/Mira.exe'], { encoding: 'utf8', env: { ...process.env, VIRUSTOTAL_API_KEY: 'key', VIRUSTOTAL_PACE: '0', PATH: '/nonexistent:' + process.env.PATH } });
  assert.equal(r.status, 0);
  assert.match(r.stderr, /no such file/);
});
