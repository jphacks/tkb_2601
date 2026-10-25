"""Unity の代役を動かす: python manage.py simrobots

hub（HUB_HOST:HUB_PORT、既定 127.0.0.1:47612）に Unity と同じようにつなぎ、0.5 秒ごとに 3 台の様子を送る。
Unity と同時には動かさない（hub が受ける接続は 1 本だけ）。
"""
import os
import socket
import time

from django.core.management.base import BaseCommand

from guardian.core_loop import save_event
from guardian.sim import StandIn


class Command(BaseCommand):
    help = "Unity の代役（模擬データ）として hub に様子を送る"

    def handle(self, *args, **options):
        host = os.environ.get("HUB_HOST", "127.0.0.1")
        port = int(os.environ.get("HUB_PORT", "47612"))
        sim = StandIn()
        start = time.monotonic()
        save_event("sim", "start", f"Unity の代役を開始（{sim.boot}。送るのは模擬データ）")
        while True:
            now = time.monotonic()
            if sim.closed(now):
                time.sleep(0.5)
                continue
            try:
                with socket.create_connection((host, port), timeout=2) as connection:
                    connection.settimeout(0.05)
                    self.stdout.write(f"hub {host}:{port} につないだ")
                    connection.sendall(sim.scene_line())
                    buffer, last = b"", time.monotonic()
                    while not sim.closed(time.monotonic()):
                        now = time.monotonic()
                        if now - last >= 0.5:
                            connection.sendall(sim.tick(now - start, now - last))
                            last = now
                        try:
                            chunk = connection.recv(65536)
                            if not chunk:
                                break
                            buffer += chunk
                        except socket.timeout:
                            pass
                        while b"\n" in buffer:
                            line, buffer = buffer.split(b"\n", 1)
                            sim.on_advice(line, now)
                        for note in sim.notes:
                            self.stdout.write(note)
                            save_event("sim", "note", "代役: " + note)
                        sim.notes.clear()
            except OSError as error:
                self.stdout.write(f"hub につながらない（{error}）。1 秒後にもう一度")
            time.sleep(1.0)
