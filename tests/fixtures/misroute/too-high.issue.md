### What went wrong

Too high: a stronger model than needed (wasted quota or cost)

### The router's decision

```text
Reason: SemanticMatch
Tier: 4
Route: complex-code · similarity 0.548
Complexity: 0.048
Planned model: claude-code-opus
Served by: claude-code-opus · served
Request: Anthropic · client Claude Code · model auto · tools · ~1800 tokens
Activity entry: #812 at 2026-11-02T09:14:05Z
```

### Model that answered

claude-code-opus

### What the prompt really needed

Tier 2 (Local coder / mini). The pull request renames one variable; the local coder model reviews that fine.

### Embedding provider, model and threshold

Hashing, threshold 0.3

### Client

Claude Code

### Prompt, paraphrase or description

```text
review this pull request: it only renames one variable
```

### Router version

0.6.1

### Before you submit

- [X] This report contains no keys, tokens or private prompt text.
