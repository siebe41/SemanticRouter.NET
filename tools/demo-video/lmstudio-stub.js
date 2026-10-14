// A stand-in for LM Studio's OpenAI-compatible server, for the demo video: lists two models and streams the reply
// from replies.js for the latest user message, quickly, the way a local model on a good GPU would.
//   node lmstudio-stub.js [port]     (default 1234, LM Studio's own port)
const http = require('http');
const { replyFor, words } = require('./replies');

const models = ['qwen/qwen3-8b', 'qwen/qwen3-coder-30b'];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function lastUserText(request) {
  const user = [...(request.messages || [])].reverse().find((m) => m.role === 'user');
  if (!user) return '';
  if (typeof user.content === 'string') return user.content;
  return (user.content || []).filter((p) => p.type === 'text').map((p) => p.text).join('\n');
}

function createStub() {
  return http.createServer((req, res) => {
    let body = '';
    req.on('data', (c) => { body += c; });
    req.on('end', async () => {
      if (req.method === 'GET' && req.url.endsWith('/models')) {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ object: 'list', data: models.map((id) => ({ id, object: 'model', owned_by: 'organization_owner' })) }));
        return;
      }

      let request = {};
      try { request = JSON.parse(body || '{}'); } catch { /* not JSON */ }
      if (process.env.DEMO_STUB_LOG) require('fs').appendFileSync(process.env.DEMO_STUB_LOG, `----- ${request.model}\n${lastUserText(request)}\n`);
      const reply = replyFor(lastUserText(request));
      const usage = { prompt_tokens: Math.round(body.length / 4), completion_tokens: words(reply).length, total_tokens: 0 };
      usage.total_tokens = usage.prompt_tokens + usage.completion_tokens;
      await sleep(150 + Math.random() * 150);

      if (!request.stream) {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ id: 'stub', object: 'chat.completion', model: request.model, choices: [{ index: 0, message: { role: 'assistant', content: reply }, finish_reason: 'stop' }], usage }));
        return;
      }

      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      const chunk = (delta, finish) => `data: ${JSON.stringify({ id: 'stub', object: 'chat.completion.chunk', model: request.model, choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`;
      res.write(chunk({ role: 'assistant' }, null));
      for (const word of words(reply)) {
        res.write(chunk({ content: word }, null));
        await sleep(18);
      }
      res.write(chunk({}, 'stop'));
      res.write(`data: ${JSON.stringify({ id: 'stub', object: 'chat.completion.chunk', choices: [], usage })}\n\n`);
      res.end('data: [DONE]\n\n');
    });
  });
}

module.exports = { createStub };

if (require.main === module) {
  const port = Number(process.argv[2] || 1234);
  createStub().listen(port, '127.0.0.1', () => console.log(`LM Studio stub on http://localhost:${port}`));
}
