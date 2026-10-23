#!/usr/bin/env python3
"""A mock of the parts of Jiwe that wallet-sdk-unity talks to, for automated tests.

It encodes what Jiwe's docs say and what our shipped games have seen live (see the README):
  * the token endpoint's client-authentication modes (Basic header / form body / public),
  * idempotency keys bound to the first request, including to a *failed* first request,
  * response envelopes ({"gameData": ...}, {"userData": ...}), uppercase "XP" on period boards,
  * X-API-TOKEN vs the OIDC bearer boundary (mutually exclusive), 401 vs 403,
  * fault injection (status codes, delays, dropped connections) and CORS for WebGL.

It proves the SDK conforms to OUR MODEL of Jiwe. It cannot prove Jiwe still behaves that way —
that is what the live smoke test (Tests~/live) is for.

Standard library only. Usage:  python3 mock_jiwe_server.py --port 8765
Control API (JSON):  POST /__reset  POST /__config  POST /__faults  GET /__log  POST /__report  GET /__reports
"""
import argparse
import base64
import copy
import hashlib
import json
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

LOCK = threading.RLock()


def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


def fresh_state():
    return {
        "config": {
            "token_auth": "basic",            # basic | body | public | either
            "consume_code_on_auth_failure": False,
            "single_use_codes": True,
            "client_id": "client-1",
            "client_secret": "secret-1",
            "api_username": "test-user",
            "api_key": "test-key",
            "token_delay": 0.0,
            "me_status": 200,
            "expires_in": 900,
            "expected_challenge": "",
            "claims": {"sub": "player-123", "name": "Test Player", "nickname": "tester",
                       "bio": "hello", "picture": "https://example.test/p.png", "walletId": "wallet-abc"},
            "debits": 100, "credits": 250.5, "balance": 150.5,
            "player_balance": 1000,
            "requires_approval": False,
            "settle_after_polls": 1,
            "bearer_scope_ok": True,
            "bearer_audience_ok": True,
            "earned": {"points": 1250, "XP": 8400, "airtimeEarned": 150, "airtimeCurrency": "KES"},
        },
        "log": [], "faults": [], "reports": [],
        "codes": {}, "used_codes": set(), "access_tokens": {}, "id_tokens": set(),
        "idem": {}, "purchases": {}, "txns": {}, "game_data": {}, "user_data": {}, "auth_seq": 0,
    }


S = fresh_state()


def cfg():
    return S["config"]


