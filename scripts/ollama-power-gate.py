#!/usr/bin/env python3
"""
Power-aware HTTP reverse proxy for Ollama on macOS.

Listens on 127.0.0.1:11435 (intended to be exposed via Tailscale Serve on port 11434)
and forwards requests to local Ollama at 127.0.0.1:11434 ONLY when the Mac is on
AC Power and not in Low Power Mode.

When on Battery Power or Low Power Mode, returns HTTP 503 Service Unavailable so
GwsBusinessSuite's OllamaFailoverMonitor cleanly fails over to the AI droplet.
Local Mac apps talking directly to 127.0.0.1:11434 remain unaffected.
"""

import http.client
import http.server
import json
import os
import subprocess
import threading
import time

LISTEN_HOST = os.environ.get("OLLAMA_GATE_HOST", "127.0.0.1")
LISTEN_PORT = int(os.environ.get("OLLAMA_GATE_PORT", "11435"))
UPSTREAM_HOST = os.environ.get("OLLAMA_UPSTREAM_HOST", "127.0.0.1")
UPSTREAM_PORT = int(os.environ.get("OLLAMA_UPSTREAM_PORT", "11434"))
POWER_CACHE_SECONDS = 5.0

_power_lock = threading.Lock()
_last_check = 0.0
_allow_serving = True
_reason = "AC Power"


def check_power_state() -> tuple[bool, str]:
    global _last_check, _allow_serving, _reason
    now = time.monotonic()
    with _power_lock:
        if now - _last_check < POWER_CACHE_SECONDS:
            return _allow_serving, _reason

    allow = True
    reason = "AC Power"
    try:
        batt = subprocess.check_output(["pmset", "-g", "batt"], text=True, timeout=2)
        if "AC Power" not in batt:
            allow = False
            reason = "Battery Power"
        else:
            pm = subprocess.check_output(["pmset", "-g"], text=True, timeout=2)
            for line in pm.splitlines():
                if "lowpowermode" in line and line.strip().endswith("1"):
                    allow = False
                    reason = "Low Power Mode"
                    break
    except Exception:
        allow = True
        reason = "Unknown (fail-open)"

    with _power_lock:
        _last_check = now
        _allow_serving = allow
        _reason = reason
    return allow, reason


def _read_request_body(rfile, headers) -> bytes | None:
    te = headers.get("Transfer-Encoding", "").lower()
    if "chunked" in te:
        chunks = []
        while True:
            line = rfile.readline().strip()
            if not line:
                break
            chunk_size = int(line.split(b";", 1)[0], 16)
            if chunk_size == 0:
                while True:
                    trailer = rfile.readline()
                    if not trailer or trailer in (b"\r\n", b"\n"):
                        break
                break
            chunks.append(rfile.read(chunk_size))
            rfile.read(2)
        return b"".join(chunks)
    content_length = int(headers.get("Content-Length", "0") or "0")
    return rfile.read(content_length) if content_length > 0 else None


class PowerGateHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):
        # Quiet unless debugging
        pass

    def _handle_request(self):
        allow, reason = check_power_state()
        if not allow:
            body = json.dumps({"error": f"Mac Ollama gate paused ({reason}); failing over to AI droplet."}).encode("utf-8")
            self.send_response(503, "Service Unavailable")
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(body)
            return

        req_body = _read_request_body(self.rfile, self.headers)

        forward_headers = {}
        for k, v in self.headers.items():
            if k.lower() in ("host", "connection", "keep-alive", "transfer-encoding", "content-length"):
                continue
            forward_headers[k] = v
        if req_body is not None:
            forward_headers["Content-Length"] = str(len(req_body))

        conn = http.client.HTTPConnection(UPSTREAM_HOST, UPSTREAM_PORT, timeout=600)
        try:
            conn.request(self.command, self.path, body=req_body, headers=forward_headers)
            resp = conn.getresponse()

            self.send_response(resp.status, resp.reason)
            is_chunked = False
            for k, v in resp.getheaders():
                kl = k.lower()
                if kl in ("connection", "keep-alive", "transfer-encoding"):
                    if kl == "transfer-encoding" and "chunked" in v.lower():
                        is_chunked = True
                    continue
                self.send_header(k, v)
            self.send_header("Connection", "close")
            self.end_headers()

            while True:
                chunk = resp.read(8192)
                if not chunk:
                    break
                self.wfile.write(chunk)
                self.wfile.flush()
        except Exception as exc:
            err = json.dumps({"error": f"Upstream Ollama unreachable: {exc}"}).encode("utf-8")
            try:
                self.send_response(502, "Bad Gateway")
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(err)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(err)
            except Exception:
                pass
        finally:
            conn.close()

    def do_GET(self):
        self._handle_request()

    def do_POST(self):
        self._handle_request()

    def do_PUT(self):
        self._handle_request()

    def do_DELETE(self):
        self._handle_request()

    def do_HEAD(self):
        self._handle_request()


def main():
    server = http.server.ThreadingHTTPServer((LISTEN_HOST, LISTEN_PORT), PowerGateHandler)
    server.daemon_threads = True
    server.serve_forever()


if __name__ == "__main__":
    main()
