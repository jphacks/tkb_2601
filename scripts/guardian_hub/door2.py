"""扉 2（店内 ⇄ インターネット）の中身。開けるのは人（PSoC 6 の SW2 か、ハブの open）、閉じるのは自動。

開いている間にだけ、外とやりとりする。
  1. 前に Bluesky に共有した投稿についた返信を取ってくる（人の意見）
  2. いまの状況の要約を Gemini に送り、見立てをもらう（クラウド AI の意見。ローカル AI に置き換えるまでのつなぎ）
  3. 同じ要約を Bluesky に共有する
  4. 記録・見立て・返信を Google Drive の同期フォルダに書き出す（ハブの本体が、扉 2 を閉じる前に書く）
外から来たもの（見立て・返信）は提案として見せるだけで、ロボットの動きは変えない。
外に出すのは要約だけ（場所の名前、ロボットの記号、状態、倍率、推薦）。座標や生の測定値は出さない。
何を出すか（共有しない・記録だけ・投稿と記録、Gemini に聞くか）と、誰の返信を受け取るか（店がフォローしている人・
指定した人・だれでも、受付期限）は、店が画面で選ぶ（policy）。返信した人は DID で見分ける。
ソケットや USB は持たない。通信は渡された相手（client）に任せるので、そのままテストできる。
"""
from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Callable

# Bluesky の部品は共有中継（scripts/guardian_share）のものを使う
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "guardian_share"))
from bluesky import MAX_GRAPHEMES, BlueskyError, did_of, is_did, is_handle, tag_facets, utc_now_iso  # noqa: E402
from report import md_escape  # noqa: E402

GEMINI_MODEL = "gemini-3.8-flash"
# 店の記録を、店のアカウントのリポジトリに残すときの形（AT プロトコルのレコード。lexicon は core/lexicons にある）
_NSID = re.compile(r"^[a-z][a-z0-9-]{0,62}(\.[a-z0-9][a-z0-9-]{0,62}){2,}$")
# 記録の種類（NSID）。コードには名前の入らない既定だけを置き、公開の名前空間（自分のドメイン）は core/.env の
# REPORT_COLLECTION に書く（.env は Git に入れない）
REPORT_COLLECTION = os.environ.get("REPORT_COLLECTION", "").strip() if _NSID.match(os.environ.get("REPORT_COLLECTION", "").strip()) \
    else "internal.robomori.report"
GEMINI_URL = "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"
CONDITION_WORDS = {"Normal": "正常", "Warning": "注意", "Limited": "制限"}
FOLLOW_POSTS = 10   # 返信を取りに行く、直近の投稿の数
MAX_OPINIONS = 20   # 1 件の記録に載せる意見（返信への参照）の数
SHARE_LEVELS = {"off": "共有しない", "sealed": "会社だけ（暗号化した記録。宛先の人だけが開ける）",
                "record": "記録だけ（公開。タイムラインには流さない）", "post": "投稿と記録（公開）"}
ACCEPT_FROM = {"following": "店がフォローしている人", "listed": "指定した人", "anyone": "だれでも"}
DEFAULT_POLICY = {"share": "sealed", "gemini": False, "accept": "following", "allow": [], "window_days": 7, "recipients": []}
SEALED_SUMMARY = "暗号化した店の記録。宛先の人だけが開ける"
SYSTEM_PROMPT = (
    "あなたは、飲食店の配膳ロボット（A・B・C）を見守る監視AIに助言する相談役です。"
    "ロボットを動かす権限はなく、あなたの答えは店員に「提案」として表示されるだけです。"
    "与えられた要約だけから、考えられる原因（見立て）と、人が次にすること（勧める対応）を、"
    "日本語でそれぞれ 80 文字以内で答えてください。分からないことは分からないと書いてください。"
    '出力は次の JSON だけにしてください: {"assessment": "見立て", "recommendation": "勧める対応", "confidence": "高|中|低"}'
)

_CONTROL = re.compile(r"[\x00-\x1f\x7f]")


