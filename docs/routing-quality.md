# Tuning routing from evidence

Routing quality depends on route utterances and on a similarity threshold whose right value changes with the embedding model. This guide shows how to measure how well your configuration routes your own prompts, and how to improve it. Everything runs on the dashboard's **Evaluate** page or through the admin API. No model is called; only the embedding provider is.

## The starting routes

`semantic-router init` and the built-in template start with these routes. A prompt that matches none of them gets `FallbackTier` (3).

| Route | Tier | For |
|---|---|---|
| `smalltalk` | 1 | Greetings, thanks, questions about what the assistant can do. |
| `rewrite-summarize` | 1 | Summaries, rewrites, translation, spelling. |
| `agent-housekeeping` | 1 | An agent's own side requests: titles, transcript summaries, command descriptions. |
| `simple-code` | 2 | Small, self-contained code tasks: a regex, a unit test, a docstring, null checks, extract a method, a loop to LINQ, a class to a record, a method to async. Needs `code`. |
| `quick-answers` | 2 | Short factual and explain questions: what a status code, error code, flag, option or pattern means, defaults and ports, date formats. |
| `continuation` | 3 | "go ahead", "continue": in a conversation they keep its tier (`FollowsConversation`). |
| `deep-reasoning` | 4 | Design, trade-offs, risk analysis, proofs. |
| `complex-code` | 4 | Debugging, leaks, deadlocks, performance, reviews. Needs `code`. |
| `agentic-coding` | 4 | Multi-step work across files: implement, migrate, fix the build. Needs `code`. |

`quick-answers` is tier 2, not 1, because in an agentic client a question about your own system ("what time zone does the server use?") is answered with a tool call. The request then carries tools, which adds the `tools` capability, and tier 2 is where `init` puts the small models that call tools well (Claude Haiku, a local coder model). General-knowledge questions ("what is the capital of Australia?", "how does compound interest work?") are not in it and stay on the fallback tier.

The sample evaluation set that `init` writes (116 prompts) routes 111 to their tier with all-MiniLM-L6-v2 and 112 with bge-small-en-v1.5, each at the threshold `init` sets for it; CI fails below 90%. It includes guard prompts for hard work that looks like a small request ("add retry with backoff to every service client and make the tests pass", "explain why this deadlock happens under load"), so new utterances that pull such work down a tier show up as misses.

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

The evaluator decides exactly as the router does, with `DefaultRouteSelector`, the complexity rules and the short-circuit, and embeds the same text: the prose of each prompt, without pasted code (`EmbedProseOnly`). A custom `IRouteSelector` or `IPromptClassifier` is not used here.

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
- **It decays.** Every `DecayTurns` turns (default 3) that need less than the conversation's peak lower the floor by one tier, so a conversation that moved on to simpler work gets cheaper models again. A harder turn raises the peak at once. After `IdleTimeout` (default 30 minutes) without a turn, the conversation is forgotten. The router remembers at most 32,768 conversations, in memory; beyond that the least recently active are forgotten first.
- **It sticks to a model.** While the model that answered the last turn is available and exactly on the required tier, it is tried first (`X-Prompt-Route-Sticky: true`, and **sticky** on the Activity page): answers stay coherent and the provider's prompt cache keeps working.
- **It weighs the warm prompt cache.** Caches are per model, so moving a conversation sends its whole context again and writes it to the new model's cache. After each turn the router notes how much of the context the serving model reported reading or writing from its cache, and keeps it warm for as long as the provider keeps the entries, refreshed by every turn that uses it. On an Anthropic connector (`Type: Anthropic`) the router knows that from the cache markers: the connector's `CacheTtl` (`5m` or `1h`) for the markers it adds, or the longest `ttl` among an Anthropic client's own markers. For other backends it is `CacheLifetime` (default 5 minutes). On the next turn it compares two costs, at the models' configured prices: staying, which reads the cached part at the warm model's cache-read price and sends the rest at its input price, and moving to the ranking's first choice, which sends the whole context at its cache-write price. It stays when that is cheaper, also on a model above the required tier, and moves when it is not, for example down to a much cheaper, local or flat-rate model. A model below the required tier is never kept. After a failover it goes back to the warm model when that comes out of its cooldown before the cache expires. The reason shows on the Activity page and in `X-Prompt-Route-Selection`, e.g. `cache: stayed on opus (80k cached); moving to sonnet would cost $0.252 more`. Without a warm cache (expired, or a backend that reports no cache use) the rule above applies.

What it leaves alone:

