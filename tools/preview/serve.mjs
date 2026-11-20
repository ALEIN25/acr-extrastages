// Static server for the offline stage preview: node tools/preview/serve.mjs [port]  ->  http://localhost:8765/?stage=AcrXsFi01r2
// Serves only tools/preview (the viewer) and work/acr/preview (launcher --preview output), read-only, on localhost.
import { createServer } from 'node:http';
import { readFile, readdir, stat } from 'node:fs/promises';
import { join, normalize, extname, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = normalize(join(here, '..', '..'));
const previews = join(root, 'work', 'acr', 'preview');
const port = Number(process.argv[2] ?? 8765);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.glb': 'model/gltf-binary', '.png': 'image/png' };

createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://localhost');
    let file;
    if (url.pathname === '/stages') {
      const ids = [];
      for (const d of await readdir(previews).catch(() => [])) if (await stat(join(previews, d, 'preview.glb')).catch(() => null)) ids.push(d);
      res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(ids));
      return;
    }
    if (url.pathname.startsWith('/preview/')) file = normalize(join(previews, decodeURIComponent(url.pathname.slice('/preview/'.length))));
    else file = normalize(join(here, url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname)));
    if (!file.startsWith(previews) && !file.startsWith(here)) { res.writeHead(403).end(); return; }
    const body = await readFile(file);
    res.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream', 'cache-control': 'no-store' }).end(body);
  } catch {
    res.writeHead(404).end();
  }
}).listen(port, '127.0.0.1', () => console.log(`stage preview: http://localhost:${port}/`));