def clean(value, limit: int) -> str:
    """外から来た文字列を、画面と記録に出せる 1 行にする。"""
    text = _CONTROL.sub(" ", value if isinstance(value, str) else "")
    text = re.sub(r"\s+", " ", text).strip()
    return text[:limit]


# ---- 要約 -----------------------------------------------------------------------------------------

LEVEL_WORDS = {"safe": "安全", "caution": "注意", "danger": "危険"}


def _judged(snapshot: dict) -> list[dict]:
    """ルール AI が注意か危険と判断したロボット（危険が先）。"""
    robots = ((snapshot.get("judgement") or {}).get("robots")) or []
    return sorted([r for r in robots if r.get("level") in ("caution", "danger")], key=lambda r: (r["level"] != "danger", r["robot"]))


def _sensor(snapshot: dict) -> dict | None:
    """M5 の実測で、外に出す意味のあること（注意以上になった・KEY2 で戻した・途切れた）。"""
    sensor = (snapshot.get("judgement") or {}).get("sensor")
    return sensor if sensor and (sensor.get("raised") or sensor.get("humanRecoveries") or sensor.get("gaps")) else None


def notable(snapshot: dict) -> bool:
    """外に出す意味のあることがあるか（減速、いつもと違う状態、推薦、ルール AI の注意以上、M5 の実測）。"""
    return bool(snapshot["slowdowns"] or snapshot["dispatch"] or _judged(snapshot) or _sensor(snapshot)
                or any(r["condition"] != "Normal" or r["stopped"] for r in snapshot["robots"]))


def _items(snapshot: dict) -> list[str]:
    items = []
    judgement = snapshot.get("judgement") or {}
    window = judgement.get("windowMinutes") or 30
    judged = _judged(snapshot)
    for r in judged:
        items.append(f"・ロボット{r['robot']}：ルールAIの判断は{LEVEL_WORDS[r['level']]}（{window}分で停止{r['stops']}回・注意/制限{r['degraded']}回）")
    sensor = _sensor(snapshot)
    if sensor:
        items.append(f"・ロボット{sensor['robot']}：M5StickS3の実測 最大{sensor['peakMg']}mg（しきい値{sensor['thresholdMg']}mg）・"
                     f"注意以上{sensor['raised']}回・人がKEY2で{sensor['humanRecoveries']}回戻した")
    said = {r["robot"] for r in judged}
    for s in snapshot["slowdowns"]:
        mark = "（模擬の段差）" if "模擬" in s["reason"] else ""
        if s["kind"] == "area":
            items.append(f"・{s['name']}：複数台が揺れた → 速度上限×{s['scale']:g}{mark}")
        else:
            items.append(f"・ロボット{s['name']}：場所を変えても揺れる → 速度上限×{s['scale']:g}、点検を")
    for d in snapshot["dispatch"]:
        items.append(f"・ロボット{d['robot']}：{d['situation']} → {d['person']}が向かう案（約{d['eta_s']:.0f}秒）")
    for r in snapshot["robots"]:
        if (r["stopped"] or r["condition"] != "Normal") and r["id"] not in said:
            if not any(d["robot"] == r["id"] for d in snapshot["dispatch"]):
                items.append(f"・ロボット{r['id']}：{'停止' if r['stopped'] else CONDITION_WORDS.get(r['condition'], r['condition'])}")
    return items


def compose_post(snapshot: dict, tags: list[str]) -> str:
    """Bluesky への共有文（300 文字まで）。入りきらない項目は「ほか N 件」にまとめる。"""
    head = "【ロボもり 監視AI】店内の様子を共有します（人が扉を開けて送っています）"
    tail = "ロボットは外につながっていません。お気づきの点を返信で教えてください。\n" + " ".join("#" + t for t in tags)
    items = _items(snapshot)
    for keep in range(len(items), -1, -1):
        rest = len(items) - keep
        body = items[:keep] + ([f"・ほか{rest}件"] if rest else [])
        text = "\n".join([head, *body, tail])
        if len(text) <= MAX_GRAPHEMES:
            return text
    return "\n".join([head, tail])[:MAX_GRAPHEMES]