- **A conversation's first turn** routes exactly as before. A request continues a conversation only when it carries an earlier answer, so two conversations that open with the same message do not share anything.
- **Requests for a specific catalog model** and **tool-result turns** (which stay on the model that issued the calls).
- **A client's own cheap side tasks.** Claude Code sends titles, summaries and other housekeeping under a Haiku model name, which `ModelAliases` maps to `auto:cheapest`; those are not raised.

A conversation is recognised by a hash of the client, the system prompt and the first user message, which chat clients resend on every turn. Only the system and developer messages before the first user message count as the system prompt: Claude Code adds system messages later in the conversation, on every turn and around tool calls, and those must not start a new conversation each time. A client that names its session in a header is recognised by the client, the session id and the first user message instead (`SessionHeader`, default `x-claude-code-session-id`, which Claude Code sends on every request), so a system prompt that changes during the session does not split it either. The first user message stays in that key because a session can hold more than one conversation (Claude Code's subagents open with their own task): each is a conversation of its own, and its first turn does not reset the main one. No prompt text is kept, the state is in memory (warm caches are also kept in the SQLite state, so they survive a restart), and the activity log shows only a short prefix of the hash. Clients that send only the newest message (relying on a server-side history) start a new conversation on each turn, so they are routed per message as before.

```jsonc
"Middleware": { "Conversation": { "Enabled": true, "DecayTurns": 3, "IdleTimeout": "00:30:00", "ShortFollowUpLength": 60, "StickToModel": true, "CacheLifetime": "00:05:00", "SessionHeader": "x-claude-code-session-id" } }
```

## Feedback

Labels on the Evaluate page improve routing only when someone adds them. The router also notices on its own when a decision was probably not good enough, and records that with the decision (`Middleware:Feedback`, on by default):

| Feedback | Noticed when | About |
|---|---|---|
| **Retried** | The same client sends the same prompt again in the same conversation within `RetryEscalation:Window` (2 minutes) of the answer, before the conversation moved on, whether or not retry escalation is on. Only the first repeat counts: a prompt sent again and again is a script or a monitor. The same prompt in another conversation, and a short follow-up said again ("continue"), are not retries. The router remembers the latest 16,384 answers for this. | The answer. |
| **Corrected** | The next message in the conversation reads as a correction: "that's wrong", "no, I meant…", "it still doesn't compile", "you forgot…", "try again", or a short message saying it is still failing, in English, Dutch, German, French or Spanish. A short follow-up in other words or another language is compared by meaning ([below](#corrections-in-other-words)). | The previous turn. |
| **Overridden** | The next message chooses a model or tier with a [callout](clients.md#telling-the-router-what-you-want). | The previous turn. |
| **Tool errors** | Tool results come back marked as errors on two tool turns in a row (the Anthropic protocol carries the flag), or the model writes malformed tool calls. | The turn running the task, or the turn itself. |
| **Empty reply** | The model answers with no output tokens. | The turn itself. |
| **Graded low** | The optional [judge](#the-judge) grades the answer `FailingGrade` (2) or lower. | The turn itself. |

- Only decisions the router made are judged. A turn for which you chose the model (a callout, a catalog model name) is not the router's mistake.
- Each feedback item carries an automatic label: the decision needed at least one tier more (for an override, the tier you asked for). [Automatic tuning](#automatic-tuning) learns from these.
- **Where to see it:** badges on the Activity page next to each decision, and a Feedback table on the Usage page with the share of decisions that received feedback, per route and per model. A route or model with a high share is where routing goes wrong.
- No prompt text is kept: feedback records the decision, route, model, tier and a short reason, in memory and in the state database when there is one (kept as long as activity).

### Corrections in other words

Phrases catch the common corrections in five languages. For the rest, a short follow-up to an answer (up to 120 characters, not a question, no phrase matched) is compared with example corrections ("it still doesn't work", "dat klopt niet", "das ist falsch", "c'est faux", "eso está mal", and others in Italian and Portuguese) and with example next steps ("now add a test", "thanks, looks good"), using the routing embedding model. It counts as a correction when it is at least `Middleware:Feedback:CorrectionSimilarity` (0.72) close to a correction and `CorrectionMargin` (0.12) closer to the corrections than to the next steps. The check runs after the request has gone on, costs one embedding call per candidate (none for other messages), and its feedback says "(by meaning)".

The defaults were measured with the default ONNX model (all-MiniLM-L6-v2): no next step or question among the test messages counts, while short corrections the phrases miss ("klopt niet helemaal", "funktioniert nicht") do. A multilingual embedding model recognises more. Other models score similarity on their own scale: with an embeddings API, check a few of your own corrections and next steps before lowering the threshold. `CorrectionSimilarity: 0` turns the check off; it never runs with the lexical hashing embeddings.

## The judge

Retries and corrections only show the answers people pushed back on. An optional judge reads a small sample of cheap answers itself (`Middleware:Judge`, off by default):

```json
"Middleware": { "Judge": { "Enabled": true, "Model": "qwen3-32b" } }
```

- **What is graded:** `SampleRate` (2%) of the answers from models of tier `MaxTier` (2) or below, to decisions the router made itself. Not explicit choices, client side requests, tool-result turns, or answers that only call tools. At most `DailyLimit` (100) a day (UTC), counted across restarts when there is a state database.
- **How:** after the client has its reply, `Model` (a catalog model) gets the request and the answer, each cut to `MaxCharacters` (6000), and grades it from 1 (wrong) to 5 (excellent). Grading runs in the background, one answer at a time, so it never slows a reply.
- **What a low grade does:** a grade of `FailingGrade` (2) or lower is **graded low** feedback: the decision needed one tier more. [Automatic tuning](#automatic-tuning) counts such a prompt at `Tuning:JudgeWeight` (0.75), between implicit feedback (0.5) and your own labels (1), and [model reliability](#model-reliability) counts it as a failed turn.
- **Privacy:** a local-only request is never sent to a judge that is not local. With a local judge, nothing leaves the machine. A cloud judge does see the sampled requests and answers that are not local-only. A remote client's answers are not graded by a judge on a subscription CLI or Copilot connector unless that connector has `AllowRemoteClients: true`, just as the connector would not serve the client itself.
- **Cost:** the judge's calls are served like any other: budgets apply, and they show on the Usage page under route and client `(judge)`. A local judge costs nothing.
- **Where you see it:** a **graded 4/5** badge on the Activity entry, with the judge's reason in its tooltip; the API has it as `grade` and `gradeNote`. The reason is kept as `State:StorePrompts` allows: masked with `Redacted`, not at all with `Off` ([What the router stores](host.md#what-the-router-stores)). The Activity page says when the judge is on.
- **Whether to trust it:** when a graded answer's prompt is also in the evaluation set, the two are compared. A low grade agrees when your label is above the tier of the model that answered; a good grade agrees when your label is at or below it. Label graded prompts from the Activity page (or add them to the set with the same wording). The **Judge agreement** card on the Evaluate page, and `GET api/judge/agreement`, show how many agree, split by low and good grades, with the latest pairs. Once `Tuning:JudgeAgreementPairs` (10) answers have a label, [automatic tuning](#automatic-tuning) counts the judge's labels at `JudgeWeight` times the agreement: a judge that agrees with half your labels counts half as much, and one that agrees with none is ignored. The judge keeps a hash of each prompt it grades to find its label (no more of the prompt than the Activity preview is kept).

Pick a judge stronger than the models it grades; grading its own answers is skipped.

## Automatic tuning

Calibration finds better thresholds, but someone has to run it and apply the result. Automatic tuning does that by itself, in small steps, and shows every change it makes so you can undo it (`Tuning`, on by default):

- **What it learns from:** the evaluation set (prompts you labelled), and prompts labelled by [feedback](#feedback) in the last `FeedbackDays` (30). A prompt labelled by feedback counts at `FeedbackWeight` (0.5) of a hand-labelled one; one labelled only by the [judge](#the-judge) at `JudgeWeight` (0.75), scaled by how often its grades agree with your labels. Only activity previews that hold the whole prompt (160 characters or fewer) become labels, and a prompt in the evaluation set keeps your label.
- **Thresholds:** it calibrates the default threshold and each route's own threshold, then moves each one at most `MaxStep` (0.02) per run toward that value, so a big correction takes several days. A route that follows the default stays on it unless its own threshold helps.
- **Utterances:** a prompt in the evaluation set that is labelled with a route but misses it is added to that route as an example, at most `MaxUtterancesPerRoute` (2) per route per run. Prompts labelled local-only are never added unless `LocalOnlyUtterances` is on, because utterances are part of the configuration.
- **Only when it is safe:**
  - no hand-labelled prompt may route worse;
  - no labelled prompt may route below the tier it needs;
  - no local-only prompt may leave the machine;
  - and it starts only once there are `MinimumPrompts` (20) labelled prompts.

  An utterance must also leave every other labelled prompt routing as well as before.
- **When:** every `Interval` (a day), and early once `NewLabels` (10) prompts were labelled since the last run. **Run now** on the dashboard runs it at once.
- **Every change is its own configuration version** (source `auto-tune`), so the configuration history shows it with a diff and the reason, and rollback works as for any version.

### Cheaper models per route

A route's tier picks its model, but the work some routes see is easier than their tier: a cheaper model may do it as well. Tuning finds out from real turns, not labels:

1. **A trial starts** on a route with at least `Middleware:Reliability:MinimumTurns` (10) plain matches from one model in the last `FeedbackDays`, when a cheaper enabled model can do the route's work (its capabilities, and local when the route is local-only). It tries the next cheaper one, one step down, not straight to the cheapest. Cheaper means a typical turn (1,000 input and 500 output tokens) costs less with the prompt cache counted: input each model read from its cache on the route's recent turns is priced at its cache price (see [choosing a model](../README.md#step-2-choosing-a-model)). A model with cheap cache reads is not traded for one that only lists a lower input price; a model without enough turns is compared at its list prices. Router learned lists it as **on trial**, with why: "mid (tier 3) costs less than cloud (tier 4), which served agentic: 2 of 40 turns drew feedback (2 retried). 5% of the route's plain matches try it, up to 20 turns".
2. **During the trial**, `ModelTrialRate` (5%) of the route's plain matches go to the cheaper model first, with the usual models behind it, until it has `ModelTrialTurns` (20). Only the first turn of a conversation the router decided itself: never an explicit choice, a callout, a retry or other escalation, a follow-up, a tool result or a client's own request. A local-only request never goes to a cloud model on trial. Activity marks these turns **trial**.
3. **The trial is judged** at the next run after it has its turns. Feedback that says a turn was not good enough (retried, corrected, tool errors, empty reply, graded low) is counted on both models' turns. The cheaper model takes the route over when its share is at most `ModelTrialMargin` (5 points) above the current model's: the route gets `PreferredModel` in a new configuration version, and its plain matches go to that model first from then on, even below the route's tier. Otherwise it is **dropped**. Either way the evidence is in the reason: "mid: 1 of 20 trial turns drew feedback (1 corrected); cloud: 2 of 40 turns drew feedback".

**Undo** puts the route back on its tier's choice; **Stop trial** ends a running trial. A dropped, stopped or undone model is not tried on that route again for `HoldDays`. Trials run only when `Tuning:Mode` is `Apply` and learning is not paused; `ModelTrialRate: 0` turns them off. A route's `PreferredModel` can also be set by hand; turns that need more (an escalation, an explicit choice, a conversation that needs its tier) are routed by the tier as usual.

### Where you see it

- **Router learned** (its own dashboard page, and a card on the Overview) lists each change with:
  - what changed: the setting, the route, and the old and new value or the utterance;
  - why: for example "At 0.48, 9 labelled prompts route to the tier they need that do not now (3 from the evaluation set, 6 from feedback: retried, corrected)";
  - when, and the configuration version that applied it;
  - its effect since: the share of decisions on what it touches that received feedback, before and after, and how many decisions it routed differently.
- **On a decision:** an Activity entry that a learned threshold routed differently (it matched only because the threshold was lowered, or the reverse) carries a **learned** badge, with "learned 3 Oct: threshold of complex-code 0.52 → 0.48" in its tooltip. The API has it as `learned` and `learnedNote`.
- **In the desktop app:** the tray menu shows "Router learned 2 things this week" and opens the page.

### Undo

- **Undo** puts back that one setting, not everything after it: later changes to other settings stay. Later steps of the same threshold go with it, since they built on it. If someone changed the setting since, undo refuses rather than overwrite that.
- **An undone change is not made again** for `HoldDays` (30). Within that time it comes back only if the evidence is `StrongerEvidence` (2) times stronger, and then only as a suggestion. After the hold it also waits on the page as a suggestion rather than being applied. When the default threshold is held, the routes that follow it are left alone too, so the same effect does not return by another setting.
- **Undo all** puts back every learned change in one configuration version, and **Pause learning** stops it until you resume. Each is one click.
- `Tuning:Mode`: `Apply` (the default), `Suggest` (changes wait on the page for **Apply** or **Dismiss**; a dismissed one is held like an undone one) or `Off`.

```jsonc
"Tuning": { "Mode": "Apply", "Interval": "1.00:00:00", "MaxStep": 0.02, "MinimumPrompts": 20, "HoldDays": 30 }
```

Changes and state are kept in the state database when there is one (the tool has one by default), otherwise in memory.

## Model reliability

A model's tier says how capable it is, not how it does on your work. A small local model can be fine for chat and keep failing at agentic coding (malformed tool calls, empty replies). The router keeps score from [feedback](#feedback) and stops sending a model the work it keeps failing at (`Middleware:Reliability`, on by default):

- **What counts:**
  - each turn a model answered for the router (not explicit choices), per route and for turns with tools;
  - a turn counts as failed when it drew feedback: retried, corrected, tool errors, an empty reply or graded low by the [judge](#the-judge). Feedback on a tool-result turn counts against the turn that started the task.
  - It counts over the last `Window` (7 days).
- **When a model is left out of some work:** all of these hold:
  - it has at least `MinimumTurns` (10) turns on that work;
  - at least `MaxFailureRate` (30%) of them drew feedback;
  - it is `PeerMargin` (15 points) worse than the other models with enough turns on the same work, when there are any.
- **What happens then:**
  - it is skipped for that route (or for requests with tools) like a cooled-down model, for `Period` (1 day);
  - it still serves its other work;
  - it is used anyway when no other model could serve the request.
- **Afterwards:** it is offered again with a fresh count, so a model that improves (or a fixed prompt template) comes back.
- **Never:** a model is never left out of every route it can serve, and a model you chose (a catalog name, a callout, a tool loop finishing on its model) is never skipped.
- **Where you see it:**
  - **Models page:** a Reliability card with each model's share of turns with feedback per route and with tools, the models left out ("left out until …: 7 of 20 turns drew feedback (5 with tool errors, 2 retried); other models 10%") and an **Offer again** button. The model's row says "not used for route agentic-coding".
  - **Activity:** a decision that skipped a model carries "skipped *model*".
  - **Response header:** `X-Prompt-Route-Unreliable`.

The figures are kept in memory and recomputed from the activity log at most once a minute; after a restart they are computed again from the stored activity.

## Retry escalation

When a client sends the same prompt again shortly after it was answered, the answer was often not good enough. With `Middleware:RetryEscalation:Enabled`, such a retry goes to a model at least one tier above the one that answered. The header and the activity entry show `RetryEscalation` as the reason.

```jsonc
"Middleware": { "RetryEscalation": { "Enabled": true, "Window": "00:02:00" } }
```

- A retry is the same prompt from the same client in the same conversation, before another turn of it was answered; a short follow-up (up to `Conversation:ShortFollowUpLength` characters) after the first turn is not one. A prompt that keeps coming back escalates each time, but gives feedback only once.
- The router keeps a hash of the client, the conversation and the prompt, in memory, never the prompt text.
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
| `GET api/tuning` | Automatic tuning: `mode`, `paused`, `lastRun`, `nextRun`, `lastResult`, `thisWeek`, `suggestions`, and the `changes` newest first (each with `summary`, `why`, `examples`, `state`, `version` and `effect`). |
| `GET api/tuning/changes?limit=100` | The changes only. |
| `POST api/tuning/run` | Run tuning now; returns what it did. |
| `POST api/tuning/changes/{id}/undo` | Undo one change (409 when it is not applied, or its setting changed since). |
| `POST api/tuning/changes/{id}/apply`, `…/dismiss` | Apply or turn down a suggestion. |
| `POST api/tuning/undo-all` | Undo every applied change in one configuration version. |
| `POST api/tuning/pause`, `POST api/tuning/resume` | Pause or resume learning. |
| `GET api/reliability` | Each model's turns and share with feedback per route and with tools, the share of the other models on the same work, and the models left out (`excludedSince`, `excludedUntil`, `detail`). |
| `POST api/reliability/reset` | `{"model": "…", "route": "…"}` (or `"capability": "tools"`, or neither for everything): offer the model again with a fresh count. |

Changing the set, adding utterances, evaluating and comparing providers (which embed every prompt in the set), route health (which embeds every utterance) every tuning action (run, undo, apply, dismiss, pause) and resetting a model's reliability count need the admin role. Reading what tuning learned and the reliability figures is open to the read-only role.

From code, the same pieces are in `SemanticRouter.Core`: `EvaluationSet.Parse`, `RoutingEvaluator` (`PrepareAsync`, `Evaluate`, `Calibrate`), `ThresholdTuner` (the bounded, guarded steps automatic tuning takes) and `RouteHealthAnalyzer`.

```csharp
var evaluator = new RoutingEvaluator(embeddingProvider, options);
PreparedEvaluation prepared = await evaluator.PrepareAsync(EvaluationSet.Parse(File.ReadAllText("eval.jsonl")));
EvaluationReport report = evaluator.Evaluate(prepared);
CalibrationReport calibration = evaluator.Calibrate(prepared);
Console.WriteLine($"{report.Accuracy:P1}; best threshold {calibration.Recommended.Threshold}");
```
