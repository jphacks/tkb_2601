"""中核の hub を動かす: python manage.py runhub

Unity（GuardianHubLink）か代役（simrobots）が 47612 につないでくる。Docker では閉じたネットワークの中で 0.0.0.0 に
開き、門番（nginx）が 127.0.0.1:47612 だけを中へ通す。

Unity の行を読んで助言を返すのはこのスレッドだけで、DB には触らない。記録（SQLite）は別のスレッド（records）が書く。
"""
import os
import queue
import signal
import threading
import time
from pathlib import Path

from django.core.management.base import BaseCommand
from django.db import connection

from hub import UnityServer
from monitor import Monitor

from guardian.core_loop import CoreHub, CoreLoop, save_event
from guardian.relay import RelayTail

EVENT_LIMIT = 256
RECORD_PERIOD = 0.1


class Command(BaseCommand):
    help = "監視 AI の hub を動かす（Unity か代役から 47612 で様子を受け取り、助言を返す）"

    def handle(self, *args, **options):
        port = int(os.environ.get("HUB_PORT", "47612"))
        bind = os.environ.get("HUB_BIND", "127.0.0.1")
        monitor = Monitor(learn_seconds=float(os.environ.get("HUB_LEARN_SECONDS", "20")),
                          relax_after=float(os.environ.get("HUB_RELAX_AFTER", "120")),
                          simulated_bumps=tuple(b for b in os.environ.get("HUB_SIMULATE_BUMP", "").split(",") if b))
        events: queue.Queue = queue.Queue(maxsize=EVENT_LIMIT)   # 行が押し寄せても、読む側が待つだけで増え続けない
        stop = threading.Event()
        server = UnityServer(port, events, stop, host=bind)
        threading.Thread(target=server.accept_forever, name="unity", daemon=True).start()
        relay_dir = Path(os.environ.get("RELAY_LOG_DIR", "/relay"))
        relay = RelayTail(relay_dir) if relay_dir.is_dir() else None
        hub = CoreHub(monitor)
        loop = CoreLoop(hub, server.send, lambda: server.connected, relay, server.set_accepting)
        save_event("hub", "start", f"中核の hub を開始（{bind}:{port}。センサー中継の 47610 は使わない）"
                   + ("" if relay else "。センサー中継の記録は見つからないので、読まない"))

        def stop_now(signum, frame):
            raise KeyboardInterrupt

        def records():
            try:
                while not stop.is_set():
                    loop.slow_tick(time.monotonic())
                    stop.wait(RECORD_PERIOD)
            finally:
                connection.close()

        signal.signal(signal.SIGTERM, stop_now)
        threading.Thread(target=records, name="records", daemon=True).start()
        try:
            while True:
                try:
                    kind, payload = events.get(timeout=0.05)
                except queue.Empty:
                    kind, payload = None, None
                now = time.monotonic()
                if kind is not None:
                    loop.on_event(kind, payload, now)
                loop.fast_tick(now)
        except KeyboardInterrupt:
            pass
        finally:
            stop.set()
            server.close()
            loop.recorder.flush()
            save_event("hub", "stop", "中核の hub を止めた")
