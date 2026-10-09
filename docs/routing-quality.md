# Tuning routing from evidence

Routing quality depends on route utterances and on a similarity threshold whose right value changes with the embedding model. This guide shows how to measure how well your configuration routes your own prompts, and how to improve it. Everything runs on the dashboard's **Evaluate** page or through the admin API. No model is called; only the embedding provider is.

## Evaluation sets

An evaluation set is a list of prompts with the tier each should get. A case can also name the route it should match and mark the prompt as local-only. The format is JSON Lines, one case per line:

```jsonl
# Lines starting with # or // are comments.
{"prompt":"summarize this report in two sentences","tier":1,"route":"rewrite-summarize"}
{"prompt":"debug this deadlock in my async code","tier":4,"route":"complex-code"}
{"prompt":"what is the capital of Australia?","tier":3}
{"prompt":"draft a reply to the patient about their results","tier":1,"localOnly":true}
```

A JSON array of the same objects works too, as does `{"cases": [...]}`. Use the prompt as the classifier sees it: the latest user message, without the system prompt.

- **Where it lives.** `semantic-router init` writes a sample set for the starting routes as `semantic-router.evaluation.jsonl` next to `semantic-router.json`. A library host keeps the set next to its configuration file (`UseConfigurationFile`), or wherever `UseEvaluationSetFile(path)` says. Without either, the set lives in memory.
- **Growing it from real traffic.** On the **Activity** page, **Label** opens a form for any entry. Pick the tier the prompt should have had (and optionally its route). Then add it to the set, add it as an utterance to a route (an existing one, or a new one created with that tier), or both. Adding an utterance applies a new configuration version immediately, so the next prompt like it routes differently without a restart.
- **Privacy.** Labels keep the local-only flag of the activity entry. **Download** (`GET api/evaluation/set/export`) leaves local-only prompts out unless you add `?localOnly=true`. The activity log keeps only the first 160 characters of a prompt, so check the text in the label form.

## Running a set

**Run** classifies every prompt with the running configuration and reports:

- **Accuracy:** prompts routed to their tier. A local-only prompt also has to stay local to count.
- **Too low** and **too high**, counted separately. A prompt routed too low gets a weaker model than it needs, which costs quality. One routed too high gets a stronger model than it needs, which costs money.
- **Accuracy per expected tier**, and a **confusion matrix**: rows are the expected tiers, columns the tiers given. Below the diagonal is too low, above it too high.
- **Route accuracy**, for cases that name a route.
- **Misrouted prompts**, each with the reason (`NoMatchFallback`, `AmbiguousMatchEscalation`, …) and the best route scores behind the decision. **Add to route…** adds the prompt as an utterance and runs the set again.

The evaluator decides exactly as the router does, with `DefaultRouteSelector`, the complexity rules and the short-circuit. A custom `IRouteSelector` or `IPromptClassifier` is not used here.

## Calibrating thresholds

**Run and calibrate** also sweeps `DefaultSimilarityThreshold` from 0 to 1 in steps of 0.01, plus the current value. The prompts are embedded once, so the sweep costs no further embedding calls. The chart shows prompts routed too low and too high at each threshold. Raising the threshold sends more prompts to the fallback tier; lowering it lets weak matches through.

- **The recommendation** is the threshold that routes the most prompts correctly. Ties go to fewer prompts routed too low (quality first), then to the value closest to the current one. So the recommendation is never worse than the current configuration on the set.
- **Route thresholds.** Each route's own `SimilarityThreshold` is then tried, with the others held fixed, and suggested only where it strictly improves on the result.
- **Use recommended values** puts them in the draft configuration. Apply as usual.

A small set overfits. Treat a large change recommended by a handful of prompts with suspicion: add prompts first.

## Route health

**Check routes** (`GET api/routes/health`) embeds every utterance and reports:

| Finding | Meaning | What to do |
|---|---|---|
| Few utterances | Fewer than five examples. | Add varied phrasings. |
| Duplicate | Two utterances of a route are near-identical (cosine ≥ 0.97). | Replace one with a different phrasing. |
| Conflict | An utterance is near-identical to one of another route. | Keep it in one route. |
| Overlap | At least half of a route's utterances match another route almost as well as their own (within `AmbiguityMargin`, both above their thresholds). Ambiguity escalation would fire on most prompts like them. Only reported when the routes differ in tier, local-only flag or capabilities. | Make the utterances more distinct, or merge the routes. |

The closest route pairs (centroid similarity and the share of ambiguous utterances) are listed below the findings.

## Comparing embedding models

**Compare embedding models** runs the set with another provider, such as Ollama's `nomic-embed-text` against the in-process hashing provider. Each side is shown at the current threshold and at its own recommended threshold. Nothing is applied. The API key can be a `${env:…}` or `${secret:…}` reference; `***` means the current key. This makes an outbound call to the endpoint you give, so it needs the admin role.

