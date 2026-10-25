#!/usr/bin/env bash
# Robomori core dashboard: end-to-end screenshot capture WITHOUT Docker.
#
# What it does (all on 127.0.0.1, nothing goes to the internet):
#   1. makes a fresh work dir (SQLite DB, static files, export folder, logs)
#   2. migrate + collectstatic, registers a demo "company only (sealed)" recipient (its private key is discarded),
#      and sets a timetable around "now": store -> export (1 min) -> share -> standalone (overnight)
#   3. starts hub (runhub), the Unity stand-in (simrobots) and web (gunicorn) as plain processes
#   4. issues a one-time password and hands it to capture.js (Playwright), which logs in via /login#code=...,
#      waits for live data (AI slowdown, scheduled export, stopped robot + recommendation), runs ONE dry-run door2
#      (no Gemini / Bluesky: clients are never created), then saves the PNGs
#   5. stops everything it started (also on Ctrl+C / errors)
#
# Usage (from anywhere):
#   PY=/path/to/venv/bin/python REPO=/path/to/tkb_2601 ./capture.sh
#
# Env knobs (all optional):
#   PY               python with core/requirements.txt installed        (default: python3)
#   REPO             repository root                                     (default: git toplevel of $PWD)
#   SHOTS            where the PNGs go                                   (default: <script dir>/shots)
#   WORK             work dir (DB, logs). Created fresh if not given     (default: <script dir>/work.XXXXXX)
#   WEB_PORT         dashboard port                                      (default: 8000)
#   HUB_PORT         hub port for the stand-in                           (default: 47612)
#   TZ               time zone for Django and the browser                (default: Asia/Tokyo)
#   CAPTURE_TIMEOUT  max seconds to wait for live data after login       (default: 480)
#   NO_TIMETABLE=1   leave the timetable empty (all day "share"; no scheduled export in the inbox)
#   NO_DOOR2=1       do not run the dry-run door2 (inbox then only has the scheduled export)
#   NO_VIEWER=1      skip the core/viewer/index.html screenshot
#   KEEP_WORK=1      keep the work dir (logs + DB) after the run (default: logs kept, DB removed)
#
# Assumptions: bash, Python 3.10+ with Django 5.2 / gunicorn / whitenoise / cryptography / tzdata,
# Node 18+ with the "playwright" package resolvable by require() and its Chromium already installed.
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
PY=${PY:-python3}
REPO=${REPO:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}
CORE="$REPO/core"
SHOTS=${SHOTS:-"$SCRIPT_DIR/shots"}
WEB_PORT=${WEB_PORT:-8000}
HUB_PORT=${HUB_PORT:-47612}
export TZ=${TZ:-Asia/Tokyo}

[ -f "$CORE/manage.py" ] || { echo "REPO=$REPO does not look like the repo (no core/manage.py)"; exit 1; }
command -v node >/dev/null || { echo "node not found"; exit 1; }
node -e "require('playwright')" 2>/dev/null || { echo "node cannot require('playwright') (npm i playwright; npx playwright install chromium)"; exit 1; }
"$PY" -c "import django, gunicorn, whitenoise, cryptography" 2>/dev/null || { echo "$PY lacks core/requirements.txt packages"; exit 1; }

if [ -z "${WORK:-}" ]; then WORK=$(mktemp -d "$SCRIPT_DIR/work.XXXXXX"); else mkdir -p "$WORK"; fi
LOGS="$WORK/logs"
mkdir -p "$SHOTS" "$LOGS" "$WORK/data" "$WORK/export"
echo "work dir: $WORK"

# ---- environment for every Python process --------------------------------------------------------
# No credentials ever reach the app; any accidental outbound HTTP(S) goes to a dead local port.
unset GEMINI_API_KEY BLUESKY_HANDLE BLUESKY_APP_PASSWORD BLUESKY_SERVICE BLUESKY_APPVIEW ROBOMORI_HOST_KEY ROBOMORI_HOST_IP || true
export HTTPS_PROXY=http://127.0.0.1:9 HTTP_PROXY=http://127.0.0.1:9 ALL_PROXY=http://127.0.0.1:9
export https_proxy=$HTTPS_PROXY http_proxy=$HTTP_PROXY all_proxy=$ALL_PROXY
export NO_PROXY=127.0.0.1,localhost no_proxy=127.0.0.1,localhost
export DOOR2_DRY_RUN=1
export PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1   # do not write __pycache__ into the repo
export DJANGO_DEBUG=0
export ROBOMORI_DATA="$WORK/data" ROBOMORI_STATIC="$WORK/static" GUARDIAN_LIB="$REPO/scripts"
export EXPORT_DIR="$WORK/export" RELAY_LOG_DIR="$WORK/no-sensor-bridge"   # no M5 relay log in this run
export ROBOMORI_HOST_PORT=$WEB_PORT
export HUB_BIND=127.0.0.1 HUB_HOST=127.0.0.1 HUB_PORT
export HUB_LEARN_SECONDS=${HUB_LEARN_SECONDS:-10} HUB_RELAX_AFTER=${HUB_RELAX_AFTER:-60}
export STORE_NAME=${STORE_NAME:-ロボもり デモ店}
# per-run secret key, kept only in this process environment (no secret_key file is written)
DJANGO_SECRET_KEY=$("$PY" -c 'import secrets; print(secrets.token_urlsafe(50))')
export DJANGO_SECRET_KEY

port_free() {
  "$PY" - "$1" <<'EOF'
import socket, sys
port = int(sys.argv[1])
try:   # something already answers there
    socket.create_connection(("127.0.0.1", port), timeout=1).close()
    sys.exit(1)
except OSError:
    pass
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)   # ignore TIME_WAIT left by a previous run
try:
    s.bind(("127.0.0.1", port))
