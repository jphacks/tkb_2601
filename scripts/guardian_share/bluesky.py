"""Bluesky（AT Protocol）の最小クライアント。標準ライブラリだけで動く。

- 投稿: com.atproto.server.createSession でログインし、com.atproto.repo.createRecord で app.bsky.feed.post を作る
- 返信: 公開 AppView の app.bsky.feed.getPostThread（認証なし）で、投稿についた返信を取る
- 誰の返信を受け取るか: 返信した人は DID で見分ける。店のフォロー一覧（app.bsky.graph.getFollows）とハンドルの解決
  （com.atproto.identity.resolveHandle）も公開 AppView から読む（認証なし）
パスワードはアプリパスワードを使い、ログにもファイルにも残さない。
"""
from __future__ import annotations

import json
import re
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from typing import Callable

# 投稿本文の上限（Bluesky の lexicon）。日本語は 1 文字 1 書記素なので、len() で上から抑える。
MAX_GRAPHEMES = 300
MAX_BYTES = 3000
POST_COLLECTION = "app.bsky.feed.post"

_DID = re.compile(r"^did:[a-z]+:[A-Za-z0-9._:%-]+$")
_RKEY = re.compile(r"^[A-Za-z0-9._:~-]{1,512}$")
_HANDLE = re.compile(r"^[A-Za-z0-9.-]{1,253}$")


class BlueskyError(Exception):
    """XRPC がエラーを返した（HTTP の状態と、AT Protocol のエラー名）。"""

    def __init__(self, status: int, error: str, message: str):
        super().__init__(f"HTTP {status} {error}: {message}")
        self.status = status
        self.error = error


Transport = Callable[..., dict]


def http_json(method: str, url: str, body: dict | None = None, token: str | None = None, timeout: float = 10.0) -> dict:
    """JSON を送って JSON を受け取る。通信できないときは OSError（URLError）をそのまま上げる。"""
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Accept", "application/json")
    if data is not None:
        request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        try:
            detail = json.loads(error.read().decode("utf-8") or "{}")
        except ValueError:
            detail = {}
        raise BlueskyError(error.code, str(detail.get("error", "")), str(detail.get("message", error.reason))) from None
    return json.loads(payload) if payload else {}


def utc_now_iso(now: datetime | None = None) -> str:
    """createdAt 用の UTC 時刻（例: 2026-10-24T01:15:02.123Z）。"""
    moment = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    return moment.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def check_length(text: str) -> None:
    if len(text) > MAX_GRAPHEMES or len(text.encode("utf-8")) > MAX_BYTES:
        raise ValueError(f"投稿が長すぎる（{len(text)} 文字、上限 {MAX_GRAPHEMES}）")


def tag_facets(text: str, tags: list[str]) -> list[dict]:
    """本文中の #タグ をハッシュタグとして扱わせる facet（位置は UTF-8 のバイト数）。"""
    data = text.encode("utf-8")
    facets = []
    for tag in tags:
        needle = ("#" + tag).encode("utf-8")
        start = data.find(needle)
        if start < 0:
            continue
        end = start + len(needle)
        if end < len(data) and not data[end:end + 1].isspace():
            continue  # 別の語の一部（#タグ名の途中）なら付けない
        facets.append({"index": {"byteStart": start, "byteEnd": end},
                       "features": [{"$type": "app.bsky.richtext.facet#tag", "tag": tag}]})
    return sorted(facets, key=lambda facet: facet["index"]["byteStart"])


def did_of(uri: str) -> str:
    """at://did/collection/rkey の DID。形がおかしければ空。"""
    parts = uri.split("/") if uri.startswith("at://") else []
    return parts[2] if len(parts) >= 3 and _DID.match(parts[2]) else ""


def is_did(text: str) -> bool:
    return bool(_DID.match(text or "")) and len(text) <= 2048


def is_handle(text: str) -> bool:
    return bool(_HANDLE.match(text or "")) and "." in (text or "") and not text.startswith(".") and not text.endswith(".")


def web_url(uri: str, handle: str | None = None) -> str | None:
    """at://did/app.bsky.feed.post/rkey を bsky.app の URL にする。形がおかしければ None。"""
    parts = uri.split("/") if uri.startswith("at://") else []
    if len(parts) != 5 or parts[3] != POST_COLLECTION or not _DID.match(parts[2]) or not _RKEY.match(parts[4]):
        return None
    actor = handle if handle and _HANDLE.match(handle) else parts[2]
    return f"https://bsky.app/profile/{actor}/post/{parts[4]}"