def deep_merge(dst, src):
    for k, v in src.items():
        if isinstance(v, dict) and isinstance(dst.get(k), dict):
            deep_merge(dst[k], v)
        else:
            dst[k] = v


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):  # silence
        pass

    # ---- plumbing -------------------------------------------------------
    def _read(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n).decode("utf-8", "replace") if n else ""

    def _cors(self):
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers",
                         self.headers.get("Access-Control-Request-Headers") or "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS")
        self.send_header("Access-Control-Expose-Headers", "*")

    def send_json(self, status, obj, extra=None):
        data = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self._cors()
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def send_raw(self, status, body, ctype="text/html"):
        data = body.encode() if isinstance(body, str) else body
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self._cors()
        self.end_headers()
        self.wfile.write(data)

    def err(self, status, message):
        self.send_json(status, {"type": "ERROR", "message": message})

    def hdr(self, name):
        return self.headers.get(name)

    # ---- dispatch -------------------------------------------------------
    def do_OPTIONS(self):
        self.send_response(204)
        self._cors()
        self.send_header("Access-Control-Max-Age", "600")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self): self._handle("GET")
    def do_POST(self): self._handle("POST")
    def do_PUT(self): self._handle("PUT")
    def do_DELETE(self): self._handle("DELETE")

    def _handle(self, method):
        url = urlparse(self.path)
        path, query = url.path, parse_qs(url.query)
        body = self._read()
        with LOCK:
            if path.startswith("/__"):
                return self._control(method, path, body)
            S["log"].append({
                "method": method, "path": path, "query": url.query, "body": body,
                "headers": "".join(f"{k.lower()}: {v}\n" for k, v in self.headers.items()),
            })
            fault = self._take_fault(path)
        if fault:
            if fault.get("delay"):
                time.sleep(float(fault["delay"]))
            if fault.get("drop"):
                self.close_connection = True
                try:
                    self.connection.shutdown(2)
                except OSError:
                    pass
                return
            if fault.get("status"):
                b = fault.get("body")
                if isinstance(b, str):
                    return self.send_raw(int(fault["status"]), b)
                return self.send_json(int(fault["status"]), b if b is not None else {"type": "ERROR", "message": "injected fault"})
        with LOCK:
            self._route(method, path, query, body)

    def _take_fault(self, path):
        for f in S["faults"]:
            if f["match"] in path and f.get("count", 1) != 0:
                if "count" in f:
                    f["count"] -= 1
                return dict(f)
        return None

    # ---- control --------------------------------------------------------
    def _control(self, method, path, body):
        global S
        data = json.loads(body) if body else {}
        if path == "/__reset":
            S = fresh_state()
            return self.send_json(200, {"ok": True})
        if path == "/__config":
            deep_merge(S["config"], data)
            return self.send_json(200, {"ok": True})
        if path == "/__faults":
            S["faults"] = data.get("faults", [])
            return self.send_json(200, {"ok": True})
        if path == "/__log":
            return self.send_json(200, {"items": S["log"]})
        if path == "/__report":
            S["reports"].append(data)
            return self.send_json(200, {"ok": True})
        if path == "/__reports":
            return self.send_json(200, {"items": S["reports"]})
        if path == "/__state":
            return self.send_json(200, {"player_balance": S["config"]["player_balance"], "purchase_count": len(S["purchases"]),
                                        "game_data": S["game_data"], "user_data": S["user_data"], "auth_seq": S["auth_seq"]})
        if path == "/__health":
            return self.send_json(200, {"ok": True})
        return self.send_json(404, {"error": "unknown control path"})

    # ---- identity -------------------------------------------------------
    def _route(self, method, path, query, body):
        if path == "/auth":
            return self._auth(query)
        if path == "/token":
            return self._token(body)
        if path == "/me":
            return self._me()
        if path.startswith("/rest/api/v1/"):
            return self._wallet(method, path[len("/rest/api/v1/"):], body)
        self.send_json(404, {"type": "ERROR", "message": "not found: " + path})

    def _auth(self, query):
        """Simulates the Jiwe login page: 'logs the player in' and redirects straight back."""
        redirect = (query.get("redirect_uri") or [""])[0]
        state = (query.get("state") or [""])[0]
        if not redirect:
            return self.send_raw(400, "missing redirect_uri")
        S["auth_seq"] += 1
        code = "code-" + uuid.uuid4().hex[:12]
        S["codes"][code] = {
            "challenge": (query.get("code_challenge") or [""])[0],
            "method": (query.get("code_challenge_method") or [""])[0],
            "redirect_uri": redirect,
            "scope": (query.get("scope") or [""])[0],
            "resource": (query.get("resource") or [""])[0],
        }
        sep = "&" if "?" in redirect else "?"
        self.send_response(302)
        self.send_header("Location", f"{redirect}{sep}code={code}&state={state}")
        self.send_header("Content-Length", "0")
        self._cors()
        self.end_headers()

    def _jwt(self, extra):
        c = dict(cfg()["claims"])
        c.update({"iss": "mock-jiwe", "aud": cfg()["client_id"], "iat": int(time.time()), "exp": int(time.time()) + 14 * 86400})
        c.update(extra)
        head = b64url(json.dumps({"alg": "none", "typ": "JWT"}).encode())
        tok = f"{head}.{b64url(json.dumps(c).encode())}.sig"
        S["id_tokens"].add(tok)
        return tok

    def _token(self, body):
        c = cfg()
        if c["token_delay"]:
            time.sleep(float(c["token_delay"]))
        ctype = self.hdr("Content-Type") or ""
        if "application/x-www-form-urlencoded" not in ctype:
            return self.send_json(400, {"error": "invalid_request", "error_description":
                                        "only application/x-www-form-urlencoded content-type bodies are supported on POST /token"})
        form = {k: v[0] for k, v in parse_qs(body, keep_blank_values=True).items()}
        auth = self.hdr("Authorization") or ""
        h_id = h_secret = None
        if auth.startswith("Basic "):
            try:
                h_id, _, h_secret = base64.b64decode(auth[6:]).decode().partition(":")
            except Exception:
                return self.send_json(400, {"error": "invalid_request", "error_description": "bad basic credentials"})
        b_id, b_secret = form.get("client_id"), form.get("client_secret")
        mode = c["token_auth"]
        ok_id, ok_secret = c["client_id"], c["client_secret"]

        def reject(status, error, desc):
            if c["consume_code_on_auth_failure"] and form.get("code"):
                S["used_codes"].add(form["code"])
            return self.send_json(status, {"error": error, "error_description": desc})

        header_present = h_id is not None
        body_secret_present = b_secret not in (None, "")
        if header_present and (b_id is not None or body_secret_present):
            return reject(400, "invalid_request", "client authentication must only be provided using one mechanism")
        if mode == "basic":
            if not header_present or h_id != ok_id or h_secret != ok_secret:
                return reject(401, "invalid_client", "client authentication failed")
        elif mode == "body":
            if header_present or b_id != ok_id or b_secret != ok_secret:
                return reject(401, "invalid_client", "client authentication failed")
        elif mode == "public":
            if header_present or body_secret_present or b_id != ok_id:
                return reject(401, "invalid_client", "client authentication failed")
        else:  # either
            good_h = header_present and h_id == ok_id and h_secret == ok_secret
            good_b = (not header_present) and b_id == ok_id and b_secret == ok_secret
            if not (good_h or good_b):
                return reject(401, "invalid_client", "client authentication failed")

        if form.get("grant_type") != "authorization_code":
            return self.send_json(400, {"error": "unsupported_grant_type"})
        code = form.get("code", "")
        if not code or (c["single_use_codes"] and code in S["used_codes"]):
            return self.send_json(400, {"error": "invalid_grant", "error_description": "authorization code is invalid or already used"})
        verifier = form.get("code_verifier", "")
        if not verifier:
            return self.send_json(400, {"error": "invalid_grant", "error_description": "code_verifier is required"})
        challenge = (S["codes"].get(code) or {}).get("challenge") or c["expected_challenge"]
        if challenge and b64url(hashlib.sha256(verifier.encode()).digest()) != challenge:
            return self.send_json(400, {"error": "invalid_grant", "error_description": "PKCE verification failed"})
        known = S["codes"].get(code)
        if known and known["redirect_uri"] != form.get("redirect_uri"):
            return self.send_json(400, {"error": "invalid_grant", "error_description": "redirect_uri mismatch"})
        S["used_codes"].add(code)

        access = "access-" + uuid.uuid4().hex[:10]
        S["access_tokens"][access] = {"sub": c["claims"]["sub"]}
        return self.send_json(200, {"access_token": access, "id_token": self._jwt({}),
                                    "token_type": "Bearer", "expires_in": c["expires_in"]})

    def _me(self):
        auth = self.hdr("Authorization") or ""
        if not auth.startswith("Bearer ") or auth[7:] not in S["access_tokens"]:
            return self.send_json(401, {"error": "invalid_token"})
        if cfg()["me_status"] != 200:
            return self.send_json(cfg()["me_status"], {"error": "invalid_token"})
        cl = cfg()["claims"]
        return self.send_json(200, {k: cl[k] for k in ("sub", "name", "nickname", "bio", "picture") if k in cl})

    # ---- wallet ---------------------------------------------------------
    def _api_key_ok(self, write=True):
        c = cfg()
        if self.hdr("X-API-USERNAME") != c["api_username"] or (write and self.hdr("X-API-KEY") != c["api_key"]):
            self.err(403, "Invalid username/api key")
            return False
        return True

    def _player(self):
        """Returns the player's sub, or None after sending the error."""
        tok = self.hdr("X-API-TOKEN")
        if not tok:
            self.err(401, "An X-API-TOKEN identity token is required")
            return None
        if tok in S["id_tokens"]:
            return cfg()["claims"]["sub"]
        if tok in S["access_tokens"]:
            return S["access_tokens"][tok]["sub"]
        self.err(400, "The serialization of the jwt is invalid")
        return None

    def _json(self, body):
        try:
            v = json.loads(body) if body else {}
            return v if isinstance(v, dict) else None
        except ValueError:
            return None

    def _wallet(self, method, rel, body):
        c = cfg()
        # OIDC bearer boundary — mutually exclusive with API-key headers
        if rel.startswith("oidc/"):
            if self.hdr("X-API-USERNAME") or self.hdr("X-API-KEY"):
                return self.err(400, "API-key headers are not accepted on an OIDC route")
            auth = self.hdr("Authorization") or ""
            if not auth.startswith("Bearer ") or auth[7:] not in S["access_tokens"] or not c["bearer_audience_ok"]:
                return self.send_json(401, {"type": "ERROR", "message": "Bearer token missing or unverifiable"})
            if not c["bearer_scope_ok"]:
                return self.send_json(403, {"type": "ERROR", "message": "insufficient scope"})
            if rel == "oidc/rewards/earned" and method == "GET":
                e = c["earned"]
                return self.send_json(200, {"points": e["points"], "XP": e["XP"]})
            return self.err(404, "no such oidc resource")

        if rel == "clients/account-balance" and method == "GET":
            if not self._api_key_ok(): return
            return self.send_json(200, {"type": "OK", "debits": c["debits"], "credits": c["credits"], "available": c["balance"]})

        if rel == "leaderboard" and method == "POST":
            if not self._api_key_ok(): return
            req = self._json(body)
            if req is None: return self.err(400, "invalid JSON")
            if req.get("leaderboardPeriod"):
                rows = [  # deliberately NOT in score order, and period boards use uppercase "XP"
                    {"id": "u-bob", "name": "Bob", "username": "bob", "rank": 2, "XP": 300},
                    {"id": "u-ann", "name": "Ann", "username": "ann", "rank": 1, "XP": 900},
                    {"id": "u-cy", "name": "Cy", "username": "cy", "rank": 3, "XP": 500},
                ]
            else:
                rows = [
                    {"id": "u-ann", "name": "Ann", "username": "ann", "rank": 1, "currentXP": 1641, "xp": 16},
                    {"id": "u-max", "name": "Max", "username": "max", "rank": 2, "xp": 915},
                ]
            return self.send_json(200, {"type": "OK", "leaderboard": rows[: int(req.get("maxEntries") or 10)], "totalEntries": len(rows)})

        if rel in ("rewards/xp", "rewards/airtime") and method == "POST":
            if not self._api_key_ok(): return
            if self._player() is None: return
            req = self._json(body)
            if req is None: return self.err(400, "invalid JSON")
            if rel == "rewards/xp" and not (isinstance(req.get("xpEarned"), int) and req["xpEarned"] > 0):
                return self.err(400, "xpEarned should be greater than 0")
            if rel == "rewards/airtime":
                if not (isinstance(req.get("airtimeReward"), int) and req["airtimeReward"] >= 5):
                    return self.err(400, "airtimeReward should be at least 5")
                if not req.get("recipientNumber"):
                    return self.err(400, "recipientNumber is required")
            tid = str(uuid.uuid4())
            S["txns"][tid] = {"status": "PENDING" if rel == "rewards/airtime" else "POSTED", "polls": 0}
            msg = {"rewards/xp": "XP processed successfully",
                   "rewards/airtime": "Airtime reward sent successfully."}[rel]
            return self.send_json(202 if rel == "rewards/airtime" else 200, {"type": "SUCCESS", "message": msg, "ledger_transaction_id": tid})

        if rel == "rewards/earned" and method == "GET":
            if not self._api_key_ok(): return
            if self._player() is None: return
            return self.send_json(200, c["earned"])

        if rel == "transactions/status" and method == "POST":
            if not self._api_key_ok(): return
            req = self._json(body) or {}
            t = S["txns"].get(req.get("transactionId"))
            if not t:
                return self.send_json(404, {"type": "ERROR", "message": "Ledger transaction not found"})
            t["polls"] += 1
            if t["status"] == "PENDING" and t["polls"] > c["settle_after_polls"]:
                t["status"] = "POSTED"
            return self.send_json(200, {"type": "OK", "statusId": str(uuid.uuid4()), "status": t["status"]})

        if rel == "metadata/game-data" and method == "GET":
            if self.hdr("X-API-USERNAME") != c["api_username"]:
                return self.err(403, "Invalid username")
            return self.send_json(200, {"type": "OK", "gameData": S["game_data"]})
        if rel == "metadata/update/game-data" and method == "POST":
            if not self._api_key_ok(): return
            return self._merge(body, "gameData", S["game_data"], "Game data updated successfully")

        if rel == "metadata/user-data" and method == "GET":
            if not self._api_key_ok(): return
            sub = self._player()
            if sub is None: return
            return self.send_json(200, {"type": "OK", "userData": S["user_data"].get(sub, {})})
        if rel == "metadata/update/user-data" and method == "POST":
            if not self._api_key_ok(): return
            sub = self._player()
            if sub is None: return
            return self._merge(body, "userData", S["user_data"].setdefault(sub, {}), "User data updated successfully")

        if rel == "purchases/wallet" and method == "POST":
            return self._purchase(body)
        if rel.startswith("purchases/wallet/") and method == "GET":
            if not self._api_key_ok(): return
            if self._player() is None: return
            p = S["purchases"].get(rel.rsplit("/", 1)[1])
            if not p:
                return self.err(404, "purchase not found")
            p["polls"] += 1
            if p["status"] == "PENDING" and p["polls"] > c["settle_after_polls"]:
                p["status"], p["requires_approval"] = "POSTED", False
            out = {k: v for k, v in p.items() if k != "polls"}
            out["type"] = "SUCCESS"
            return self.send_json(200, out)

        self.err(404, f"no such wallet route: {method} {rel}")

    def _merge(self, body, key, store, message):
        req = self._json(body)
        if req is None or not isinstance(req.get(key), dict):
            return self.err(400, f"{key} is required — send the body wrapped as {{\"{key}\": {{...}}}}")
        for k, v in req[key].items():
            if v is None:
                store.pop(k, None)
            else:
                store[k] = v
        self.send_json(200, {"type": "OK", "message": message})

    def _purchase(self, body):
        c = cfg()
        if not self._api_key_ok(): return
        sub = self._player()
        if sub is None: return
        key = self.hdr("Idempotency-Key")
        if not key:
            return self.err(400, "Idempotency key is required")
        if len(key) > 200:
            return self.err(400, "Idempotency-Key must be at most 200 characters")
        req = self._json(body)
        if req is None: return self.err(400, "invalid JSON")
        if "entityId" in req or "gamerWalletId" in req or "entityName" in req:
            return self.err(400, "unrecognised field (this endpoint takes itemId, not entityId/entityName/gamerWalletId)")
        amt = req.get("amount")
        if not isinstance(amt, (int, float)) or isinstance(amt, bool) or amt <= 0:
            return self.err(400, "amount must be a positive number")
        if float(amt) != int(amt):
            return self.err(400, "amount must be a whole number")
        fingerprint = json.dumps(req, sort_keys=True)
        prev = S["idem"].get((sub, key))
        if prev:
            if prev["fingerprint"] != fingerprint:
                return self.err(409, "This Idempotency-Key was already used for a different request")
            return self.send_json(prev["status"], prev["response"], {"Idempotent-Replay": "true"})
        if int(amt) > c["player_balance"]:
            resp = {"type": "ERROR", "message": "Insufficient balance"}
            S["idem"][(sub, key)] = {"fingerprint": fingerprint, "status": 422, "response": resp}
            return self.send_json(422, resp)
        pid, tid = str(uuid.uuid4()), str(uuid.uuid4())
        approval = bool(c["requires_approval"])
        rec = {"payment_order_id": pid, "ledger_transaction_id": tid, "status": "PENDING" if approval else "POSTED",
               "requires_approval": approval, "amount": int(amt), "currency": "KES", "itemId": req.get("itemId", ""),
               "description": req.get("description", ""), "catalogPriceValidated": False,
               "created_at": time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime()), "polls": 0}
        S["purchases"][pid] = rec
        if not approval:
            c["player_balance"] -= int(amt)
        resp = {"type": "SUCCESS",
                "message": "Purchase submitted and is awaiting approval" if approval else "Purchase completed successfully",
                **{k: v for k, v in rec.items() if k not in ("polls", "created_at")}}
        status = 202 if approval else 200
        S["idem"][(sub, key)] = {"fingerprint": fingerprint, "status": status, "response": resp}
        self.send_json(status, resp)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    args = ap.parse_args()
    srv = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    srv.daemon_threads = True
    print(f"mock jiwe on http://127.0.0.1:{args.port}", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
