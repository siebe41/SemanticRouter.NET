// What the stand-in models answer in the demo video: the first entry whose pattern matches the latest prompt wins.
// Used by lmstudio-stub.js and fake-claude/claude, so a reply looks the same whichever model the router picks.
const replies = [
  [/^(write|generate|suggest)\b.{0,40}\btitle\b/i, 'Billing repo help'],
  [/risks we missed|review (that|the) plan/i,
    "Two gaps. The replay isn't idempotent yet, so a retried night creates duplicates during the freeze too: key invoices by ledger event id first. And nothing covers rollback: if cutover fails after the freeze, the legacy ledger has missed two hours of writes. Plan a replay back, or keep dual-writing until you're sure."],
  [/what can you (do|help)/i,
    "Hi! I can read and change the code in this repo, write tests, explain what something does, and dig into bugs. What would you like to work on?"],
  [/unit test/i,
    "Here's an xUnit test for `InvoiceTotal`:\n\n```csharp\n[Fact]\npublic void InvoiceTotal_sums_lines_and_rounds_tax()\n{\n    var invoice = new Invoice([new Line(19.99m, 2), new Line(5.00m, 1)]);\n    Assert.Equal(54.39m, InvoiceTotal.Compute(invoice, taxRate: 0.21m));\n}\n```"],
  [/stale/i,
    "Found it. `CacheWarmer` runs before the new config is loaded, so after a deploy it re-caches prices from the old snapshot. Moving the warm-up after `ConfigLoaded` fixes it; I've drafted the change in `Startup.cs`."],
  [/deadlock|root cause|memory leak|race condition/i,
    "The invoice job blocks on `.Result` inside `LedgerClient.GetBalance`, which runs on the request's synchronization context. Under load every worker thread waits on a continuation that needs that same context, so the pool starves.\n\nFix: make `GetBalance` async all the way down and `await` it in `InvoiceJob.RunAsync`, or call `ConfigureAwait(false)` in the client. I'd do the first; it's a three-file change."],
  [/regex/i,
    "Here's a practical one:\n\n```\n^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$\n```\n\nIt accepts one `@`, no spaces, and a dot in the domain. It deliberately doesn't try to cover every RFC 5322 edge case."],
  [/call fail|401|unauthori[sz]ed/i,
    "The API answers 401 because that key belongs to the sandbox workspace, and `api.billing.example` is production. Use the production key from your secret store, and don't paste keys into prompts: rotate this one."],
  [/customer record|account number|salary/i,
    'Account 4471 belongs to Acme Corp: contract renews in March, billing contact is Bob, and the account is still on the legacy ledger.'],
  [/summari[sz]e/i,
    '- Replaying ledger events twice creates duplicate invoices.\n- Closed account numbers get reused in the legacy system.\n- Tax rounding differs by up to one cent per line.'],
  [/implement|refactor|across the codebase|every caller/i,
    "Plan:\n1. Add `ICooldownStore` with a SQLite implementation.\n2. Load cooldowns at start-up and write them on every change.\n3. Update the three callers and add tests for a restart mid-cooldown.\n\nStarting with step 1."],
  [/trade-?offs?|architecture|design/i,
    'Event sourcing gives you a full audit trail and lets you replay billing history, at the cost of more moving parts and harder queries. CRUD is simpler and fine while invoices are the only source of truth. For the billing migration I would keep CRUD and add an append-only ledger table for audits.'],
];

const fallback = 'Sure. Here you go.';

// Prompts that make a local model call one of the client's tools first (Claude Code's Read), and what it says once the
// tool result comes back.
const toolCalls = [
  [/what does this (project|repo) do|read the readme/i, { name: 'Read', input: (project) => ({ file_path: `${project}/README.md` }) }],
  [/billing migration note/i, { name: 'Read', input: (project) => ({ file_path: `${require('path').dirname(project)}/notes/Billing migration.md` }) }],
];
const afterReadme = "It's the billing service: it builds invoices from ledger events, applies tax per line, and converts currencies through the FX client. The README lists two open risks, duplicate invoices on replay and reused account numbers, both tracked under the billing migration.";
const afterNote = 'The migration is on track: ledger events replay nightly into the new store, and cutover is the weekend of November 14 with a two-hour write freeze. Three risks are open: duplicate invoices on replay, reused account numbers, and one-cent tax rounding differences.';

/** What a model says once the tool result it asked for comes back. */
const afterTool = (result) => (process.env.DEMO_FULL_ANSWERS ? fullAnswer(latest(result), 'Next step.')
  : /Billing migration/.test(result || '') ? afterNote : afterReadme);

/** The prompt without the <system-reminder> blocks clients add, and only its last 400 characters (the latest turn). */
function latest(prompt) {
  return (prompt || '').replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, '').trim().slice(-400);
}

// With DEMO_FULL_ANSWERS set (the savings video), answers run as long as real ones: a quick answer about a hundred
// words, a coding task a few hundred, a hard debugging question about a thousand. The usual replies are a sentence or
// two, which would leave the output side of the bill, where most of the price is, nearly empty.
const more = [
  'The relevant code is in the billing project, and the change stays inside the invoice pipeline.',
  'I checked the callers as well, and none of them depend on the old behaviour.',
  'The existing tests still pass, and I added cases for the edge conditions mentioned above.',
  'If you want, I can also update the documentation and the changelog entry for this.',
  'One thing to watch: the nightly job runs with a different configuration than the web app.',
  'The same pattern appears in the export worker, so it may be worth fixing there too.',
  'This keeps the public API unchanged, so nothing outside the project needs to be rebuilt.',
  'I would roll it out behind the existing feature flag and watch the error rate for a day.',
  'Here is the reasoning step by step, so you can check each assumption against the logs.',
  'The numbers line up with what the metrics showed during last week\'s incident.',
];
function fullAnswer(text, reply) {
  // A step of an agent's loop, after a tool result: the model's thinking and its next tool call, a few thousand tokens.
  const target = /\(end of tool output\)|summary of the conversation/.test(text) ? 4500
    : /root cause|step by step|race condition|diagnose|deadlock|design|plan |why does|review|figure out/i.test(text) ? 1000
    : /^(write|add|convert|turn|extract|refactor|implement|make)\b/i.test(text) ? 380 : 110;
  // The same prompt always gets the same length, somewhere within a quarter either side of the target.
  const hash = [...text].reduce((h, c) => (h * 31 + c.charCodeAt(0)) >>> 0, 7);
  const length = Math.round(target * (0.75 + (hash % 1000) / 2000));
  let answer = reply;
  for (let i = hash % more.length; words(answer).length < length; i++) answer += ` ${more[i % more.length]}`;
  return answer;
}

/** The reply for a prompt, and its words for streaming. */
function replyFor(prompt) {
  const text = latest(prompt);
  const hit = replies.find(([pattern]) => pattern.test(text));
  const reply = hit ? hit[1] : fallback;
  return process.env.DEMO_FULL_ANSWERS ? fullAnswer(text, reply) : reply;
}

function words(text) {
  return text.match(/\s*\S+/g) || [text];
}

/** The tool call to make first for a prompt, when the client offers that tool, or null. */
function toolCallFor(prompt, toolNames) {
  const hit = toolCalls.find(([pattern, call]) => pattern.test(latest(prompt)) && toolNames.includes(call.name));
  return hit ? hit[1] : null;
}

module.exports = { replyFor, words, toolCallFor, afterTool };
