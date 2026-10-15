// A small Obsidian-style vault for the privacy video: a dozen linked notes about the demo's billing project. Fictional
// people and companies; nothing in it is real.
const fs = require('fs');
const path = require('path');

const notes = {
  'Billing migration.md': `---
tags: [project, billing]
---
# Billing migration

Moving invoices from the legacy ledger to the new event store by the end of Q4.
Owner: [[Dana Kim]], reviewed by the [[Platform team]].

## Status
- Ledger events have been replayed nightly into the new store since September.
- Cutover is planned for the weekend of November 14, with a two-hour write freeze.
- [[Acme Corp]] and [[Globex]] are the pilot customers for the new invoices.

## Open risks
- Replaying ledger events twice creates duplicate invoices, see [[Invoice service]].
- Closed account numbers are reused in the legacy system, see [[Ledger]].
- Tax rounding differs by up to one cent per line between the two systems.
`,
  'Invoice service.md': `# Invoice service

Builds invoices from ledger events and applies tax per line.
Runs as a nightly job and on demand from the billing UI.
Part of the [[Billing migration]]; on call: [[Platform team]].
`,
  'Ledger.md': `# Ledger

The legacy ledger keeps balances per account in a single SQL database.
Account numbers are recycled after closure, which the new store must not do.
See [[Billing migration]] and [[Q4 planning]].
`,
  'Q4 planning.md': `# Q4 planning

Goals for the quarter: finish the [[Billing migration]], ship usage-based pricing.
Hiring: one more engineer for the [[Platform team]].
Budget review with finance in the first week of December.
`,
  'Ideas/Usage-based pricing.md': `# Usage-based pricing

Charge per invoice line above a monthly allowance, instead of per seat.
Needs the new event store from the [[Billing migration]] to meter usage.
Talked it through with [[Acme Corp]] on the last call.
`,
  'People/Dana Kim.md': `---
tags: [person]
---
# Dana Kim

Engineering lead for billing. Owns the [[Billing migration]].
Prefers written proposals over meetings; review requests go in the team channel.
`,
  'People/Platform team.md': `---
tags: [team]
---
# Platform team

Runs the shared infrastructure and the on-call rotation for billing.
Members: [[Dana Kim]] and three engineers; weekly sync on Tuesdays.
`,
  'Customers/Acme Corp.md': `---
tags: [customer]
---
# Acme Corp

Pilot customer for the new invoices. Contract renews in March.
Billing contact prefers PDF invoices and net-45 payment terms.
Asked about [[Usage-based pricing]] for their European subsidiaries.
`,
  'Customers/Globex.md': `---
tags: [customer]
---
# Globex

Second pilot customer for the [[Billing migration]].
Invoices in three currencies; rounding issues were reported in August.
`,
  'Daily/2026-10-13.md': `# 2026-10-13

Replayed September's ledger events again; two duplicate invoices for [[Globex]].
Asked [[Dana Kim]] whether the replay can be made idempotent before cutover.
`,
  'Daily/2026-10-14.md': `# 2026-10-14

Went through the [[Billing migration]] risks with the [[Platform team]].
Decided to freeze writes for two hours during cutover rather than dual-write.
`,
};

/** Writes the vault into dir (replacing what is there), with an .obsidian folder like a real vault's. */
function writeNotes(dir) {
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(path.join(dir, '.obsidian'), { recursive: true });
  fs.writeFileSync(path.join(dir, '.obsidian', 'app.json'), '{}\n');
  for (const [file, text] of Object.entries(notes)) {
    fs.mkdirSync(path.dirname(path.join(dir, file)), { recursive: true });
    fs.writeFileSync(path.join(dir, file), text);
  }
}

module.exports = { writeNotes };