def flatten_replies(thread: dict | None, depth: int = 0) -> list[dict]:
    """getPostThread の thread から返信を古い順に取り出す。削除・ブロックで見えないものは飛ばす。"""
    replies: list[dict] = []
    for item in (thread or {}).get("replies") or []:
        post = item.get("post") if isinstance(item, dict) else None
        if not isinstance(post, dict):
            continue  # notFoundPost / blockedPost
        record = post.get("record") or {}
        author = post.get("author") or {}
        replies.append({
            "uri": post.get("uri", ""),
            "cid": post.get("cid", ""),
            "url": web_url(post.get("uri", ""), author.get("handle")),
            "did": author.get("did", "") if is_did(author.get("did", "")) else "",
            "handle": author.get("handle", ""),
            "name": author.get("displayName") or "",
            "text": record.get("text", ""),
            "created_at": record.get("createdAt") or post.get("indexedAt") or "",
            "indexed_at": post.get("indexedAt") or "",   # AppView が受け取った時刻（受付期限はこちらで見る）
            "depth": depth + 1,
        })
        replies.extend(flatten_replies(item, depth + 1))
    return sorted(replies, key=lambda reply: reply["created_at"])


class BlueskyClient:
    """1 つのアカウントで投稿し、公開 AppView から返信を読む。"""

    def __init__(self, identifier: str, password: str, service: str = "https://bsky.social",
                 appview: str = "https://public.api.bsky.app", transport: Transport = http_json):
        self._identifier = identifier
        self._password = password
        self._service = service.rstrip("/")
        self._appview = appview.rstrip("/")
        self._transport = transport
        self._token: str | None = None
        self.did: str | None = None
        self.handle: str = identifier

    def _login(self) -> None:
        session = self._transport("POST", self._service + "/xrpc/com.atproto.server.createSession",
                                  {"identifier": self._identifier, "password": self._password})
        self._token = session["accessJwt"]
        self.did = session["did"]
        self.handle = session.get("handle") or self._identifier

    def post(self, text: str, facets: list[dict] | None = None, langs: tuple[str, ...] = ("ja",),
             now: datetime | None = None) -> dict:
        """投稿して、uri・cid・bsky.app の URL を返す。アクセストークンが切れていたら 1 回だけログインし直す。"""
        check_length(text)
        record = {"$type": POST_COLLECTION, "text": text, "createdAt": utc_now_iso(now), "langs": list(langs)}
        if facets:
            record["facets"] = facets
        result = self.create_record(POST_COLLECTION, record)
        return {"uri": result["uri"], "cid": result["cid"], "url": web_url(result["uri"], self.handle),
                "handle": self.handle, "created_at": record["createdAt"]}

    def create_record(self, collection: str, record: dict) -> dict:
        """自分のリポジトリにレコードを 1 件書き、uri と cid を返す（投稿以外の、自分で決めた形のレコードも書ける）。
        アクセストークンが切れていたら 1 回だけログインし直す。"""
        for attempt in (1, 2):
            if self._token is None:
                self._login()
            try:
                result = self._transport("POST", self._service + "/xrpc/com.atproto.repo.createRecord",
                                         {"repo": self.did, "collection": collection, "record": record}, self._token)
            except BlueskyError as error:
                if attempt == 1 and error.error in ("ExpiredToken", "InvalidToken"):
                    self._token = None
                    continue
                raise
            return {"uri": result["uri"], "cid": result["cid"]}
        raise AssertionError("unreachable")

    def replies(self, uri: str) -> list[dict]:
        query = urllib.parse.urlencode({"uri": uri, "depth": 6, "parentHeight": 0})
        result = self._transport("GET", self._appview + "/xrpc/app.bsky.feed.getPostThread?" + query)
        return flatten_replies(result.get("thread"))

    def follows(self, actor: str, pages: int = 10) -> set[str]:
        """actor がフォローしているアカウントの DID（公開 AppView。100 件ずつ、最大 pages 回）。"""
        found: set[str] = set()
        cursor = ""
        for _ in range(pages):
            query = {"actor": actor, "limit": 100}
            if cursor:
                query["cursor"] = cursor
            result = self._transport("GET", self._appview + "/xrpc/app.bsky.graph.getFollows?" + urllib.parse.urlencode(query))
            found.update(f["did"] for f in result.get("follows") or [] if isinstance(f, dict) and is_did(f.get("did", "")))
            cursor = result.get("cursor") or ""
            if not cursor:
                break
        return found

    def resolve_handle(self, handle: str) -> str:
        """ハンドル（例: alice.bsky.social）を DID にする。"""
        if not is_handle(handle):
            raise ValueError(f"ハンドルの形ではない: {handle[:64]}")
        result = self._transport("GET", self._appview + "/xrpc/com.atproto.identity.resolveHandle?" + urllib.parse.urlencode({"handle": handle}))
        did = result.get("did", "")
        if not is_did(did):
            raise ValueError(f"{handle} の DID が返らなかった")
        return did
