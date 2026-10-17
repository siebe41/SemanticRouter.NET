#!/usr/bin/env python3
"""A stand-in OpenAI-compatible server for make-fixture.sh: answers every chat request with "ok" and token usage,
streams when asked, and answers the model `rate-limited` with a 429 so the router records a quota cooldown. Each
request's Authorization header is appended to the file named by the second argument.

Usage: stub-openai.py <port> <authorization-log>
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1])
AUTH_LOG = sys.argv[2]


class Stub(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _json(self, status, body, headers=()):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        for name, value in headers:
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path.rstrip("/").endswith("/models"):
            self._json(200, {"object": "list", "data": [{"id": "small-model", "object": "model"}, {"id": "big-model", "object": "model"}]})
        else:
            self._json(404, {"error": {"message": "not found"}})

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0") or 0)) or b"{}")
        with open(AUTH_LOG, "a", encoding="utf-8") as log:
            log.write(f"{self.path} {self.headers.get('Authorization', '')}\n")
        model = body.get("model", "")
        if model == "rate-limited":
            self._json(429, {"error": {"message": "Rate limit reached", "type": "rate_limit_error"}}, [("Retry-After", "600")])
            return
        if not self.path.rstrip("/").endswith("/chat/completions"):
            self._json(404, {"error": {"message": "not found"}})
            return
        usage = {"prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15}
        if body.get("stream"):
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            chunks = [
                {"id": "c", "object": "chat.completion.chunk", "created": 0, "model": model,
                 "choices": [{"index": 0, "delta": {"role": "assistant", "content": "ok"}, "finish_reason": None}]},
                {"id": "c", "object": "chat.completion.chunk", "created": 0, "model": model,
                 "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]},
                {"id": "c", "object": "chat.completion.chunk", "created": 0, "model": model, "choices": [], "usage": usage},
            ]
            for chunk in chunks:
                self.wfile.write(f"data: {json.dumps(chunk)}\n\n".encode())
            self.wfile.write(b"data: [DONE]\n\n")
            return
        self._json(200, {
            "id": "c", "object": "chat.completion", "created": 0, "model": model,
            "choices": [{"index": 0, "message": {"role": "assistant", "content": "ok"}, "finish_reason": "stop"}],
            "usage": usage,
        })


ThreadingHTTPServer(("127.0.0.1", PORT), Stub).serve_forever()
