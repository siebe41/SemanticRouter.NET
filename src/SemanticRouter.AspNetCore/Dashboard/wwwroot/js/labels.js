/* Semantic Router dashboard: plain-language labels and the formatters every page shares (reasons, where the configuration
   came from, tiers, money, counts, dates, durations). Imports only core.js. */
import { plural as words, sourceLabel, whenDate, h, state, tiers } from './core.js';

/* Why a prompt got its tier, in words. The enum value stays in the tooltip (it is what headers, logs and exports say). */
export const REASONS = {
  SemanticMatch: 'matched a route',
  NoMatchFallback: 'no route matched → fallback tier',
  ComplexityEscalation: 'raised for complexity',
  AmbiguousMatchEscalation: 'two routes close → higher tier',
  ComplexityShortCircuit: 'complex → tier from complexity alone',
  EmptyPrompt: 'empty prompt → fallback tier',
  ClassifierFailure: 'classification failed → fallback tier',
  ModelOverride: 'model asked for by name',
  ConversationAffinity: 'tool turn → same model',
  RetryEscalation: 'sent again → raised a tier',
  ConversationFloor: 'kept at the conversation\'s tier',
  ExplicitCallout: 'asked for with a callout',
  ClientHousekeeping: 'client housekeeping → small model',
  ResumedSession: 'resumed session',
  RateLimited: 'over its rate limit',
};
export const reasonLabel = (reason) => REASONS[reason] || String(reason || '');
export const reasonHtml = (reason) => `<span title="${h(`Reason: ${reason}`)}">${h(reasonLabel(reason))}</span>`;

/* Where the running configuration came from, in words: core.js's. */
export { sourceLabel };

/* Counts with thousands separators, and words that follow their number (core.js's plural, with the separators). */
export const count = (n) => Number(n || 0).toLocaleString();
export const plural = (n, one, many) => words(n, one, many).replace(/^\S+/, count(n));

/*
 * Money, one way everywhere: "$0" for nothing, "<$0.01" below a cent, cents above. precise keeps two significant digits
 * below a cent, where amounts are compared side by side (the prompt tester's estimate per model).
 */
export function usd(v, precise) {
  const n = Number(v || 0);
  if (n === 0) return '$0';
  if (Math.abs(n) < 0.01) return precise ? '$' + Number(n.toPrecision(2)) : (n < 0 ? '-' : '') + '<$0.01';
  return (n < 0 ? '-$' : '$') + Math.abs(n).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/* A time, with the date when it is not today: core.js's. */
export const stamp = whenDate;
export const fullStamp = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' }); };

/* A tier, the same way everywhere: "T2 · Coding", with its description in the tooltip. Running tiers first, so an
   activity entry shows the ladder it was routed on. */
export function tierChip(level) {
  const running = ((state.status && state.status.tiers) || []).find((t) => t.level === level);
  const draft = tiers().find((t) => t.Level === level);
  const name = running ? running.name : draft && draft.Name;
  const description = running ? running.description : draft && draft.Description;
  return `<span class="tier" title="${h(`Tier ${level}${description ? ': ' + description : ''}`)}">T${h(level)}${name ? ' · ' + h(name) : ''}</span>`;
}

/* "00:15:00" → "15 min"; "1.00:00:00" → "1 d". Empty when it is not a duration. */
export function duration(text) {
  const m = /^(?:(\d+)\.)?(\d+):(\d\d):(\d\d)(?:\.\d+)?$/.exec(String(text || '').trim());
  if (!m) return '';
  const days = +(m[1] || 0) + Math.floor(+m[2] / 24);
  const parts = [[days, 'd'], [+m[2] % 24, 'h'], [+m[3], 'min'], [+m[4], 's']].filter(([n]) => n > 0).map(([n, u]) => `${n} ${u}`);
  return parts.join(' ') || '0 s';
}
export const DURATION_PATTERN = '^\\d+(\\.\\d+)?:\\d\\d:\\d\\d$';