except OSError:
    sys.exit(1)
finally:
    s.close()
EOF
}
port_free "$WEB_PORT" || { echo "127.0.0.1:$WEB_PORT is busy (set WEB_PORT)"; exit 1; }
port_free "$HUB_PORT" || { echo "127.0.0.1:$HUB_PORT is busy (set HUB_PORT)"; exit 1; }

PIDS=""
cleanup() {
  status=$?
  trap - EXIT INT TERM
  if [ -n "$PIDS" ]; then
    echo "stopping: $PIDS"
    kill $PIDS 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
      alive=""
      for p in $PIDS; do kill -0 "$p" 2>/dev/null && alive="$alive $p"; done
      [ -z "$alive" ] && break
      sleep 1
    done
    [ -n "${alive:-}" ] && kill -9 $alive 2>/dev/null || true
  fi
  if [ "${KEEP_WORK:-}" != "1" ]; then rm -rf "$WORK/data" "$WORK/static" "$WORK/fontcache"; fi
  exit $status
}
trap cleanup EXIT
trap 'exit 130' INT TERM

cd "$CORE"
manage() { "$PY" manage.py "$@"; }

echo "== migrate / collectstatic"
manage migrate --noinput --verbosity 0
manage collectstatic --noinput --verbosity 0

echo "== demo recipient for 'company only (sealed)' (private key discarded)"
manage demo_setup --name "保守会社（デモ）" | sed -n 1p   # 2nd line = private JWK, dropped

if [ "${NO_TIMETABLE:-}" != "1" ]; then
  SPEC=$("$PY" - <<'EOF'
import datetime as dt, os
from zoneinfo import ZoneInfo
now = dt.datetime.now(ZoneInfo(os.environ["TZ"]))
base = now.replace(second=0, microsecond=0)
# the export minute starts >= 45 s from now, so the AI has slowed something down before the summary is written
k = 1 if (base + dt.timedelta(minutes=1) - now).total_seconds() >= 45 else 2
f = lambda m: (base + dt.timedelta(minutes=m)).strftime("%H:%M")
print(f"{f(-180)}-{f(k)} store, {f(k)}-{f(k + 1)} export, {f(k + 1)}-{f(360)} share, {f(360)}-{f(-180)} standalone")
EOF
)
  echo "== timetable: $SPEC"
  manage timetable --set "$SPEC" >/dev/null
fi

echo "== start hub / sim / web"
"$PY" manage.py runhub >"$LOGS/hub.log" 2>&1 &
PIDS="$PIDS $!"
sleep 1
"$PY" manage.py simrobots >"$LOGS/sim.log" 2>&1 &
PIDS="$PIDS $!"
NO_CTL=""   # gunicorn 26 otherwise opens a control socket under ~/.gunicorn
if "$PY" -m gunicorn --help 2>/dev/null | grep -- --no-control-socket >/dev/null; then NO_CTL=--no-control-socket; fi
"$PY" -m gunicorn robomori.wsgi --bind "127.0.0.1:$WEB_PORT" --workers 2 --threads 4 $NO_CTL \
  --access-logfile "$LOGS/web-access.log" >"$LOGS/web.log" 2>&1 &
PIDS="$PIDS $!"

"$PY" - "$WEB_PORT" <<'EOF'
import sys, time, urllib.request
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
for _ in range(60):
    try:
        opener.open(f"http://127.0.0.1:{sys.argv[1]}/healthz", timeout=2)
        sys.exit(0)
    except OSError:
        time.sleep(1)
sys.exit("web did not answer /healthz")
EOF
echo "web is up on http://127.0.0.1:$WEB_PORT"

# ---- fonts: prefer the Noto Sans JP the repo bundles (Linux/fontconfig only; macOS has Hiragino) -----
FONT_DIR="$REPO/Assets/Art/Fonts/Resources"
if [ "$(uname -s)" = "Linux" ] && [ -f "$FONT_DIR/NotoSansJP-Regular.otf" ]; then
  mkdir -p "$WORK/fontcache"
  cat >"$WORK/fonts.conf" <<EOF
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "fonts.dtd">
<fontconfig>
  <include ignore_missing="yes">/etc/fonts/fonts.conf</include>
  <dir>$FONT_DIR</dir>
  <cachedir>$WORK/fontcache</cachedir>
</fontconfig>
EOF
  export FONTCONFIG_FILE="$WORK/fonts.conf"
fi

# ---- one-time password -> Playwright (env only; never printed or written to a file) -------------
ROBOMORI_OTP=$(manage issue_otp --plain | tail -n 1)
case "$ROBOMORI_OTP" in
  ????-????) ;;
  *) echo "could not issue a one-time password"; exit 1 ;;
esac

echo "== capture (log: $LOGS/capture.log)"
BASE_URL="http://127.0.0.1:$WEB_PORT" SHOTS="$SHOTS" LOGS="$LOGS" PY="$PY" CORE_DIR="$CORE" \
  VIEWER_HTML="$CORE/viewer/index.html" CAPTURE_TIMEOUT="${CAPTURE_TIMEOUT:-480}" \
  EXPECT_EXPORT=$([ "${NO_TIMETABLE:-}" = "1" ] && echo 0 || echo 1) \
  RUN_DOOR2=$([ "${NO_DOOR2:-}" = "1" ] && echo 0 || echo 1) NO_VIEWER="${NO_VIEWER:-}" \
  ROBOMORI_OTP="$ROBOMORI_OTP" node "$SCRIPT_DIR/capture.js" 2>&1 | tee "$LOGS/capture.log"
unset ROBOMORI_OTP
echo "PNGs in $SHOTS"
ls -1 "$SHOTS"
