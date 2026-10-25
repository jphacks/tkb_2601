"""画面（ダッシュボード）と、この PC の中だけで使う API。

扉 2 はここからは開けない（開けられるのは PC 側の PSoC 6 の SW2 だけ）。画面が頼めるのは扉 1 の訓練だけ。
画面に入るには、PC 側で出したワンタイムパスワード（auth.py）が要る。
"""
import json
import os
from datetime import timedelta
from urllib.parse import urlparse

from django.http import HttpResponse, HttpResponseRedirect, JsonResponse
from django.shortcuts import render
from django.utils import timezone
from django.views.decorators.http import require_GET, require_http_methods, require_POST

import layout
from door2 import ACCEPT_FROM, REPORT_COLLECTION, SHARE_LEVELS, compose_post, normalize_policy, notable, report_record

from . import auth, drive, rulebot, schedule
from .core_loop import save_event
from .models import Event, Gate, Live, Opinion, RelayTrust, Request, ShareSetting, Slot
from .outbound import build_snapshot, carried_opinions, rule_judgement, tags

FRESH_SECONDS = 3.0


class ContentSecurityPolicy:
    """画面と API には、外の読み込みを許さない CSP を付ける（管理画面は Django の既定のまま）。"""

    POLICY = ("default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; "
              "frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'")

    def __init__(self, get_response):
        self.get_response = get_response

    def __call__(self, request):
        response = self.get_response(request)
        if not request.path.startswith("/admin/"):
            response.setdefault("Content-Security-Policy", self.POLICY)
        return response


def _age(moment) -> float | None:
    return None if moment is None else round((timezone.now() - moment).total_seconds(), 1)


def _iso(moment) -> str | None:
    return None if moment is None else timezone.localtime(moment).isoformat(timespec="seconds")


@require_GET
def dashboard(request):
    return render(request, "guardian/dashboard.html")


def _safe_next(value: str) -> str:
    """入ったあとに戻る先。この画面の中の道だけ（外の住所や // で始まるものは使わない）。"""
    parsed = urlparse(value or "")
    if parsed.scheme or parsed.netloc or not (value or "").startswith("/") or value.startswith("//") or value.startswith("/login"):
        return "/"
    return value


@require_http_methods(["GET", "POST"])
def login(request):
    """合言葉で入る。合言葉は PC 側の画面（start.ps1・otp.ps1）にだけ出る。このページのソースには何も入れない。"""
    if auth.signed_in(request):
        return HttpResponseRedirect(_safe_next(request.GET.get("next", "/")))
    error = ""
    if request.method == "POST":
        ok, error = auth.check(request.POST.get("code", ""))
        if ok:
            auth.sign_in(request)
            Event.objects.create(source="door", kind="login", text="画面に入った（ワンタイムパスワード。この PC から）")
            return HttpResponseRedirect(_safe_next(request.POST.get("next", "/")))
        Event.objects.create(source="door", kind="login_failed", text="画面に入れなかった（合言葉が違う・期限切れ）")
    return render(request, "guardian/login.html", {"error": error, "next": _safe_next(request.GET.get("next", "/")),
                                                    "minutes": auth.minutes()}, status=401 if error else 200)


@require_POST
def logout(request):
    request.session.flush()
    return HttpResponseRedirect("/login")


@require_GET
def key_state(request):
    """PC 側の鍵（psoc6_key）だけが読む状態: 扉 2 とランプに要るものだけ。起動ごとの鍵（X-Robomori-Key）が要る。"""
    if not auth.host_key_ok(request):
        return JsonResponse({"error": "forbidden"}, status=403)
    gate = Gate.get()
    local = timezone.localtime()
    _, mode = schedule.current(local, list(Slot.objects.all()))
    waiting = Event.objects.filter(source__in=("ai", "dispatch"))
    if gate.last_closed_at:
        waiting = waiting.filter(at__gt=gate.last_closed_at)
    live, trust = Live.get(), RelayTrust.get()
    relay_age = _age(trust.last_sample_seen_at)
    return JsonResponse({"door2": {"state": gate.state, "pending": waiting.exists()},
                         "schedule": {"door2_allowed": mode in schedule.DOOR2_ALLOWED, "label": schedule.MODES[mode][0]},
                         "unity": {"connected": live.unity_connected, "stand_in": live.unity_boot.startswith("sim-")},
                         "relay": "none" if not trust.file else "fresh" if relay_age is not None and relay_age <= FRESH_SECONDS else "stale"},
                        json_dumps_params={"ensure_ascii": False})


