/* Semantic Router dashboard: cloud models' list prices, looked up with POST api/prices/lookup (OpenRouter's public
   price list) and written into the draft. Local models are never looked up. */
import { api, state } from './core.js';

const isSubscription = (conn) => !!conn && (conn.Type === 'Cli' || conn.Type === 'Copilot');

/** What of a connector says whose price a model pays: its type, preset and host. */
const asked = (conn, model) => ({ model, type: (conn && conn.Type) || 'OpenAICompatible', preset: (conn && conn.Preset) || 'Custom', baseAddress: (conn && conn.BaseAddress) || null });

/** The list's prices for each of `models` (catalog entries or suggestions) on `connector`, in order; null where it has none. */
export async function lookUpPrices(connector, models) {
  const conn = (state.config.Connectors || {})[connector];
  const r = await api('POST', '/prices/lookup', { models: models.map((m) => asked(conn, m.Model || m.Name || '')) });
  return { source: r.source, problem: r.problem, prices: r.prices || [] };
}

/**
 * Writes `price` into `model`: a subscription's API prices (what its use is worth), anything else's own prices. A list
 * without a cache-write price means the provider charges the input price, which is what an empty field says. The
 * context window is filled in only when the model has none.
 */
export function applyPrice(model, price, conn) {
  if (isSubscription(conn)) {
    Object.assign(model, { ApiInputCostPerMillionTokens: price.input, ApiOutputCostPerMillionTokens: price.output, ApiCacheReadCostPerMillionTokens: price.cacheRead ?? null, ApiCacheWriteCostPerMillionTokens: price.cacheWrite ?? null });
  } else {
    Object.assign(model, { InputCostPerMillionTokens: price.input, OutputCostPerMillionTokens: price.output, CacheReadCostPerMillionTokens: price.cacheRead ?? null, CacheWriteCostPerMillionTokens: price.cacheWrite ?? null });
  }
  if (!(model.ContextWindow > 0) && price.contextWindow) model.ContextWindow = price.contextWindow;
}

/** "$3 / $15 per M (cache read $0.3, write $3.75)", for messages. */
export const describePrice = (p) => `$${p.input} / $${p.output} per M${p.cacheRead != null || p.cacheWrite != null ? ` (cache${p.cacheRead != null ? ` read $${p.cacheRead}` : ''}${p.cacheWrite != null ? `${p.cacheRead != null ? ',' : ''} write $${p.cacheWrite}` : ''})` : ''}`;
