// Title cards and captions drawn over the real dashboard while the demo video records; record.js drives them.
(() => {
  const logo = '<svg viewBox="0 0 32 32" aria-hidden="true"><rect width="32" height="32" rx="6" fill="var(--brand-mark-bg)"/>'
    + '<path d="M6 16h8l4-8 4 16 4-8h0" fill="none" stroke="var(--accent)" stroke-width="3" stroke-linejoin="round" stroke-linecap="round"/>'
    + '<circle cx="26" cy="16" r="2.8" fill="var(--signal)"/></svg>';
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  const card = document.createElement('div');
  card.id = 'demo-card';
  document.body.appendChild(card);

  const caption = document.createElement('div');
  caption.id = 'demo-caption';
  caption.className = 'hidden';
  caption.innerHTML = '<div class="headline"></div><div class="prompt"></div><div class="result"></div>';
  document.body.appendChild(caption);
  const headline = caption.querySelector('.headline');
  const prompt = caption.querySelector('.prompt');
  const result = caption.querySelector('.result');

  // Reveals each .demo-pill and .fade of the card one after another.
  const reveal = async (gap) => {
    for (const el of card.querySelectorAll('.demo-pill, .fade')) { await sleep(gap); el.classList.add('in'); }
  };

  window.__demo = {
    async title() {
      card.innerHTML = `
        <div class="brand">${logo}<div class="name">SemanticRouter<span>.NET</span></div></div>
        <div class="tagline">Every prompt goes to the cheapest model<br>that can answer it.</div>
        <div class="row">
          <div class="demo-pill local"><b>Local models</b><small>LM Studio · Ollama · vLLM</small></div>
          <div class="demo-pill subs"><b>Your subscriptions</b><small>Claude Code · Copilot · Codex</small></div>
          <div class="demo-pill api"><b>Paid APIs</b><small>OpenAI · Anthropic · Gemini</small></div>
        </div>
        <div class="sub fade">One endpoint for Claude Code, Codex and any OpenAI or Anthropic client.</div>`;
      card.classList.remove('hidden');
      await reveal(450);
    },
    async end() {
      card.innerHTML = `
        <div class="brand">${logo}<div class="name">SemanticRouter<span>.NET</span></div></div>
        <div class="tagline fade">Cheap prompts stay local. Hard ones get Claude.<br>Private ones never leave your machine.</div>
        <code class="fade">dotnet tool install -g SemanticRouter.Host<br>semantic-router init <span class="c"># finds LM Studio, Ollama and your CLIs</span></code>
        <div class="url fade">github.com/siebe41/SemanticRouter.NET</div>
        <div class="sub fade">Open source · MIT · .NET 10</div>`;
      card.classList.remove('hidden');
      await reveal(600);
    },
    hideCard() { card.classList.add('hidden'); },

    async scene(text, who) {
      headline.textContent = text;
      prompt.innerHTML = who ? `<span class="who">${esc(who)} ›</span><span class="typed"></span><span class="cursor"></span>` : '';
      result.classList.remove('in');
      result.innerHTML = '';
      caption.classList.remove('hidden');
      await sleep(400);
    },
    // Types the prompt at a steady pace, so the viewer can read it.
    async type(text, cps = 55) {
      const typed = prompt.querySelector('.typed');
      const start = performance.now();
      for (;;) {
        const n = Math.min(text.length, Math.floor(((performance.now() - start) / 1000) * cps));
        typed.textContent = text.slice(0, n);
        if (n >= text.length) break;
        await new Promise((r) => requestAnimationFrame(r));
      }
    },
    result(html) {
      prompt.querySelector('.cursor')?.remove();
      result.innerHTML = html;
      result.classList.add('in');
    },
    hideCaption() { caption.classList.add('hidden'); },
  };
})();