@require_GET
def state(request):
    live, gate, trust = Live.get(), Gate.get(), RelayTrust.get()
    relay_age = _age(trust.last_sample_seen_at)
    relay = {
        "file": trust.file, "level": trust.last_level, "hold": trust.hold, "reason": trust.last_reason,
        "samples": trust.samples, "key2": trust.key2, "bad_lines": trust.bad_lines, "gaps": trust.gaps,
        "age": relay_age,
        "state": "none" if not trust.file else "fresh" if relay_age is not None and relay_age <= FRESH_SECONDS else "stale",
        "robot": trust.robot, "threshold_mg": trust.threshold_mg, "recent": trust.recent or [],
        "peak_mg": _recent_peak(trust, minutes=10),
        "events": [{"at": _iso(e.at), "kind": e.kind, "text": e.text}
                   for e in Event.objects.filter(source="relay").exclude(kind__in=("replay", "file"))[:6]],
    }
    since = timezone.now() - timedelta(hours=12)
    opinions = [{"at": _iso(o.at), "source": o.source, "text": o.text, "url": o.url, "detail": o.detail}
                for o in Opinion.objects.filter(at__gte=since)[:8]]
    events = [{"at": _iso(e.at), "source": e.source, "kind": e.kind, "text": e.text} for e in Event.objects.all()[:40]]
    slots = list(Slot.objects.all())
    local = timezone.localtime()
    slot, mode = schedule.current(local, slots)
    change = schedule.next_change(local, slots)
    timetable = {
        "mode": mode, "label": schedule.MODES[mode][0], "detail": schedule.MODES[mode][1],
        "span": f"{slot.start:%H:%M}-{slot.end:%H:%M}" if slot else "",
        "door2_allowed": mode in schedule.DOOR2_ALLOWED,
        "next": {"at": f"{change[0]:%H:%M}", "mode": change[1], "label": schedule.MODES[change[1]][0]} if change else None,
        "slots": [{"start": f"{s.start:%H:%M}", "end": f"{s.end:%H:%M}", "mode": s.mode, "label": schedule.MODES[s.mode][0],
                   "current": s == slot} for s in slots],
    }
    pending = Event.objects.filter(source__in=("ai", "dispatch"))
    if gate.last_closed_at:
        pending = pending.filter(at__gt=gate.last_closed_at)
    talk = [{"at": _iso(e.at), "robot": e.data.get("robot", ""), "question": e.data.get("question", ""),
             "answer": e.data.get("answer", ""), "by": e.data.get("by", "")} for e in Event.objects.filter(source="talk")[:8]]
    return JsonResponse({
        "now": _iso(timezone.now()),
        "unity": {"connected": live.unity_connected, "boot": live.unity_boot, "stand_in": live.unity_boot.startswith("sim-"),
                  "age": _age(live.last_telemetry_at), "rejected": live.rejected},
        "hub_age": _age(live.updated_at),
        "door1": {"state": live.door1, "reason": live.door1_reason},
        "door2": {"state": gate.state, "opened_by": gate.opened_by, "since": _iso(gate.changed_at),
                  "last": gate.last_result, "pending": pending.exists()},
        "schedule": timetable,
        "ai": live.ai_state, "robots": live.robots, "people": live.people, "seats": live.seats, "slowdowns": live.slowdowns,
        "proposals": live.proposals, "dispatch": live.dispatch, "relay": relay, "opinions": opinions, "events": events,
        "talk": talk, "scene": _scene_source(live),
    }, json_dumps_params={"ensure_ascii": False})


