// What the stand-in models answer in the demo video: the first entry whose pattern matches the latest prompt wins.
// Used by lmstudio-stub.js and fake-claude/claude, so a reply looks the same whichever model the router picks.
const replies = [
  [/^(write|generate|suggest)\b.{0,40}\btitle\b/i, 'Billing repo help'],
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
];
const afterTool = "It's the billing service: it builds invoices from ledger events, applies tax per line, and converts currencies through the FX client. The README lists two open risks, duplicate invoices on replay and reused account numbers, both tracked under the billing migration.";

/** The prompt without the <system-reminder> blocks clients add, and only its last 400 characters (the latest turn). */
function latest(prompt) {
  return (prompt || '').replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, '').trim().slice(-400);
}

/** The reply for a prompt, and its words for streaming. */
function replyFor(prompt) {
  const text = latest(prompt);
  const hit = replies.find(([pattern]) => pattern.test(text));
  return hit ? hit[1] : fallback;
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
