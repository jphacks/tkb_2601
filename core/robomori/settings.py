"""Robomori core（Docker の中核）の設定。

インターネットに出ない前提で、この PC の中（127.0.0.1）からだけ使う。秘密の鍵はリポジトリに置かず、
環境変数か、データの置き場（/data）に初回だけ作る。
"""
import ipaddress
import os
import secrets
import sys
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent.parent
DATA_DIR = Path(os.environ.get("ROBOMORI_DATA", BASE_DIR / "data"))
# 監視ハブの部品（scripts/guardian_hub、scripts/guardian_share）をそのまま使う。Docker では /app/lib に入れてある
GUARDIAN_LIB = Path(os.environ.get("GUARDIAN_LIB", BASE_DIR.parent / "scripts"))
for folder in ("guardian_share", "guardian_hub"):
    if str(GUARDIAN_LIB / folder) not in sys.path:
        sys.path.insert(0, str(GUARDIAN_LIB / folder))


def _secret_key() -> str:
    if os.environ.get("DJANGO_SECRET_KEY"):
        return os.environ["DJANGO_SECRET_KEY"]
    path = DATA_DIR / "secret_key"
    try:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        try:
            descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        except FileExistsError:
            return path.read_text(encoding="ascii").strip()
        key = secrets.token_urlsafe(50)
        with os.fdopen(descriptor, "w", encoding="ascii") as file:
            file.write(key)
        return key
    except OSError:
        return secrets.token_urlsafe(50)  # 書けない場所（イメージを作る途中など）では、その場限りの鍵


SECRET_KEY = _secret_key()
DEBUG = os.environ.get("DJANGO_DEBUG") == "1"
# 門番（nginx）が 127.0.0.1:8000 で受けて中へ渡す。外の名前では受け付けない
ALLOWED_HOSTS = ["127.0.0.1", "localhost", "web"]
CSRF_TRUSTED_ORIGINS = ["http://127.0.0.1:8000", "http://localhost:8000"]


def _host_ip() -> str:
    """この PC の IPv4（start.ps1 が渡す）。PC 側の host-only の中継がこの住所で受け、この PC からの接続だけを通す。
    私設の IPv4（10.x・172.16-31.x・192.168.x・169.254.x）だけを受け付ける。"""
    value = os.environ.get("ROBOMORI_HOST_IP", "").strip()
    try:
        address = ipaddress.IPv4Address(value)
    except ValueError:
        return ""
    return value if (address.is_private or address.is_link_local) and not address.is_loopback else ""


HOST_IP = _host_ip()
HOST_PORT = os.environ.get("ROBOMORI_HOST_PORT", "8000").strip()
if HOST_IP and HOST_PORT.isdigit() and 0 < int(HOST_PORT) < 65536:
    ALLOWED_HOSTS.append(HOST_IP)
    CSRF_TRUSTED_ORIGINS.append(f"http://{HOST_IP}:{HOST_PORT}")

INSTALLED_APPS = [
    "django.contrib.admin",
    "django.contrib.auth",
    "django.contrib.contenttypes",
    "django.contrib.sessions",
    "django.contrib.messages",
    "django.contrib.staticfiles",
    "guardian",
]
MIDDLEWARE = [
    "django.middleware.security.SecurityMiddleware",
    "django.contrib.sessions.middleware.SessionMiddleware",
    # 合言葉（ワンタイムパスワード）で入るまでは、画面の HTML・JavaScript・データを返さない（静的ファイルも）
    "guardian.auth.RequirePass",
    "whitenoise.middleware.WhiteNoiseMiddleware",
    "django.middleware.common.CommonMiddleware",
    "django.middleware.csrf.CsrfViewMiddleware",
    "django.contrib.auth.middleware.AuthenticationMiddleware",
    "django.contrib.messages.middleware.MessageMiddleware",
    "django.middleware.clickjacking.XFrameOptionsMiddleware",
    "guardian.views.ContentSecurityPolicy",
]
ROOT_URLCONF = "robomori.urls"
TEMPLATES = [{
    "BACKEND": "django.template.backends.django.DjangoTemplates",
    "DIRS": [],
    "APP_DIRS": True,
    "OPTIONS": {"context_processors": [
        "django.template.context_processors.request",
        "django.contrib.auth.context_processors.auth",
        "django.contrib.messages.context_processors.messages",
    ]},
}]
WSGI_APPLICATION = "robomori.wsgi.application"

# 記録は SQLite 1 ファイル。web・hub・door2 が同じ置き場（/data）を使う（WAL は apps.py で有効にする）
DATABASES = {"default": {
    "ENGINE": "django.db.backends.sqlite3",
    "NAME": DATA_DIR / "robomori.sqlite3",
    "OPTIONS": {"timeout": 10},
}}
DEFAULT_AUTO_FIELD = "django.db.models.BigAutoField"

LANGUAGE_CODE = "ja"
TIME_ZONE = os.environ.get("TZ", "Asia/Tokyo")
USE_I18N = True
USE_TZ = True

STATIC_URL = "static/"
STATIC_ROOT = Path(os.environ.get("ROBOMORI_STATIC", BASE_DIR / "staticfiles"))

# 外から読まれる・埋め込まれる・推測されることを減らす
SECURE_CONTENT_TYPE_NOSNIFF = True
SECURE_REFERRER_POLICY = "same-origin"
SECURE_CROSS_ORIGIN_OPENER_POLICY = None   # guardian.auth.RequirePass が 127.0.0.1 でだけ same-origin を付ける
X_FRAME_OPTIONS = "DENY"
SESSION_COOKIE_HTTPONLY = True
SESSION_COOKIE_SAMESITE = "Strict"
SESSION_COOKIE_NAME = "robomori_session"
SESSION_COOKIE_AGE = 8 * 60 * 60          # 合言葉で入ってから長くても 8 時間
SESSION_EXPIRE_AT_BROWSER_CLOSE = True    # ブラウザを閉じたら入り直す
CSRF_COOKIE_HTTPONLY = True
CSRF_COOKIE_SAMESITE = "Strict"

LOGGING = {
    "version": 1,
    "disable_existing_loggers": False,
    "handlers": {"console": {"class": "logging.StreamHandler"}},
    "root": {"handlers": ["console"], "level": "WARNING"},
}