def _recent_peak(trust: RelayTrust, minutes: int) -> int:
    """直近 minutes 分の、いちばん大きな揺れ（mg）。中核が分ごとに覚えた最大から出す。"""
    since = int(timezone.now().timestamp() // 60) - minutes
    return max([p[1] for p in trust.peaks or [] if isinstance(p, list) and len(p) == 2 and p[0] > since] or [0])


def _scene_source(live: Live) -> dict:
    """いま画面が描く配置の出どころ。Unity（か代役）が送った配置が、いまつながっている起動のものなら、それを使う。"""
    scene = live.scene or {}
    if scene.get("things") and scene.get("boot") and scene.get("boot") == live.unity_boot:
        return {"source": "stand_in" if live.unity_boot.startswith("sim-") else "unity", "rev": live.scene_rev, "boot": live.unity_boot}
    return {"source": "copy", "rev": 0, "boot": ""}


@require_GET
def layout_view(request):
    """店内の配置。Unity がつないでいる間は、Unity のシーンにある物から読んだもの（GuardianHubLink の scene）。
    まだ届いていなければ、scripts/guardian_hub/layout.py の写し（source: copy）で描く。"""
    live = Live.get()
    where = _scene_source(live)
    body = dict(live.scene) if where["source"] != "copy" else layout.as_scene()
    body.update(source=where["source"], rev=where["rev"])
    return JsonResponse(body, json_dumps_params={"ensure_ascii": False})


@require_GET
def drive_list(request):
    """Google Drive 行きの記録の受信箱（書き出しフォルダを読むだけ）。"""
    return JsonResponse(drive.listing(), json_dumps_params={"ensure_ascii": False})


@require_GET
def drive_item(request, stamp: str):
    item = drive.message(stamp)
    if item is None:
        return JsonResponse({"error": "見つからない"}, status=404)
    return JsonResponse(item, json_dumps_params={"ensure_ascii": False})


@require_POST
def drill(request):
    """扉 1 の訓練（模擬）を hub に頼む。CSRF の確認つき（この画面からの操作だけ受け付ける）。"""
    if Request.objects.filter(kind="drill", done_at__isnull=True).exists():
        return JsonResponse({"queued": False, "reason": "前の訓練がまだ終わっていない"}, status=409)
    Request.objects.create(kind="drill")
    return JsonResponse({"queued": True}, status=202)


def _share_body(setting: ShareSetting) -> dict:
    """店が選んだこと、次に SW2 で扉 2 を開けたときに外へ出るもの（いまの設定とお店の様子で作る下書き）、残した記録。"""
    policy = normalize_policy(setting.policy())
    snapshot = build_snapshot(Gate.get())
    text = compose_post(snapshot, tags()) if notable(snapshot) else ""
    last = Opinion.objects.filter(source="record").first()
    previous = last.detail if last else None
    record = None
    if text and policy["share"] == "sealed":
        record = {"summary": "暗号化した店の記録。宛先の人だけが開ける", "share": "sealed",
                  "sealed": {"alg": "ECDH-P256+HKDF-SHA256+A256GCM", "ciphertext": "（中身は暗号化。外からは読めない）",
                             "recipients": [{"kid": r["kid"], "name（画面にだけ出す）": r["name"]} for r in policy["recipients"]]}}
    elif text and policy["share"] != "off":
        record = report_record(snapshot, text, None, os.environ.get("STORE_NAME", "ロボもり デモ店"), policy["share"], policy["accept"],
                               previous, carried_opinions())
        record["createdAt"] = "（送るときの時刻）"
        if policy["share"] == "post":
            record["post"] = "（投稿したあとに、その投稿を指す）"
    post = Opinion.objects.filter(source="post").first()
    records = [{"at": _iso(o.at), "uri": o.uri, "share": o.detail.get("share", "post"), "opinions": o.detail.get("opinions", 0)}
               for o in Opinion.objects.filter(source="record")[:5]]
    handle = (post.detail.get("handle") if post else "") or ""
    return {
        "setting": {**policy, "updated_at": _iso(setting.updated_at)},
        "choices": {"share": SHARE_LEVELS, "accept": ACCEPT_FROM},
        "preview": {"notable": bool(text), "post": text if policy["share"] == "post" else "", "record": record,
                    "gemini": policy["gemini"] and bool(text)},
        "records": records, "collection": REPORT_COLLECTION, "handle": handle,
        "repo": records[0]["uri"].split("/")[2] if records else "",
    }


@require_http_methods(["GET", "POST"])
def share_view(request):
    """扉 2 で外へ何を出し、誰の返信を受け取るか（店が選ぶ）。変えたら監査ログに残す。CSRF の確認つき。"""
    setting = ShareSetting.get()
    if request.method == "POST":
        try:
            body = json.loads(request.body.decode("utf-8") or "{}")
        except (UnicodeDecodeError, ValueError):
            return JsonResponse({"error": "読めない"}, status=400)
        if not isinstance(body, dict):
            return JsonResponse({"error": "読めない"}, status=400)
        recipients = body.get("recipients", setting.recipients)
        if isinstance(recipients, str):   # 1 行に 1 人:「名前 robomori-key:1:…」（名前は省ける）
            recipients = [{"name": " ".join(line.split()[:-1]), "key": line.split()[-1]} for line in recipients.splitlines() if line.split()]
        if not isinstance(recipients, list) or not all(isinstance(r, dict) for r in recipients):
            return JsonResponse({"error": "宛先は「名前 鍵」の並びで送る"}, status=400)
        allow = body.get("allow", setting.allow)
        if isinstance(allow, str):
            allow = [a for a in allow.replace(",", " ").split() if a]
        if not isinstance(allow, list) or not all(isinstance(a, str) for a in allow):
            return JsonResponse({"error": "指定した人は、DID かハンドルの並びで送る"}, status=400)
        wanted = {**setting.policy(), **{k: body[k] for k in ("share", "gemini", "accept", "window_days") if k in body}, "allow": allow,
                  "recipients": recipients}
        policy = normalize_policy(wanted)
        bad_keys = [str(r.get("key", ""))[:24] for r in recipients if str(r.get("key", "")).strip() not in {p["key"] for p in policy["recipients"]}]
        if bad_keys:
            return JsonResponse({"error": "鍵の形が違う（robomori-key:1:…）", "dropped": bad_keys[:5]}, status=400)
        dropped = [a for a in (allow if isinstance(allow, list) else []) if isinstance(a, str) and a.strip().lstrip("@") not in policy["allow"]]
        if (wanted.get("share") not in SHARE_LEVELS or wanted.get("accept") not in ACCEPT_FROM or not isinstance(wanted.get("gemini"), bool)
                or dropped or type(wanted.get("window_days")) is not int or policy["window_days"] != wanted["window_days"]):
            return JsonResponse({"error": "選べない値がある", "dropped": dropped[:5]}, status=400)
        before = setting.policy()
        setting.share, setting.gemini, setting.accept = policy["share"], policy["gemini"], policy["accept"]
        setting.allow, setting.window_days, setting.updated_at = policy["allow"], policy["window_days"], timezone.now()
        setting.recipients = [{"name": r["name"], "key": r["key"]} for r in policy["recipients"]]
        setting.save()
        if before != setting.policy():
            save_event("share", "setting", f"外への共有の設定を変えた: {SHARE_LEVELS[setting.share]}、Gemini に{'聞く' if setting.gemini else '聞かない'}、"
                       f"返信は{ACCEPT_FROM[setting.accept]}から（{setting.window_days} 日まで"
                       + (f"、指定 {len(setting.allow)} 人" if setting.accept == "listed" else "") + "）"
                       + (f"。会社だけの宛先 {len(setting.recipients)} 人" if setting.share == "sealed" else ""))
    return JsonResponse(_share_body(setting), json_dumps_params={"ensure_ascii": False})


def _rule_result() -> tuple[dict, dict, str]:
    rules, result, problem, _ = rule_judgement()
    return rules, result, problem


@require_GET
def judge_view(request):
    """ルール AI のいまの判断（ロボットごと・店全体、根拠の数字つき）。"""
    try:
        rules, result, problem = _rule_result()
    except (OSError, ValueError) as error:
        return JsonResponse({"error": f"rules.json を読めない（{error}）"}, status=500)
    return JsonResponse({**result, "levels": rulebot._words(rules), "problem": problem}, json_dumps_params={"ensure_ascii": False})


@require_POST
def ask_view(request):
    """ルール AI に聞く。答えは rules.json の数字だけで決める（外部の AI は使わない）。問いと答えは監査ログに残す。"""
    try:
        body = json.loads(request.body.decode("utf-8") or "{}")
    except (UnicodeDecodeError, ValueError):
        return JsonResponse({"error": "読めない"}, status=400)
    question = body.get("q") if isinstance(body, dict) else None
    if not isinstance(question, str) or not question.strip() or len(question) > 200:
        return JsonResponse({"error": "1〜200 文字で聞いてください"}, status=400)
    try:
        rules, result, problem = _rule_result()
    except (OSError, ValueError) as error:
        return JsonResponse({"error": f"rules.json を読めない（{error}）"}, status=500)
    reply = rulebot.answer(question.strip(), rules, result)
    save_event("rulebot", "ask", f"Q: {question.strip()} / A: {reply['answer'].splitlines()[0]}",
               {"intent": reply["intent"], "level": reply["level"], "robot": reply["robot"]})
    return JsonResponse({**reply, "problem": problem, "levels": rulebot._words(rules)}, json_dumps_params={"ensure_ascii": False})


@require_GET
def healthz(request):
    return HttpResponse("ok", content_type="text/plain")
