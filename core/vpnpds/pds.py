"""仮想 VPN の中の、社内 PDS の代わり（AT プロトコルの最小限の口だけ）。標準ライブラリだけで動く。

docker compose --profile vpn で立ち上がる。外への道がない vpn ネットワークの中にいて、扉 2（door2）だけがつながる。
ブラウザ（core/viewer）からは、この PC の 127.0.0.1:2583 からだけ届く（ほかの PC からは届かない）。
本物の PDS ではない（署名つきのリポジトリや連合はない）。会社ごとのアカウント・記録の置き場所・読み出しの口が、
本物と同じ形で動くことを見せるためのもの。

口: com.atproto.server.describeServer / createSession、com.atproto.repo.createRecord / listRecords / getRecord、
com.atproto.identity.resolveHandle、app.bsky.feed.getPostThread（返信はない）、app.bsky.graph.getFollows（空）
アカウント: VPN_PDS_ACCOUNTS="handle:password,handle:password"（会社ごとに 1 つ。core/host/bluesky_setup.ps1 -Vpn が
ランダムなパスワードで .env に書く。コードに既定のパスワードは置かない。なければ起動しない）
安全: ログインの鍵は 2 時間で切れる。続けて間違えたら、しばらく断る。書けるのは自分のリポジトリだけ。記録の中身はログに出さない。
"""
from __future__ import annotations

import base64
import hashlib
import json
import os
import re
import secrets
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

DATA = Path(os.environ.get("VPN_PDS_DATA", "/data/vpn-pds.json"))
PORT = int(os.environ.get("VPN_PDS_PORT", "2583"))
MAX_BODY = 512 * 1024
TOKEN_SECONDS = 2 * 60 * 60
FAIL_LIMIT, FAIL_PAUSE = 5, 60.0   # 続けて 5 回間違えたら 60 秒は断る
NSID = re.compile(r"^[a-z][a-z0-9-]{0,62}(\.[a-z0-9][a-z0-9-]{0,62}){2,}$")
lock = threading.Lock()
tokens: dict[str, tuple[str, float]] = {}   # アクセストークン → (DID, 期限)（起動している間だけ）
failures = {"count": 0, "until": 0.0}


def did_of(handle: str) -> str:
    """ハンドルから決まる DID（did:plc の形。本物の PLC には登録しない）。"""
    digest = hashlib.sha256(("robomori-vpn:" + handle).encode()).digest()
    return "did:plc:" + base64.b32encode(digest).decode().lower()[:24]


def accounts() -> dict[str, dict]:
    found = {}
    for item in os.environ.get("VPN_PDS_ACCOUNTS", "").split(","):
        if ":" in item:
            handle, password = item.strip().split(":", 1)
            if re.fullmatch(r"[a-z0-9-]+(\.[a-z0-9-]+)+", handle.lower()) and len(password) >= 16:
                found[did_of(handle)] = {"handle": handle.lower(), "password": password}
    return found


def token_owner(header: str) -> str | None:
    if not header.startswith("Bearer "):
        return None
    owner = tokens.get(header[7:])
    if owner is None or owner[1] < time.time():
        tokens.pop(header[7:], None)
        return None
    return owner[0]


def load() -> dict:
    try:
        return json.loads(DATA.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"records": {}}


def save(store: dict) -> None:
    DATA.parent.mkdir(parents=True, exist_ok=True)
    temp = DATA.with_suffix(".tmp")
    temp.write_text(json.dumps(store, ensure_ascii=False), encoding="utf-8")
    temp.replace(DATA)


def cid_of(value: dict) -> str:
    digest = hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True).encode()).digest()
    return "bafyrei" + base64.b32encode(digest).decode().lower().rstrip("=")[:52]


