// A stand-in for LM Studio's OpenAI-compatible server, for the demo videos: lists two models and streams the reply
// from replies.js for the latest user message, quickly, the way a local model on a good GPU would. With other models
// and localApi off, it stands in for a paid OpenAI-compatible API instead.
//   node lmstudio-stub.js [port]     (default 1234, LM Studio's own port)
const http = require('http');
const { replyFor, words, toolCallFor, afterTool } = require('./replies');

const lmStudioModels = ['qwen/qwen3-8b', 'qwen/qwen3-coder-30b'];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function lastUserText(request) {
  const user = [...(request.messages || [])].reverse().find((m) => m.role === 'user');
  if (!user) return '';
  if (typeof user.content === 'string') return user.content;
  return (user.content || []).filter((p) => p.type === 'text').map((p) => p.text).join('\n');
}

// project: the demo project's directory, for the absolute paths Claude Code's tools need.
// models: the model ids it lists. loaded: those in memory at the start (default all). Like LM Studio with
// just-in-time loading, a request loads its model, and LM Studio's own API (/api/v0/models, unless localApi is off)
// reports which are loaded; the router prefers those. addModel(id) lists another, as if just downloaded.
function createStub({ project = process.cwd(), models: listed = lmStudioModels, loaded = listed, localApi = true } = {}) {
  const models = [...listed];
  const inMemory = new Set(loaded);
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', (c) => { body += c; });
    req.on('end', async () => {
      if (localApi && req.method === 'GET' && req.url === '/api/v0/models') {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ object: 'list', data: models.map((id) => ({ id, object: 'model', type: /gemma-3|-vl/.test(id) ? 'vlm' : 'llm', publisher: id.split('/')[0], quantization: 'Q4_K_M', state: inMemory.has(id) ? 'loaded' : 'not-loaded', max_context_length: 40960, ...(inMemory.has(id) ? { loaded_context_length: 32768 } : {}), capabilities: ['tool_use'] })) }));
        return;
      }
      if (req.method === 'GET' && req.url.endsWith('/models')) {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ object: 'list', data: models.map((id) => ({ id, object: 'model', owned_by: 'organization_owner' })) }));
        return;
      }
      if (req.method === 'GET') { res.writeHead(404); res.end(); return; }

      let request = {};
      try { request = JSON.parse(body || '{}'); } catch { /* not JSON */ }
      if (models.includes(request.model)) inMemory.add(request.model);
      if (process.env.DEMO_STUB_LOG) require('fs').appendFileSync(process.env.DEMO_STUB_LOG, `----- ${request.model} roles=${(request.messages || []).map((m) => m.role + (m.tool_calls ? '(calls)' : '')).join(',')}\n${lastUserText(request)}\n`);
      // A tool round trip: call a tool the client offers, then answer from its result.
      const messages = request.messages || [];
      const toolNames = (request.tools || []).map((t) => t.function?.name).filter(Boolean);
      // Clients add system messages mid-conversation, so the last message that isn't one says where the turn is.
      const toolResultBack = [...messages].reverse().find((m) => m.role !== 'system')?.role === 'tool';
      const call = toolResultBack ? null : toolCallFor(lastUserText(request), toolNames);
      if (call) {
        await sleep(150 + Math.random() * 150);
        const toolCall = { id: `call_${Date.now()}`, type: 'function', function: { name: call.name, arguments: JSON.stringify(call.input(project)) } };
        if (!request.stream) {
          res.writeHead(200, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ id: 'stub', object: 'chat.completion', model: request.model, choices: [{ index: 0, message: { role: 'assistant', content: null, tool_calls: [toolCall] }, finish_reason: 'tool_calls' }], usage: { prompt_tokens: 40, completion_tokens: 20, total_tokens: 60 } }));
          return;
        }
        res.writeHead(200, { 'Content-Type': 'text/event-stream' });
        const chunk = (delta, finish) => `data: ${JSON.stringify({ id: 'stub', object: 'chat.completion.chunk', model: request.model, choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`;
        res.write(chunk({ role: 'assistant', tool_calls: [{ index: 0, id: toolCall.id, type: 'function', function: { name: call.name, arguments: '' } }] }, null));
        res.write(chunk({ tool_calls: [{ index: 0, function: { arguments: toolCall.function.arguments } }] }, null));
        res.write(chunk({}, 'tool_calls'));
        res.end('data: [DONE]\n\n');
        return;
      }
      const toolResult = [...messages].reverse().find((m) => m.role === 'tool');
      const reply = toolResultBack ? afterTool(typeof toolResult?.content === 'string' ? toolResult.content : JSON.stringify(toolResult?.content)) : replyFor(lastUserText(request));
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
  server.addModel = (id) => { if (!models.includes(id)) models.push(id); };
  return server;
}

module.exports = { createStub };

if (require.main === module) {
  const port = Number(process.argv[2] || 1234);
  createStub({ project: process.env.DEMO_PROJECT || process.cwd() }).listen(port, '127.0.0.1', () => console.log(`LM Studio stub on http://localhost:${port}`));
}