def situation_text(snapshot: dict) -> str:
    """Gemini に渡す、少し詳しい要約。"""
    lines = ["店内の配膳ロボット（A・B・C）の状況です。", "", "速度を下げているところ:"]
    lines += [f"- {'場所 ' if s['kind'] == 'area' else 'ロボット '}{s['name']}: 速度上限 {s['scale']:g} 倍。理由: {s['reason']}"
              for s in snapshot["slowdowns"]] or ["- なし"]
    lines += ["", "ロボットの状態:"]
    lines += [f"- {r['id']}: {'停止' if r['stopped'] else CONDITION_WORDS.get(r['condition'], r['condition'])}"
              for r in snapshot["robots"]] or ["- 不明"]
    lines += ["", "誰が対応するかの推薦（店内のグラフ上の最短経路）:"]
    lines += [f"- {d['robot']}（{d['situation']}）→ {d['person']}（約 {d['eta_s']:.0f} 秒）" + (f"、次点 {d['backup']}" if d.get("backup") else "")
              for d in snapshot["dispatch"]] or ["- なし"]
    recent = [r.get("text") for r in snapshot["records"] if r.get("kind") == "ai" and r.get("text")][-8:]
    if recent:
        lines += ["", "監視AIの直近の判断:"] + [f"- {t}" for t in recent]
    return "\n".join(lines)


def normalize_policy(policy: dict | None) -> dict:
    """店が選んだ設定を、使える値だけにする（知らない値は安全な側に倒す）。"""
    merged = {**DEFAULT_POLICY, **(policy or {})}
    allow = [a.strip().lstrip("@") for a in merged.get("allow") or [] if isinstance(a, str)]
    try:
        days = max(1, min(30, int(merged.get("window_days"))))
    except (TypeError, ValueError):
        days = DEFAULT_POLICY["window_days"]
    return {
        "share": merged["share"] if merged.get("share") in SHARE_LEVELS else "off",
        "gemini": merged.get("gemini") is True,
        "accept": merged["accept"] if merged.get("accept") in ACCEPT_FROM else "listed",
        "allow": [a for a in allow if is_did(a) or is_handle(a)][:50],
        "window_days": days,
        "recipients": _recipients(merged.get("recipients")),
    }


def _recipients(items) -> list[dict]:
    """宛先（会社の人）: 名前と公開の鍵。形の合わないものは入れない。"""
    from seal import is_key, key_id
    found, seen = [], set()
    for item in items or []:
        if not isinstance(item, dict) or not is_key(str(item.get("key", ""))):
            continue
        kid = key_id(item["key"])
        if kid not in seen:
            seen.add(kid)
            found.append({"name": clean(item.get("name"), 32) or kid, "key": item["key"].strip(), "kid": kid})
    return found[:20]


def fit(value, max_bytes: int, max_chars: int | None = None) -> str:
    """lexicon の maxLength は UTF-8 のバイト数（日本語は 1 文字 3 バイト）。文字の途中で切らずに収める。"""
    text = str(value if value is not None else "")[:max_chars or max_bytes]
    while len(text.encode("utf-8")) > max_bytes:
        text = text[:-1]
    return text


def _strong_ref(item: dict | None) -> dict | None:
    if item and isinstance(item.get("uri"), str) and item["uri"].startswith("at://") and isinstance(item.get("cid"), str) and item["cid"]:
        return {"uri": item["uri"], "cid": item["cid"]}
    return None


