import { createHash } from 'node:crypto';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const origin = 'https://bilicinema.leonz03.dpdns.org';
const downloads = fileURLToPath(new URL('../public/downloads/', import.meta.url));
const namePattern = /^BiliCinema-(\d{1,3}\.\d{1,3}\.\d{1,3})-android-universal\.apk$/;
const maxApk = 25 * 1024 * 1024;

function valid(entry) {
  const name = namePattern.exec(entry?.filename);
  if (!name || name[1] !== entry.version || !Number.isSafeInteger(entry.bytes)
    || entry.bytes < 1000 || entry.bytes > maxApk || !/^[a-f0-9]{64}$/.test(entry.sha256)) {
    throw new Error('Invalid Android release metadata');
  }
}

async function get(path, maxBytes) {
  const response = await fetch(origin + path, {
    redirect: 'manual', signal: AbortSignal.timeout(60000),
    headers: { 'cache-control': 'no-cache' },
  });
  if (response.status === 404) return null;
  if (response.status !== 200 || response.url !== origin + path) throw new Error(`Unexpected response for ${path}: ${response.status}`);
  const declared = Number(response.headers.get('content-length'));
  if (Number.isFinite(declared) && declared > maxBytes) throw new Error(`Oversized response for ${path}`);
  const chunks = [];
  let length = 0;
  for await (const chunk of response.body) {
    length += chunk.length;
    if (length > maxBytes) throw new Error(`Oversized response for ${path}`);
    chunks.push(chunk);
  }
  return Buffer.concat(chunks, length);
}

const manifestBytes = await get('/downloads/android-manifest.json', 65536);
if (manifestBytes === null) {
  const health = await get('/health', 4096);
  if (!health || JSON.parse(new TextDecoder().decode(health)).service !== 'bilicinema-rooms') {
    throw new Error('Update manifest absent and existing Worker health could not be verified');
  }
  console.log('No published Android manifest; existing Worker is healthy. First release may be staged separately.');
} else {
  const manifest = JSON.parse(new TextDecoder().decode(manifestBytes));
  if (manifest.applicationId !== 'com.leonz03.bilicinema' || !Number.isSafeInteger(manifest.versionCode)
    || !Array.isArray(manifest.history) || manifest.history.length > 2) throw new Error('Invalid Android manifest');
  const entries = [manifest, ...manifest.history];
  const names = new Set();
  await mkdir(downloads, { recursive: true });
  const localManifestPath = join(downloads, 'android-manifest.json');
  let localManifest = null;
  if (process.argv.includes('--preserve-local')) {
    try { localManifest = JSON.parse(await readFile(localManifestPath, 'utf8')); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (localManifest && (localManifest.version !== manifest.version || localManifest.sha256 !== manifest.sha256)) {
      const localEntries = [localManifest, ...(localManifest.history || [])];
      if (!localEntries.some(entry => entry.filename === manifest.filename && entry.sha256 === manifest.sha256)) {
        throw new Error('Local staged manifest conflicts with the published release');
      }
    }
  }
  for (const entry of entries) {
    valid(entry);
    if (names.has(entry.filename)) throw new Error('Duplicate Android release');
    names.add(entry.filename);
    const target = join(downloads, entry.filename);
    let bytes;
    try {
      if ((await stat(target)).size > maxApk) throw new Error(`Oversized local Android asset: ${entry.filename}`);
      bytes = await readFile(target);
    }
    catch (error) { if (error.code !== 'ENOENT') throw error; bytes = await get('/downloads/' + entry.filename, maxApk); }
    if (!bytes || bytes.length !== entry.bytes || createHash('sha256').update(bytes).digest('hex') !== entry.sha256) {
      throw new Error(`Missing or corrupt published Android asset: ${entry.filename}`);
    }
    await writeFile(target, bytes);
  }
  if (!localManifest || !process.argv.includes('--preserve-local')) await writeFile(localManifestPath, manifestBytes);
  console.log(`Restored ${entries.length} verified Android APK asset(s) from the production manifest.`);
}
