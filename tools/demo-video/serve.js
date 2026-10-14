// Starts a stub LM Studio and the real `semantic-router` host on demo-router.json, for recording the demo video.
//   DEMO_PORT        the router's port (default 5290)
//   DEMO_ROUTER_DLL  the host to run (default: the Release build of src/SemanticRouter.Host)
// The stub answers like LM Studio on localhost:1234 (or DEMO_PORT + 1 when LM Studio itself is running). Claude Code
// is fake-claude/claude behind the real CLI connector; while the file at limitFile exists it answers with Claude
// Code's session-limit message (record.js creates it mid-video).
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

const port = Number(process.env.DEMO_PORT || 5290);
const dll = process.env.DEMO_ROUTER_DLL
  || path.resolve(__dirname, '../../src/SemanticRouter.Host/bin/Release/net10.0/semantic-router.dll');
const limitFile = path.join(os.tmpdir(), 'semantic-router-demo-claude-limit');
fs.rmSync(limitFile, { force: true });

const replies = { 'qwen/qwen3-8b': 'Sure, here you go.', 'qwen/qwen3-coder-30b': 'Here is the code.' };

const stub = http.createServer((req, res) => {
  let body = '';
  req.on('data', (c) => { body += c; });
  req.on('end', () => {
    if (req.method === 'GET' && req.url.endsWith('/models')) {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ object: 'list', data: Object.keys(replies).map((id) => ({ id })) }));
      return;
    }

    let request = {};
    try { request = JSON.parse(body || '{}'); } catch { /* not JSON */ }
    setTimeout(() => {
      const reply = replies[request.model] || 'OK.';
      const usage = { prompt_tokens: 40, completion_tokens: 12, total_tokens: 52 };
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
    }, 180 + Math.random() * 200);
  });
});

function start(stubPort) {
  // A throw-away configuration directory holding demo-router.json with its placeholders filled in.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'semantic-router-demo-'));
  const json = (s) => JSON.stringify(s).slice(1, -1);
  const config = fs.readFileSync(path.join(__dirname, 'demo-router.json'), 'utf8')
    .replace(/__LMSTUDIO__/g, `http://localhost:${stubPort}`)
    .replace(/__CLAUDE__/g, json(path.join(__dirname, 'fake-claude', 'claude')))
    .replace(/__VAULT__/g, json(path.join(__dirname, 'vault')));
  fs.writeFileSync(path.join(dir, 'semantic-router.json'), config);

  const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_NOLOGO: '1', DEMO_CLAUDE_LIMIT_FILE: limitFile };
  delete env.ROUTER_ADMIN_TOKEN;
  const router = spawn('dotnet', [dll, '--config-dir', dir, '--urls', `http://127.0.0.1:${port}`], { env, stdio: 'inherit' });
  const stop = () => { router.kill(); stub.close(); fs.rmSync(dir, { recursive: true, force: true }); fs.rmSync(limitFile, { force: true }); };
  router.on('exit', (code) => { stub.close(); process.exit(code ?? 0); });
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { stop(); process.exit(0); });
  process.on('exit', () => router.kill());
}

stub.once('error', (e) => {
  if (e.code !== 'EADDRINUSE') throw e;
  stub.listen(port + 1, '127.0.0.1', () => start(port + 1));
});
stub.listen(1234, '127.0.0.1', () => start(1234));