## Conversations

The classifier looks at the latest message, but a conversation is more than that. In the middle of a task, "ok, go ahead" or "continue" looks like small talk on its own, and would hand the task to a small model. With `Middleware:Conversation` (on by default) the router routes by the conversation:

- **A floor.** It remembers the highest tier a conversation needed recently, and a new turn needs at least that. The header and the activity entry show `ConversationFloor` when the floor raised the tier.
- **Short follow-ups keep the full floor.** A message of up to `ShortFollowUpLength` characters (default 60, without code blocks), such as "yes", "try again" or "looks good, commit it", is not classified on its own.
- **It decays.** Every `DecayTurns` turns (default 3) that need less than the conversation's peak lower the floor by one tier, so a conversation that moved on to simpler work gets cheaper models again. A harder turn raises the peak at once. After `IdleTimeout` (default 30 minutes) without a turn, the conversation is forgotten.
- **It sticks to a model.** While the model that answered the last turn is available and exactly on the required tier, it is tried first (`X-Prompt-Route-Sticky: true`, and **sticky** on the Activity page): answers stay coherent and the provider's prompt cache keeps working.

What it leaves alone:

- **A conversation's first turn** routes exactly as before. A request continues a conversation only when it carries an earlier answer, so two conversations that open with the same message do not share anything.
- **Requests for a specific catalog model** and **tool-result turns** (which stay on the model that issued the calls).
- **A client's own cheap side tasks.** Claude Code sends titles, summaries and other housekeeping under a Haiku model name, which `ModelAliases` maps to `auto:cheapest`; those are not raised.

A conversation is recognised by a hash of the client, the system prompt and the first user message, which chat clients resend on every turn. No prompt text is kept, the state is in memory, and the activity log shows only a short prefix of the hash. Clients that send only the newest message (relying on a server-side history) start a new conversation on each turn, so they are routed per message as before.

```jsonc
"Middleware": { "Conversation": { "Enabled": true, "DecayTurns": 3, "IdleTimeout": "00:30:00", "ShortFollowUpLength": 60, "StickToModel": true } }
```

## Retry escalation

When a client sends the same prompt again shortly after it was answered, the answer was often not good enough. With `Middleware:RetryEscalation:Enabled`, such a retry goes to a model at least one tier above the one that answered. The header and the activity entry show `RetryEscalation` as the reason.

```jsonc
"Middleware": { "RetryEscalation": { "Enabled": true, "Window": "00:02:00" } }
```

- The router keeps a hash of the client and the prompt, in memory, never the prompt text.
- Requests that carry tool results (tool loops resend the same user message) and requests for a specific catalog model are never escalated.
- A prompt answered by the highest-tier enabled model is not escalated further.

It is off by default: a client that legitimately repeats a prompt (a "regenerate" button, a script) pays for a stronger model.

## API

| Method and path | Purpose |
|---|---|
| `GET api/evaluation/set` | The stored set: `location`, `count`, `cases`. |
| `PUT api/evaluation/set` | Replace the set. Body: an array of cases, JSON Lines, or `{"text": "…"}`. |
| `POST api/evaluation/cases` | Add a case (`{prompt, tier, route?, localOnly?, note?}`). A prompt labelled again replaces its earlier label. |
| `GET api/evaluation/set/export?localOnly=false` | Download the set as JSON Lines, without local-only prompts unless asked. |
| `POST api/evaluate?calibrate=true` | Evaluate the body's set, or the stored set when the body is empty. |
| `POST api/evaluate/compare` | `{"embeddings": {provider settings}, "text": optional set}`: both providers' reports and calibrations. |
| `GET api/routes/health` | Findings and route pairs. |
| `POST api/routes/{name}/utterances` | `{"utterance": "…", "tier": n}`: add an utterance (creating the route when `tier` is given) and apply. Returns how the utterance now classifies. |

Changing the set, adding utterances and comparing providers need the admin role. Evaluating and route health are open to the read-only role.

From code, the same pieces are in `SemanticRouter.Core`: `EvaluationSet.Parse`, `RoutingEvaluator` (`PrepareAsync`, `Evaluate`, `Calibrate`) and `RouteHealthAnalyzer`.

```csharp
var evaluator = new RoutingEvaluator(embeddingProvider, options);
PreparedEvaluation prepared = await evaluator.PrepareAsync(EvaluationSet.Parse(File.ReadAllText("eval.jsonl")));
EvaluationReport report = evaluator.Evaluate(prepared);
CalibrationReport calibration = evaluator.Calibrate(prepared);
Console.WriteLine($"{report.Accuracy:P1}; best threshold {calibration.Recommended.Threshold}");
```