def tid() -> str:
    alphabet = "234567abcdefghijklmnopqrstuvwxyz"
    n = (time.time_ns() // 1000) << 10 | secrets.randbelow(1024)
    return "".join(alphabet[(n >> (5 * i)) & 31] for i in reversed(range(13)))


class Handler(BaseHTTPRequestHandler):
    server_version = "robomori-vpn-pds"

    def log_message(self, fmt, *args):   # 中身（記録）はログに出さない
        print(f"{self.address_string()} {self.command} {urlparse(self.path).path}", flush=True)

    def _send(self, status: int, body: dict) -> None:
        data = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Private-Network", "true")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _error(self, status: int, error: str, message: str) -> None:
        self._send(status, {"error": error, "message": message})

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, Authorization")
        # ブラウザが手元のファイル（file://）からこの PC の住所を読むときの確認（Private Network Access）に答える
        self.send_header("Access-Control-Allow-Private-Network", "true")
        self.end_headers()

    def _method(self) -> tuple[str, dict]:
        url = urlparse(self.path)
        if not url.path.startswith("/xrpc/"):
            return "", {}
        return url.path[len("/xrpc/"):], {k: v[0] for k, v in parse_qs(url.query).items()}

    def do_GET(self):
        method, q = self._method()
        if method == "com.atproto.server.describeServer":
            return self._send(200, {"did": "did:web:vpn-pds", "availableUserDomains": [".vpn.internal"], "inviteCodeRequired": True})
        if method == "com.atproto.identity.resolveHandle":
            handle = (q.get("handle") or "").lower()
            for did, account in accounts().items():
                if account["handle"] == handle:
                    return self._send(200, {"did": did})
            return self._error(400, "InvalidRequest", "Unable to resolve handle")
        if method in ("com.atproto.repo.listRecords", "com.atproto.repo.getRecord"):
            repo, collection = q.get("repo", ""), q.get("collection", "")
            if repo not in accounts():
                repo = next((d for d, a in accounts().items() if a["handle"] == repo.lower()), repo)
            with lock:
                items = load()["records"].get(repo, {}).get(collection, [])
            if method == "com.atproto.repo.getRecord":
                found = next((r for r in items if r["uri"].rsplit("/", 1)[-1] == q.get("rkey")), None)
                return self._send(200, found) if found else self._error(400, "RecordNotFound", "Could not locate record")
            items = list(reversed(items)) if q.get("reverse") != "true" else items
            start = int(q.get("cursor") or 0)
            limit = max(1, min(100, int(q.get("limit") or 50)))
            page = items[start:start + limit]
            body = {"records": page}
            if start + limit < len(items):
                body["cursor"] = str(start + limit)
            return self._send(200, body)
        if method == "app.bsky.feed.getPostThread":
            return self._error(400, "NotFound", "Post not found")
        if method == "app.bsky.graph.getFollows":
            return self._send(200, {"follows": []})
        return self._error(404, "MethodNotImplemented", method or "not xrpc")

    def do_POST(self):
        method, _ = self._method()
        length = int(self.headers.get("Content-Length") or 0)
        if length > MAX_BODY:
            return self._error(413, "PayloadTooLarge", "too large")
        try:
            body = json.loads(self.rfile.read(length) or b"{}")
        except ValueError:
            return self._error(400, "InvalidRequest", "not json")
        if method == "com.atproto.server.createSession":
            if time.time() < failures["until"]:
                return self._error(429, "RateLimitExceeded", "too many failed logins; wait a minute")
            ident, password = str(body.get("identifier", "")).lower(), str(body.get("password", ""))
            for did, account in accounts().items():
                if ident in (did, account["handle"]) and secrets.compare_digest(password.encode(), account["password"].encode()):
                    failures["count"] = 0
                    now = time.time()
                    for old in [k for k, (_, until) in tokens.items() if until < now]:
                        tokens.pop(old, None)
                    token = secrets.token_urlsafe(32)
                    tokens[token] = (did, now + TOKEN_SECONDS)
                    return self._send(200, {"did": did, "handle": account["handle"], "accessJwt": token, "refreshJwt": token})
            failures["count"] += 1
            if failures["count"] >= FAIL_LIMIT:
                failures.update(count=0, until=time.time() + FAIL_PAUSE)
            time.sleep(0.4)
            return self._error(401, "AuthenticationRequired", "Invalid identifier or password")
        if method == "com.atproto.repo.createRecord":
            did = token_owner(self.headers.get("Authorization", ""))
            if not did:
                return self._error(401, "AuthenticationRequired", "login first")
            if body.get("repo") not in (did, accounts()[did]["handle"]):
                return self._error(403, "Forbidden", "you can write only to your own repo")
            collection, record = str(body.get("collection", "")), body.get("record")
            if not NSID.match(collection) or not isinstance(record, dict) or record.get("$type") != collection:
                return self._error(400, "InvalidRequest", "collection (NSID) and a record of that $type")
            uri, cid = f"at://{did}/{collection}/{tid()}", cid_of(record)
            with lock:
                store = load()
                store["records"].setdefault(did, {}).setdefault(collection, []).append({"uri": uri, "cid": cid, "value": record})
                save(store)
            return self._send(200, {"uri": uri, "cid": cid})
        return self._error(404, "MethodNotImplemented", method or "not xrpc")


def main() -> None:
    if not accounts():
        raise SystemExit("VPN_PDS_ACCOUNTS がない（core\\host\\bluesky_setup.ps1 -Vpn で、ランダムなパスワードの会社のアカウントを作る）")
    for did, account in accounts().items():
        print(f"アカウント: @{account['handle']} = {did}", flush=True)
    print(f"仮想 VPN の社内 PDS を開始（:{PORT}。外への道はない）", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
