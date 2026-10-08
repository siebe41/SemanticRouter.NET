// Starts a stub OpenAI-compatible backend and the real router host for the end-to-end tests, and stops both on exit.
//   E2E_PORT          the router's port (default 5190); the stub listens on E2E_PORT + 1
//   E2E_ROUTER_DLL    the host to run (default: the Release build of src/SemanticRouter.Host)
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

const port = Number(process.env.E2E_PORT || 5190);
const stubPort = port + 1;
const dll = process.env.E2E_ROUTER_DLL
  || path.resolve(__dirname, '../../src/SemanticRouter.Host/bin/Release/net9.0/semantic-router.dll');

// The stub model: answers every chat request with the same sentence, streamed or not.
const reply = 'Hello from the stub backend.';
const stub = http.createServer((req, res) => {
  let body = '';
  req.on('data', (c) => { body += c; });
  req.on('end', () => {
    if (req.method === 'GET' && req.url.endsWith('/models')) {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ object: 'list', data: [{ id: 'stub-small' }, { id: 'stub-large' }] }));
      return;
    }

    let request = {};
    try { request = JSON.parse(body || '{}'); } catch { /* not JSON */ }
    const usage = { prompt_tokens: 12, completion_tokens: 6, total_tokens: 18 };
    if (request.stream) {
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      const chunk = (delta, finish) => `data: ${JSON.stringify({ id: 'stub', object: 'chat.completion.chunk', model: request.model, choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`;
      res.write(chunk({ role: 'assistant', content: reply }, null));
      res.write(chunk({}, 'stop'));
      res.write(`data: ${JSON.stringify({ id: 'stub', object: 'chat.completion.chunk', choices: [], usage })}\n\n`);
      res.end('data: [DONE]\n\n');
      return;
    }

    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ id: 'stub', object: 'chat.completion', model: request.model, choices: [{ index: 0, message: { role: 'assistant', content: reply }, finish_reason: 'stop' }], usage }));
  });
});

// A throw-away configuration directory: the fixture configuration and two theme files.
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'semantic-router-e2e-'));
const config = fs.readFileSync(path.join(__dirname, 'fixtures', 'semantic-router.json'), 'utf8').replace(/__STUB__/g, `http://127.0.0.1:${stubPort}`);
fs.writeFileSync(path.join(dir, 'semantic-router.json'), config);
fs.cpSync(path.join(__dirname, 'fixtures', 'themes'), path.join(dir, 'themes'), { recursive: true });

stub.listen(stubPort, '127.0.0.1', () => {
  const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_NOLOGO: '1' };
  delete env.ROUTER_ADMIN_TOKEN;
  const router = spawn('dotnet', [dll, '--config-dir', dir, '--urls', `http://127.0.0.1:${port}`], { env, stdio: 'inherit' });
  const stop = () => { router.kill(); stub.close(); fs.rmSync(dir, { recursive: true, force: true }); };
  router.on('exit', (code) => { stub.close(); process.exit(code ?? 0); });
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { stop(); process.exit(0); });
  process.on('exit', () => router.kill());
});