def _parse_time(text: str) -> datetime | None:
    try:
        moment = datetime.fromisoformat(str(text).replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def sealed_record(snapshot: dict, summary: str, store: str, recipients: list[dict], previous: dict | None = None) -> dict:
    """会社だけ: 中身（いつもの店の記録）を宛先の人の鍵で暗号化し、外から見えるのは時刻・宛先の鍵の ID・前の記録だけにする。"""
    from seal import seal
    content = report_record(snapshot, summary, None, store, "sealed")
    record = {"$type": REPORT_COLLECTION, "createdAt": content["createdAt"], "summary": SEALED_SUMMARY, "share": "sealed",
              "sealed": seal(content, [r["key"] for r in recipients])}
    if _strong_ref(previous):
        record["previous"] = _strong_ref(previous)
    return record


def report_record(snapshot: dict, summary: str, post: dict | None, store: str, share: str = "post", accept: str = "",
                  previous: dict | None = None, opinions: list[dict] | None = None) -> dict:
    """店の記録（AT プロトコルのレコード）。数は整数だけ（レコードに小数は入れられない）。座標や生の測定値は入れない。
    意見（返信）は文をコピーせず、書いた人のリポジトリにある投稿を strongRef で指す。"""
    record = {
        "$type": REPORT_COLLECTION,
        "createdAt": utc_now_iso(),
        "store": fit(store, 256, 64),
        "summary": fit(summary, 3000, MAX_GRAPHEMES),
        "slowdowns": [{"kind": fit(s["kind"], 64), "place": fit(s["name"], 64), "speedPercent": max(30, min(100, int(round(float(s["scale"]) * 100)))),
                       "reason": fit(s.get("reason", ""), 600)} for s in snapshot["slowdowns"]][:10],
        "responders": [{"robot": fit(d["robot"], 8), "situation": fit(d["situation"], 64), "person": fit(d["person"], 64),
                        "etaSeconds": max(0, int(round(float(d["eta_s"]))))} for d in snapshot["dispatch"]][:5],
        "stopped": [fit(r["id"], 8) for r in snapshot["robots"] if r["stopped"]][:10],
    }
    judgement = snapshot.get("judgement")
    if judgement and judgement.get("rulesVersion"):
        record["judgement"] = {
            "rulesVersion": fit(judgement["rulesVersion"], 64), "windowMinutes": max(0, int(judgement.get("windowMinutes") or 0)),
            "store": judgement.get("store") if judgement.get("store") in LEVEL_WORDS else "safe",
            "robots": [{"robot": fit(r.get("robot"), 8), "level": r.get("level") if r.get("level") in LEVEL_WORDS else "safe",
                        "stops": max(0, int(r.get("stops") or 0)), "degraded": max(0, int(r.get("degraded") or 0))}
                       for r in (judgement.get("robots") or [])][:10]}
        sensor = judgement.get("sensor")
        if sensor and sensor.get("robot"):
            record["sensor"] = {"device": fit(sensor.get("device") or "M5StickS3", 64), "robot": fit(sensor["robot"], 8), "measured": True,
                                **{k: max(0, int(sensor.get(k) or 0)) for k in ("thresholdMg", "peakMg", "raised", "humanRecoveries", "gaps")}}
    if share in ("post", "record", "sealed"):
        record["share"] = share
    if accept in ACCEPT_FROM:
        record["accept"] = accept
    if _strong_ref(post):
        record["post"] = _strong_ref(post)   # com.atproto.repo.strongRef
    if _strong_ref(previous):
        record["previous"] = _strong_ref(previous)
    refs = []
    for opinion in opinions or []:
        ref = _strong_ref(opinion.get("post"))
        if ref and is_did(opinion.get("author", "")) and ref["uri"] not in {r["post"]["uri"] for r in refs}:
            refs.append({"author": opinion["author"], "post": ref})
    if refs:
        record["opinions"] = refs[:MAX_OPINIONS]
    return record


# ---- Gemini ---------------------------------------------------------------------------------------

class GeminiError(Exception):
    pass


Transport = Callable[[str, dict, dict], dict]


def http_post_json(url: str, body: dict, headers: dict, timeout: float = 20.0) -> dict:
    request = urllib.request.Request(url, data=json.dumps(body).encode("utf-8"), method="POST",
                                     headers={"Content-Type": "application/json", **headers})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        try:
            message = json.loads(error.read().decode("utf-8")).get("error", {}).get("message", "")
        except (ValueError, AttributeError):
            message = ""
        raise GeminiError(f"HTTP {error.code} {message}".strip()) from None


class GeminiClient:
    """Gemini API（generateContent）。API キーはヘッダーで渡し、URL やログには出さない。"""

    def __init__(self, api_key: str, model: str = GEMINI_MODEL, transport: Transport = http_post_json):
        self._key = api_key
        self.model = model
        self._transport = transport

    def judge(self, situation: str) -> dict:
        body = {
            "systemInstruction": {"parts": [{"text": SYSTEM_PROMPT}]},
            "contents": [{"role": "user", "parts": [{"text": situation}]}],
            "generationConfig": {"responseMimeType": "application/json", "temperature": 0.2},
        }
        result = self._transport(GEMINI_URL.format(model=self.model), body, {"x-goog-api-key": self._key})
        return parse_judgement(result)


def parse_judgement(result: dict) -> dict:
    """generateContent の返事から見立てを取り出す。JSON で返らなかったときは、本文を見立てとして使う。"""
    blocked = (result.get("promptFeedback") or {}).get("blockReason")
    candidates = result.get("candidates") or []
    if blocked or not candidates:
        raise GeminiError(f"見立てが返らなかった（{blocked or '候補なし'}）")
    parts = (candidates[0].get("content") or {}).get("parts") or []
    text = "".join(p.get("text", "") for p in parts if isinstance(p, dict) and not p.get("thought")).strip()
    text = re.sub(r"^```(?:json)?\s*|\s*```$", "", text)
    try:
        data = json.loads(text)
    except ValueError:
        data = None
    if not isinstance(data, dict):
        data = {"assessment": text}
    confidence = data.get("confidence") if data.get("confidence") in ("高", "中", "低") else ""
    judgement = {"assessment": clean(data.get("assessment"), 120), "recommendation": clean(data.get("recommendation"), 120),
                 "confidence": confidence}
    if not judgement["assessment"] and not judgement["recommendation"]:
        raise GeminiError("見立てが空だった")
    return judgement


# ---- 扉 2 が開いている間のやりとり -------------------------------------------------------------------

class Outside:
    """扉 2 の向こうとのやりとり。gemini・bluesky が None なら、その相手とは通信しない（試し運転）。
    policy は店が選んだこと（何を出すか・誰の返信を受け取るか）。"""

    def __init__(self, gemini: GeminiClient | None, bluesky, tags: list[str], store: str = "ロボもり", policy: dict | None = None):
        self.policy = normalize_policy(policy)
        self.gemini = gemini if self.policy["gemini"] else None
        self.bluesky = bluesky
        self.tags = tags
        self.store = store
        self.posts: list[dict] = []          # これまでに共有した投稿（返信を追う）。uri・url・handle・created_at
        self.seen_replies: set[str] = set()
        self.previous: dict | None = None    # 1 つ前の記録（uri・cid）。記録を順にたどれるようにする
        self.carry: list[dict] = []          # 受け取ったが、まだ記録に載せていない意見（author・post）

    def _allowed(self, result: dict) -> set[str] | None:
        """受け取る相手の DID。None は「だれでも」。調べられなかったときは空（だれも受け取らない）。"""
        accept = self.policy["accept"]
        if accept == "anyone":
            return None
        if accept == "following":
            store = next((did_of(p["uri"]) for p in reversed(self.posts) if did_of(p.get("uri", ""))), "")
            try:
                return self.bluesky.follows(store) if store else set()
            except (BlueskyError, OSError, ValueError, KeyError) as error:
                result["errors"].append(f"店のフォロー一覧を読めなかったので、返信は受け取らない（{error}）")
                return set()
        allowed = set()
        for entry in self.policy["allow"]:
            if is_did(entry):
                allowed.add(entry)
                continue
            try:
                allowed.add(self.bluesky.resolve_handle(entry))
            except (BlueskyError, OSError, ValueError, KeyError) as error:
                result["errors"].append(f"@{entry} を DID にできなかったので、その人の返信は受け取らない（{error}）")
        return allowed

    def _read_replies(self, result: dict) -> None:
        """前の投稿への返信（外部の意見）を、店が選んだ相手・受付期限の中からだけ受け取る。"""
        allowed = self._allowed(result) if self.posts else set()
        window = timedelta(days=self.policy["window_days"])
        for post in self.posts[-FOLLOW_POSTS:]:
            try:
                replies = self.bluesky.replies(post["uri"])
            except (BlueskyError, OSError, ValueError, KeyError) as error:
                result["errors"].append(f"Bluesky の返信を取れなかった（{error}）")
                break
            store = did_of(post["uri"])
            posted = _parse_time(post.get("created_at", ""))
            for reply in replies:
                if reply["uri"] in self.seen_replies or (store and reply.get("did") == store) or reply.get("handle") == post.get("handle"):
                    continue  # 取り込み済み、または自分の返信
                arrived = _parse_time(reply.get("indexed_at") or reply.get("created_at") or "")
                if posted is None or arrived is None or arrived > posted + window:
                    result["late"] += 1          # 受付期限を過ぎた（時刻が分からないものも受け取らない）
                    continue
                if not is_did(reply.get("did", "")) or (allowed is not None and reply["did"] not in allowed):
                    result["not_allowed"] += 1   # 受け取る相手ではない。文は記録にも画面にも残さない
                    continue
                self.seen_replies.add(reply["uri"])
                result["replies"].append({"uri": clean(reply.get("uri"), 300), "cid": clean(reply.get("cid"), 200),
                                          "did": reply["did"], "handle": clean(reply.get("handle"), 64),
                                          "text": clean(reply.get("text"), 300),
                                          "url": reply.get("url") or "", "created_at": clean(reply.get("created_at"), 40),
                                          "post": post.get("url") or ""})

    def exchange(self, snapshot: dict) -> dict:
        """扉 2 が開いている間に 1 回だけ呼ぶ。通信の失敗は例外にせず、errors に書いて続ける。"""
        share = self.policy["share"]
        result = {"replies": [], "judgement": None, "post": None, "record": None, "text": "", "errors": [], "notable": notable(snapshot),
                  "gemini": "off" if self.gemini is None else "on", "bluesky": "off" if self.bluesky is None else "on",
                  "share": share, "accept": self.policy["accept"], "late": 0, "not_allowed": 0}
        # 1. 前の投稿への返信（外部の意見）
        if self.bluesky is not None:
            self._read_replies(result)
        if not result["notable"]:
            return result  # 減速も故障もないときは、外に聞くことも共有することもない
        # 2. クラウド AI の見立て
        if self.gemini is not None:
            try:
                result["judgement"] = self.gemini.judge(situation_text(snapshot))
            except (GeminiError, OSError, ValueError, KeyError) as error:
                result["errors"].append(f"Gemini の見立てを取れなかった（{error}）")
        # 会社だけ: 平文は外に出さない。投稿はせず、宛先の人だけが開ける暗号化した記録を 1 件置く
        if share == "sealed":
            result["text"] = compose_post(snapshot, self.tags)
            if self.bluesky is not None:
                try:
                    record = sealed_record(snapshot, result["text"], self.store, self.policy["recipients"], self.previous)
                    result["record"] = self.bluesky.create_record(REPORT_COLLECTION, record)
                    result["record"]["recipients"] = len(record["sealed"]["recipients"])
                    self.previous = result["record"]
                except (BlueskyError, OSError, ValueError, KeyError) as error:
                    result["errors"].append(f"暗号化した記録を残せなかった（{error}）")
            return result
        # 3. Bluesky への共有（店が「共有しない」を選んでいれば、投稿も記録も書かない）
        result["text"] = compose_post(snapshot, self.tags)
        if self.bluesky is not None and share == "post":
            try:
                posted = self.bluesky.post(result["text"], tag_facets(result["text"], self.tags))
                result["post"] = posted
                self.posts.append({"uri": posted["uri"], "url": posted.get("url"), "handle": posted.get("handle"),
                                   "created_at": posted.get("created_at", "")})
            except (BlueskyError, OSError, ValueError, KeyError) as error:
                result["errors"].append(f"Bluesky に共有できなかった（{error}）")
        # 4. 店の記録を、店のアカウントのリポジトリに残す（アカウントごと持ち運べる。ほかのアプリからも読める）
        if self.bluesky is not None and share in ("post", "record"):
            opinions = self.carry + [{"author": r["did"], "post": {"uri": r["uri"], "cid": r["cid"]}} for r in result["replies"]]
            record = report_record(snapshot, result["text"], result["post"], self.store, share, self.policy["accept"], self.previous, opinions)
            try:
                result["record"] = self.bluesky.create_record(REPORT_COLLECTION, record)
                result["record"]["opinions"] = len(record.get("opinions", []))
                self.previous, self.carry = result["record"], []
            except (BlueskyError, OSError, ValueError, KeyError) as error:
                result["errors"].append(f"AT プロトコルの記録を残せなかった（{error}）")
        return result


def render_report(stamp: str, records: list[dict], result: dict) -> str:
    """Google Drive に書く Markdown。外から来た文章は、Markdown として解釈させない。"""
    out = [f"# ロボもり 扉2 の記録 {stamp}", ""]
    out += ["## 共有した要約", ""]
    if result.get("scheduled"):
        out += ["時間割の書き出し（人が前もって決めた時刻）。Gemini・Bluesky には送っていない（送るのは SW2 を押したときだけ）。", ""]
        if result.get("text"):
            out += ["```", result["text"], "```", ""]
    elif result.get("text"):
        post = result.get("post")
        if post and post.get("url"):
            out.append(f"Bluesky に共有した: {post['url']}")
        elif result.get("share") == "off":
            out.append("店の設定（共有しない）で、Bluesky には出していない。")
        elif result.get("share") == "record" and result.get("bluesky") == "on":
            out.append("店の設定（記録だけ）で、タイムラインには投稿していない。")
        elif result.get("bluesky") == "off":
            out.append("Bluesky には共有していない（試し運転）。")
        else:
            out.append("Bluesky に共有できなかった（「うまくいかなかったこと」を見る）。")
        out += ["", "```", result["text"], "```", ""]
    else:
        out += ["減速も故障もなかったので、共有していない。", ""]
    out += ["## 外部 AI の見立て（Gemini。提案であり、ロボットの動きは変えていない）", ""]
    judgement = result.get("judgement")
    if judgement:
        out += [f"- 見立て: {md_escape(judgement['assessment'])}", f"- 勧める対応: {md_escape(judgement['recommendation'])}"]
        if judgement.get("confidence"):
            out.append(f"- 確かさ: {judgement['confidence']}")
    else:
        out.append("聞いていない（API キーがない試し運転、減速も故障もない、または失敗）。")
    record = result.get("record")
    if record and record.get("uri"):
        out += ["", "## 店の記録（AT プロトコル）", "", f"- {record['uri']}",
                f"- 意見（返信への参照）: {record.get('opinions', 0)} 件"]
    out += ["", "## Bluesky の返信（外部の意見。確かめていない仮説）", ""]
    skipped = []
    if result.get("not_allowed"):
        skipped.append(f"受け取る相手（{ACCEPT_FROM.get(result.get('accept'), '')}）ではない返信 {result['not_allowed']} 件")
    if result.get("late"):
        skipped.append(f"受付期限を過ぎた返信 {result['late']} 件")
    if skipped:
        out += ["取り込まなかった: " + "、".join(skipped) + "（文は残していない）", ""]
    if not result.get("replies"):
        out.append("新しい返信はない。")
    for reply in result.get("replies", []):
        out += [f"### @{md_escape(reply['handle'])} {md_escape(reply['created_at'])}", ""]
        if reply.get("url"):
            out += [reply["url"], ""]
        out += ["    " + reply["text"], ""]   # 字下げしたコードブロック: リンクや画像を埋め込ませない
    if result.get("errors"):
        out += ["", "## うまくいかなかったこと", ""] + [f"- {md_escape(e)}" for e in result["errors"]]
    out += ["", f"## 監視ハブの記録（{len(records)} 件）", ""]
    for item in records:
        text = item.get("text") or (f"ロボット {item.get('robot')}: {item.get('situation')} → {item.get('person')}"
                                    f"（約 {item.get('eta_s')} 秒）" if item.get("kind") == "dispatch" else item.get("door", ""))
        out.append(f"- {item.get('at', '')} [{item.get('kind', '')}] {md_escape(str(text))}")
    return "\n".join(out) + "\n"


def stamp_now() -> str:
    return datetime.now().strftime("%Y%m%d-%H%M%S")
