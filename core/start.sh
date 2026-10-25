#!/bin/sh
# ロボもり 中核を立ち上げ、PSoC 6 の鍵と Google Drive へ写す処理を動かす（Mac / Linux）。
#
#   ./core/start.sh              Unity を使うとき
#   ./core/start.sh --sim        Unity の代役で見せるとき（Unity と同時には動かさない）
#   ./core/start.sh --dry-run    扉 2 を開けても Gemini・Bluesky には送らない（ほかの引数も psoc6_key.py に渡す）
#   ROBOMORI_NO_LISTEN=1 ./core/start.sh   画面を 127.0.0.1 だけで出す（この PC の IPv4 では出さない）
#
# 画面にはワンタイムパスワードで入る（ここに出る。新しく出すとき: cd core && docker compose exec web python manage.py issue_otp）。
# 画面はこの PC の IPv4 でも出す（psoc6_key.py の HostOnlyRelay。ほかの PC からの接続はすぐ切る）。
# 起動ごとに、PSoC 6 の鍵と check_pc.py だけが使う鍵を作る（Logs/.robomori-host-key）。
#
# Ctrl+C で止まるのは PSoC 6 の鍵（と画面の中継）だけで、中核は動いたまま（Unity の本線には関係しない）。
# 中核も止めるとき: cd core && docker compose --profile sim down
set -eu
cd "$(dirname "$0")"

profile=""
if [ "${1:-}" = "--sim" ]; then
  profile="--profile sim"
  shift
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker が見つからない。Docker Desktop を起動してから、もう一度"
  exit 1
fi

python=python3
command -v python3 >/dev/null 2>&1 || python=python

# 起動ごとの鍵と、この PC の IPv4（UDP の connect は何も送らない）
ROBOMORI_HOST_KEY=$("$python" -c 'import secrets; print(secrets.token_hex(32))')
if [ "${ROBOMORI_NO_LISTEN:-}" = "1" ]; then
  ROBOMORI_HOST_IP=""
else
  ROBOMORI_HOST_IP=$("$python" - <<'EOF2'
import socket
try:
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.connect(("192.0.2.1", 9))
        address = probe.getsockname()[0]
    print("" if address.startswith("127.") else address)
except OSError:
    print("")
EOF2
)
fi
ROBOMORI_HOST_PORT=8000
export ROBOMORI_HOST_KEY ROBOMORI_HOST_IP ROBOMORI_HOST_PORT

# shellcheck disable=SC2086
docker compose $profile up -d

# 中核が答えるまで待つ（最大 60 秒）。答えなくても Unity の本線は動く
if ! "$python" - <<'EOF2'
import sys, time, urllib.request
for _ in range(60):
    try:
        urllib.request.urlopen("http://127.0.0.1:8000/healthz", timeout=2)
        sys.exit(0)
    except OSError:
        time.sleep(1)
sys.exit(1)
EOF2
then
  echo "中核が答えない（cd core && docker compose logs web hub）"
  exit 1
fi

mkdir -p ../Logs
umask 077
printf '%s' "$ROBOMORI_HOST_KEY" > ../Logs/.robomori-host-key
pass=$(docker compose exec -T web python manage.py issue_otp --plain 2>/dev/null | tail -n 1 || true)
if [ -n "$ROBOMORI_HOST_IP" ]; then screen="http://$ROBOMORI_HOST_IP:8000"; else screen="http://127.0.0.1:8000"; fi
echo ""
echo "  画面:                 $screen（この PC からだけ）"
echo "  ワンタイムパスワード: ${pass:-（出せなかった。cd core && docker compose exec web python manage.py issue_otp）}"
echo "  （1 回だけ・10 分。新しく出すとき: cd core && docker compose exec web python manage.py issue_otp）"
sign_in=""
if printf '%s' "$pass" | grep -Eq '^[A-Z2-9]{4}-[A-Z2-9]{4}$' && [ "${ROBOMORI_NO_BROWSER:-}" != "1" ]; then
  sign_in="/login#code=$pass"   # # の後ろはサーバーに送られない。ブラウザが開いて、自動で入る
  echo "  ブラウザで画面を開き、このパスワードで自動で入ります（開かないとき: ROBOMORI_NO_BROWSER=1）"
fi
echo ""

if [ -n "$ROBOMORI_HOST_IP" ]; then
  exec "$python" host/psoc6_key.py --drive-folder auto --host-key "$ROBOMORI_HOST_KEY" --listen "$ROBOMORI_HOST_IP" --open-screen "$sign_in" "$@"
fi
exec "$python" host/psoc6_key.py --drive-folder auto --host-key "$ROBOMORI_HOST_KEY" --open-screen "$sign_in" "$@"
