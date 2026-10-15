// Publishes the recorded videos to a Ghost site as draft posts, through the Ghost Admin API: uploads each video and
// its poster frame, then creates a draft with the video, a short write-up and the install commands.
//
//   GHOST_URL=https://your-site.ghost.io GHOST_ADMIN_KEY=<id>:<secret> node publish_ghost.js [video …]
//
// GHOST_ADMIN_KEY comes from Ghost Admin → Settings → Integrations → Add custom integration → Admin API key. Posts are
// created as drafts, never published: review them in Ghost Admin first. Videos default to every entry in posts below
// whose MP4 exists under out/.
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const site = (process.env.GHOST_URL || '').replace(/\/+$/, '');
const [keyId, keySecret] = (process.env.GHOST_ADMIN_KEY || '').split(':');
if (!site || !keyId || !keySecret) {
  console.error('Set GHOST_URL and GHOST_ADMIN_KEY (<id>:<secret>).');
  process.exit(2);
}

const out = path.join(__dirname, 'out');
const repo = 'https://github.com/siebe41/SemanticRouter.NET';
const install = `<pre><code>dotnet tool install -g SemanticRouter.Host
semantic-router init
semantic-router</code></pre>`;

const posts = {
  intro: {
    file: 'semantic-router-demo.mp4',
    title: 'SemanticRouter.NET in two minutes',
    excerpt: 'Send every prompt to the cheapest model that can answer it: a local model, your Claude subscription, or a paid API.',
    body: `<p>Most prompts don't need a frontier model. SemanticRouter.NET sits between your AI tools and your models and sends each prompt to the cheapest model that can handle it.</p>
<p>In this video: installing it, letting <code>semantic-router init</code> find LM Studio and the Claude Code CLI, pointing Claude Code at it, and watching each turn go to the right model, including what happens when Claude's session limit runs out.</p>
<h2>Try it</h2>${install}
<p>Then point Claude Code at it with <code>ANTHROPIC_BASE_URL=http://localhost:5080</code>, or any OpenAI client with <code>OPENAI_BASE_URL=http://localhost:5080/v1</code>. Source and docs: <a href="${repo}">${repo.replace('https://', '')}</a>.</p>`,
  },
  'deep-dive': {
    file: 'semantic-router-deep-dive.mp4',
    title: 'Deep dive: how SemanticRouter.NET routes a prompt',
    excerpt: 'Routes, complexity, tiers and strategies; tool calls, callouts and secret detection; failover; and how every decision explains itself.',
    body: `<p>A closer look at how SemanticRouter.NET decides where each prompt goes, and what it does when something goes wrong.</p>
<ol>
<li><strong>What does the prompt need?</strong> Routes, similarity and complexity, in the dashboard's prompt tester.</li>
<li><strong>Who should answer?</strong> Tiers, and the strategy that ranks the models that qualify.</li>
<li><strong>Coding agents.</strong> Claude Code's tool calls staying on one model, and <code>@opus</code>-style callouts.</li>
<li><strong>Privacy and failure.</strong> Secret detection keeping a conversation local; cooldowns and failover.</li>
<li><strong>Seeing why.</strong> The activity log and the Evaluate page.</li>
</ol>
<h2>Try it</h2>${install}
<p>Source and docs: <a href="${repo}">${repo.replace('https://', '')}</a>.</p>`,
  },
};

// Ghost Admin API tokens: a short-lived HS256 JWT signed with the key's hex secret.
function token() {
  const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const now = Math.floor(Date.now() / 1000);
  const unsigned = `${b64({ alg: 'HS256', typ: 'JWT', kid: keyId })}.${b64({ iat: now, exp: now + 300, aud: '/admin/' })}`;
  return `${unsigned}.${crypto.createHmac('sha256', Buffer.from(keySecret, 'hex')).update(unsigned).digest('base64url')}`;
}

async function admin(method, apiPath, body, isForm = false) {
  const res = await fetch(`${site}/ghost/api/admin${apiPath}`, {
    method,
    headers: { Authorization: `Ghost ${token()}`, 'Accept-Version': 'v5.0', ...(isForm ? {} : { 'Content-Type': 'application/json' }) },
    body: isForm ? body : body && JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${apiPath}: ${res.status} ${text.slice(0, 500)}`);
  return JSON.parse(text);
}

async function upload(kind, file, type, extra = {}) {
  const form = new FormData();
  form.append('file', new Blob([fs.readFileSync(file)], { type }), path.basename(file));
  for (const [k, v] of Object.entries(extra)) form.append(k, v);
  const res = await admin('POST', `/${kind}/upload/`, form, true);
  return (res.media || res.images)[0];
}

(async () => {
  const wanted = process.argv.slice(2).length ? process.argv.slice(2) : Object.keys(posts);
  for (const name of wanted) {
    const post = posts[name];
    const video = path.join(out, post.file);
    if (!post || !fs.existsSync(video)) { console.log(`skip ${name}: ${video} not found`); continue; }

    // A poster frame from a few seconds in (the title card), as the video's thumbnail and the post's feature image.
    const poster = video.replace(/\.mp4$/, '.poster.jpg');
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-ss', '4', '-i', video, '-frames:v', '1', '-q:v', '3', poster]);
    const image = await upload('images', poster, 'image/jpeg', { purpose: 'image' });
    const media = await upload('media', video, 'video/mp4');
    console.log(`uploaded ${post.file} → ${media.url}`);

    const html = `<!--kg-card-begin: html--><video controls preload="metadata" playsinline poster="${image.url}" src="${media.url}" style="width:100%;border-radius:8px"></video><!--kg-card-end: html-->\n${post.body}`;
    const res = await admin('POST', '/posts/?source=html', { posts: [{ title: post.title, custom_excerpt: post.excerpt, feature_image: image.url, html, status: 'draft' }] });
    const created = res.posts[0];
    console.log(`draft "${created.title}": ${site}/ghost/#/editor/post/${created.id}`);
  }
})().catch((err) => { console.error(err.message); process.exit(1); });
