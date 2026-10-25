"""画面に入るためのワンタイムパスワードと、PC 側の鍵（psoc6_key）だけが読む状態。

- 合言葉（ワンタイムパスワード）は PC 側で出す（start.ps1 か core\\host\\otp.ps1 → python manage.py issue_otp）。
  画面には出さず、ページのソース（F12）にも入れない。DB にはハッシュだけを置く。
- 1 回使ったら使えない。期限（既定 10 分）が来ても、続けて 5 回間違えても使えない。新しく出すと前のものは取り消す。
- 入ったあとは、ブラウザを閉じるまで（長くても 8 時間）のセッション。クッキーは HttpOnly・SameSite=Strict。
- 合言葉なしで開けるのは、入口のページ・その見た目と自動で入る小さなスクリプト・死活確認（/healthz）・PC 側の鍵の口（/api/key）だけ。
- PC 側は、出した合言葉を「/login#code=…」としてブラウザに渡す（# の後ろはサーバーに送られない）。入口のスクリプトが
  それを読んで送り、すぐ住所から消す。合言葉そのものは、ページにも記録にも残らない。
  画面の HTML・JavaScript・データは、すべて入ってから。
"""
from __future__ import annotations

import hashlib
import hmac
import os
import secrets
import time
from datetime import timedelta
from urllib.parse import quote

from django.db import transaction
from django.http import HttpResponseRedirect, JsonResponse
from django.utils import timezone

from .models import Pass

ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"   # 見間違えやすい 0・O・1・I・L は使わない
LENGTH = 8                                      # 31^8 ≒ 8.5 × 10^11 通り
TRIES = 5                                       # 続けて間違えられる回数。超えたら、その合言葉は使えない
SESSION_KEY = "robomori_pass"
SESSION_SECONDS = 8 * 60 * 60
FAIL_DELAY = 0.4                                # 間違えたときに待たせる秒数（総当たりを遅くする）
OPEN_PATHS = {"/login", "/healthz", "/api/key", "/static/guardian/login.css", "/static/guardian/login.js"}
HOST_KEY_HEADER = "X-Robomori-Key"


def minutes() -> int:
    try:
        return max(1, min(60, int(os.environ.get("ROBOMORI_PASS_MINUTES", "10"))))
    except ValueError:
        return 10


def normalize(text: str) -> str:
    """打ち間違いを許す: 大文字にし、空白とハイフンを除く。"""
    return "".join(ch for ch in (text or "").upper() if ch.isalnum())[:32]


def _digest(code: str, salt: str) -> str:
    return hashlib.scrypt(code.encode("ascii", "ignore"), salt=bytes.fromhex(salt), n=2 ** 14, r=8, p=1, dklen=32).hex()


def issue(valid_minutes: int | None = None) -> tuple[str, Pass]:
    """新しい合言葉を出す。まだ使っていない前の合言葉は取り消す。平文はここで返すだけで、どこにも残さない。"""
    code = "".join(secrets.choice(ALPHABET) for _ in range(LENGTH))
    salt = secrets.token_hex(16)
    now = timezone.now()
    with transaction.atomic():
        Pass.objects.filter(used_at__isnull=True).update(used_at=now)
        item = Pass.objects.create(expires_at=now + timedelta(minutes=valid_minutes or minutes()), salt=salt, digest=_digest(code, salt))
    return code, item


def display(code: str) -> str:
    return f"{code[:4]}-{code[4:]}"


def check(text: str) -> tuple[bool, str]:
    """合言葉を確かめる。合えば使い切りにする。理由は人に見せる短い文。"""
    code = normalize(text)
    now = timezone.now()
    with transaction.atomic():
        item = Pass.objects.select_for_update().filter(used_at__isnull=True).first()
        if item is None or item.expires_at <= now:
            if item is not None:
                item.used_at = now
                item.save(update_fields=["used_at"])
            time.sleep(FAIL_DELAY)
            return False, "使える合言葉がありません（期限切れか、もう使われました）。PC の画面で新しく出してください"
        if len(code) == LENGTH and hmac.compare_digest(_digest(code, item.salt), item.digest):
            item.used_at = now
            item.save(update_fields=["used_at"])
            return True, ""
        item.failures += 1
        if item.failures >= TRIES:
            item.used_at = now
        item.save(update_fields=["failures", "used_at"])
        left = TRIES - item.failures
    time.sleep(FAIL_DELAY)
    if left <= 0:
        return False, "続けて間違えたので、この合言葉は使えなくなりました。PC の画面で新しく出してください"
    return False, f"合言葉が違います（あと {left} 回）"


def signed_in(request) -> bool:
    return bool(request.session.get(SESSION_KEY))


def sign_in(request) -> None:
    request.session.cycle_key()   # 入る前のセッション ID を使い回さない
    request.session[SESSION_KEY] = timezone.now().isoformat(timespec="seconds")
    request.session.set_expiry(SESSION_SECONDS)


def host_key_ok(request) -> bool:
    """PC 側の鍵（psoc6_key.ps1 / .py）だけが知る値。start.ps1 が起動ごとに作って、コンテナと鍵に渡す。"""
    expected = os.environ.get("ROBOMORI_HOST_KEY", "")
    given = request.headers.get(HOST_KEY_HEADER, "")
    return len(expected) >= 32 and hmac.compare_digest(given.encode(), expected.encode())


class RequirePass:
    """合言葉で入っていなければ、入口のページへ（API は 401）。HTML と API はブラウザに残させない（no-store）。"""

    def __init__(self, get_response):
        self.get_response = get_response

    def __call__(self, request):
        request.get_host()   # 知らない名前（Host）で来たものは、何より先に 400 で断る（DisallowedHost）
        path = request.path
        if path not in OPEN_PATHS and not signed_in(request):
            if path.startswith("/api/"):
                response = JsonResponse({"error": "合言葉で入ってください"}, status=401)
            else:
                response = HttpResponseRedirect("/login" + ("?next=" + quote(path, safe="/") if path not in ("/", "/logout") else ""))
        else:
            response = self.get_response(request)
        if not path.startswith("/static/"):
            response["Cache-Control"] = "no-store"
        if request.get_host().split(":")[0] in ("127.0.0.1", "localhost"):
            # http の IPv4 の住所では、ブラウザはこの見出しを使わず F12 に誤りとして出すだけなので、127.0.0.1 でだけ付ける
            response.setdefault("Cross-Origin-Opener-Policy", "same-origin")
        return response
