#!/usr/bin/env python3
"""Builds real state in a running release of the router, through that release's own API, for make-fixture.sh:
a connector with an API key (moved to the secrets file), models, a budget and a gateway key in two configuration
changes, then chat and streaming traffic, a retried prompt (feedback), a 429 (a quota cooldown), a classification
and, where the release has them, an evaluation case and a tuning run. Writes fixture.json, which the upgrade tests
read, next to the configuration.

Usage: drive.py <router-url> <stub-url> <version> <configuration-directory>
"""
import json
import os
import sys
import urllib.error
import urllib.request

ROUTER, STUB, VERSION, CONFIG_DIR = sys.argv[1].rstrip("/"), sys.argv[2].rstrip("/"), sys.argv[3], sys.argv[4]
API_KEY = f"sk-fixture-{VERSION}"
GATEWAY_KEY = f"gw-fixture-{VERSION}"


def call(method, path, body=None, allow=(200,)):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(ROUTER + path, data=data, method=method, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            status, text = response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        status, text = error.code, error.read().decode()
    print(f"{method} {path} -> {status}", file=sys.stderr)
    if status not in allow:
        raise SystemExit(f"{method} {path} answered {status}: {text[:500]}")
    return status, (json.loads(text) if text.strip().startswith(("{", "[")) else text)


def change(edit):
    _, envelope = call("GET", "/router/api/config")
    config = envelope["config"]
    edit(config)
    call("PUT", "/router/api/config?persist=true", config)


def first(config):
    config["Connectors"]["stub"] = {"Type": "OpenAICompatible", "BaseAddress": STUB, "ApiKey": API_KEY}
    config["Models"] = [
        {"Name": "small", "Connector": "stub", "Model": "small-model", "Tier": 1,
         "InputCostPerMillionTokens": 0.15, "OutputCostPerMillionTokens": 0.6, "Capabilities": ["chat", "code"]},
        {"Name": "big", "Connector": "stub", "Model": "big-model", "Tier": 4,
         "InputCostPerMillionTokens": 3, "OutputCostPerMillionTokens": 15, "Capabilities": ["chat", "code", "reasoning", "tools"]},
        {"Name": "flaky", "Connector": "stub", "Model": "rate-limited", "Tier": 4,
         "InputCostPerMillionTokens": 3, "OutputCostPerMillionTokens": 15, "Priority": 1, "Capabilities": ["chat", "code"]},
    ]
    config.setdefault("Usage", {})["Budgets"] = [{"Name": "daily-big", "Model": "big", "Period": "Day", "MaxCostUsd": 5}]


def second(config):
    config["Middleware"]["GatewayKeys"] = {"fixture-client": GATEWAY_KEY}
    config["FallbackTier"] = 2


def chat(content, model="auto", stream=False, allow=(200,)):
    body = {"model": model, "messages": [{"role": "user", "content": content}]}
    if stream:
        body["stream"] = True
    return call("POST", "/v1/chat/completions", body, allow)


change(first)
change(second)

for prompt in ["hello, how are you?", "write a regex that matches email addresses",
               "design a scalable architecture for a multi-tenant SaaS platform"]:
    chat(prompt)
chat("summarize this text: the quick brown fox", stream=True)
chat("rename this variable everywhere")
chat("rename this variable everywhere")  # the same prompt again: a retry, recorded as feedback
chat("debug this race condition", model="flaky", allow=(200, 429, 502, 503))  # a 429: a quota cooldown

call("POST", "/router/api/classify", {"prompt": "what does this git command do"}, allow=(200, 400, 404))
status, _ = call("POST", "/router/api/evaluation/cases", {"prompt": "hi there", "tier": 1}, allow=(200, 201, 400, 404, 405))
call("POST", "/router/api/tuning/run", {}, allow=(200, 202, 400, 404, 405, 409))

_, usage = call("GET", "/router/api/usage")
with open(os.path.join(CONFIG_DIR, "fixture.json"), "w", encoding="utf-8") as manifest:
    json.dump({"version": VERSION, "connector": "stub", "apiKey": API_KEY, "gatewayKey": GATEWAY_KEY,
               "gatewayClient": "fixture-client", "usage": usage.get("totals", usage) if isinstance(usage, dict) else None},
              manifest, indent=2)
