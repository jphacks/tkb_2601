"""中核の hub。Unity（または代役の sim）から 47612 で様子を受け取り、監視 AI の助言を返す。

監視 AI・推薦・扉 1 の訓練は scripts/guardian_hub の部品をそのまま使い、ここでは記録（SQLite）への書き込みと、
センサー中継の記録の読み取りを足す。中継の 47610 には触らない（本線の StickS3 → 中継 → Unity はそのまま）。

Unity を待たせないため、仕事を 2 つに分ける。
  - Unity の側（fast_tick・on_event）: Unity の行を読み、助言を返す。DB には触らない（記録は Recorder に積むだけ）。
  - 記録の側（slow_tick）: 記録を DB に書き、画面用の様子・時間割・画面からの依頼・センサー中継の記録を扱う。
runhub はこの 2 つを別のスレッドで回すので、DB が待たされても（ほかのコンテナが書いている間など）、Unity への助言は遅れない。
"""
from __future__ import annotations

import queue
import threading
from typing import Callable

from django.db import DatabaseError
from django.utils import timezone

from dispatch import NODES
from hub import ADVICE_INTERVAL, Hub
from monitor import AREA_RADIUS, Monitor

from . import schedule
from .models import Event, Gate, Live, RelayTrust, Request, Slot
from .outbound import scheduled_export
from .relay import GAP_SECONDS, NOTABLE, RECENT, RelayTail, describe

SAVE_INTERVAL = 0.5
RELAY_INTERVAL = 1.0
REQUEST_INTERVAL = 0.5
SCHEDULE_INTERVAL = 1.0
EXPORT_VISIBLE = 3.0   # 時間割の書き出しで、扉 2 を「開」と見せておく時間（秒）
RECORD_LIMIT = 5000    # 書く前の記録をためておける数。超えたら数えて捨てる（Unity の側は待たない）
DUPLICATED = ("監視AI ", "推薦: ")   # record() で形のある記録として残すので、文のログには重ねない


def save_event(source: str, kind: str, text: str, data: dict | None = None) -> None:
    """すぐ書く（runhub の開始・終了や、door2・timetable のような別の処理から）。Unity の側からは使わない。"""
    try:
        Event.objects.create(source=source, kind=kind, text=text[:2000], data=data or {})
    except DatabaseError:
        pass  # 記録できなくても、助言は止めない


def say_to_log(message: str) -> None:
    print(f"{timezone.localtime():%H:%M:%S} {message}", flush=True)


PEAK_MINUTES = 12 * 60


def remember_sample(trust: RelayTrust, record: dict) -> None:
    """画面のグラフ用に、直近の測定値を中継の時刻（HH:MM:SS）と mg で覚える。"""
    try:
        value = int(round(float(record.get("rms_mg"))))
    except (TypeError, ValueError):
        return
    if value < 0 or value > 100000:
        return
    wall = str(record.get("wall", ""))[11:19]
    trust.recent = (list(trust.recent or []) + [[wall, value]])[-RECENT:]


def remember_peak(trust: RelayTrust, rms_mg) -> None:
    """測定値の揺れ（mg）を、中核の時刻で分ごとの最大として覚える。扉 2 で「前に閉じてから」の最大を出すため。"""
    try:
        value = int(round(float(rms_mg)))
    except (TypeError, ValueError):
        return
    if value < 0 or value > 100000:
        return
    minute = int(timezone.now().timestamp() // 60)
    peaks = [p for p in (trust.peaks or []) if isinstance(p, list) and len(p) == 2 and p[0] > minute - PEAK_MINUTES]
    if peaks and peaks[-1][0] == minute:
        peaks[-1][1] = max(peaks[-1][1], value)
    else:
        peaks.append([minute, value])
    trust.peaks = peaks

class Recorder:
    """監査ログを、Unity の側から切り離して書く。event() は待たない。flush() が DB にまとめて書く。"""

    def __init__(self, limit: int = RECORD_LIMIT):
        self._queue: queue.Queue = queue.Queue(maxsize=limit)
        self.dropped = 0

    def event(self, source: str, kind: str, text: str, data: dict | None = None) -> None:
        try:
            self._queue.put_nowait(Event(at=timezone.now(), source=source, kind=kind, text=text[:2000], data=data or {}))
        except queue.Full:
            self.dropped += 1

    def flush(self) -> int:
        items = []
        while True:
            try:
                items.append(self._queue.get_nowait())
            except queue.Empty:
                break
        if self.dropped:
            dropped, self.dropped = self.dropped, 0
            items.append(Event(source="hub", kind="dropped", text=f"記録が追いつかず、{dropped} 件を書けなかった"))
        if not items:
            return 0
        try:
            Event.objects.bulk_create(items)
        except DatabaseError:
            for item in items:   # 次の回に書き直す（入りきらない分は数える）
                try:
                    self._queue.put_nowait(item)
                except queue.Full:
                    self.dropped += 1
            return 0
        return len(items)


class CoreHub(Hub):
    """監視ハブ（scripts/guardian_hub/hub.py の Hub）。記録は DB に直接書かず、Recorder に積む。"""

    def __init__(self, monitor: Monitor, recorder: Recorder | None = None, log: Callable[[str], None] = say_to_log):
        self.recorder = recorder or Recorder()
        self._print = log
        super().__init__(monitor, log=self._say)

    def _say(self, message: str) -> None:
        self._print(message)
        if not message.startswith(DUPLICATED):
            self.recorder.event("hub", "log", message)

    def record(self, kind: str, **fields) -> None:
        if kind == "dispatch":
            text = f"ロボット {fields.get('robot')}: {fields.get('situation')} → {fields.get('person')}（約 {fields.get('eta_s')} 秒）"
        else:
            text = fields.get("text") or fields.get("door") or kind
        self.recorder.event(kind if kind in ("ai", "dispatch", "door", "drill", "talk") else "hub", kind, text, fields)
        # ハブの手元には貯めない（長く動かしても増えない）


class CoreLoop:
    """ソケットは外から渡すので、そのままテストできる。tick() は記録の側と Unity の側を 1 回ずつ回す。"""

    def __init__(self, hub: CoreHub, send: Callable[[bytes], None], connected: Callable[[], bool], relay: RelayTail | None = None,
                 set_accepting: Callable[[bool], None] = lambda accepting: None):
        self.hub, self.send, self.connected, self.relay, self.set_accepting = hub, send, connected, relay, set_accepting
        self.recorder = hub.recorder
        self.lock = threading.Lock()   # hub の中身を両方の側で触るときだけ持つ（DB を読み書きしている間は持たない）
        self.scheduled: list[tuple[float, bytes]] = []
        self.drill_at = -1e9
        self.next_advice = self.next_save = self.next_relay = self.next_requests = self.next_schedule = 0.0
        self.gate_state = "store"           # 扉 2。記録の側が DB から読み、Unity の側が助言に入れる
        self.mode: str | None = None       # 時間割のいまの mode
        self.accepting = True              # ハブが Unity の接続を受けているか（時間割の「単独」で受けない）
        self.close_gate_at: float | None = None
        self.door1, self.door1_reason = "unknown", "Unity（または代役）を待っている"
        self.last_telemetry_at = None
        self.relay_live = False   # 最初の読み込み（起動する前の記録）では、ログに 1 件ずつは書かない
        self.saved_scene_rev = 0  # DB に書いた店内の配置の版（hub.scene_rev と違えば書く）
        self.robot_seen: dict[str, tuple[bool, str]] | None = None   # ロボットごとの（止まっているか, 段階）。変わったら記録する
        Live.get()
        Gate.get()
        RelayTrust.get()

    def tick(self, now: float) -> None:
        self.slow_tick(now)
        self.fast_tick(now)

    # ---- Unity の側（DB に触らない） ----

    def on_event(self, kind: str, payload, now: float) -> None:
        if kind == "line":
            with self.lock:
                used = self.hub.on_line(payload, now)
            if used:
                self.last_telemetry_at = timezone.now()
        elif kind == "unity_connected":
            self.door1, self.door1_reason = "store", ""
            self.recorder.event("door", "door1", f"扉 1: Unity（または代役）がつながった（{payload}）")
        elif kind == "unity_lost":
            with self.lock:
                self.scheduled.clear()
            if not self.accepting:
                self.recorder.event("door", "door1", "扉 1: 時間割で単独にしたので、Unity との接続を切った")
            elif now - self.drill_at < 5:
                self.door1 = "standalone"
                self.door1_reason = "使えない助言が 3 回届いたので、Unity が自動で閉じた（訓練）。人が「店内に戻す」を押すまでつながらない"
                self.recorder.event("door", "door1", "扉 1 が自動で閉じた（訓練どおり）")
            else:
                self.door1, self.door1_reason = "unknown", "Unity（または代役）との接続が切れた"
                self.recorder.event("door", "door1", "扉 1: Unity（または代役）との接続が切れた")

    def fast_tick(self, now: float) -> None:
        due, advice = [], None
        with self.lock:
            while self.scheduled and self.scheduled[0][0] <= now:
                due.append(self.scheduled.pop(0)[1])
            if now >= self.next_advice:
                self.next_advice = now + ADVICE_INTERVAL
                self.hub.gate = self.gate_state
                if self.connected() and self.hub.telemetry is not None:
                    advice = self.hub.advice(now)
        for line in due:
            self.send(line)
        if advice is not None:
            self.send(advice)

    # ---- 記録の側（DB で待っても、Unity の側は待たない） ----

    def slow_tick(self, now: float) -> None:
        if now >= self.next_requests:
            self.next_requests = now + REQUEST_INTERVAL
            try:
                self.gate_state = Gate.objects.only("state").get(pk=1).state
            except (DatabaseError, Gate.DoesNotExist):
                pass
            self._requests(now)
        if now >= self.next_schedule:
            self.next_schedule = now + SCHEDULE_INTERVAL
            self._schedule(now)
        if self.close_gate_at is not None and now >= self.close_gate_at:
            self.close_gate_at = None
            self._close_gate()
        if now >= self.next_save:
            self.next_save = now + SAVE_INTERVAL
            self._save_live(now)
        if self.relay is not None and now >= self.next_relay:
            self.next_relay = now + RELAY_INTERVAL
            self._relay()
        self.recorder.flush()

    def _schedule(self, now: float) -> None:
        """時間割を当てはめる。閉じる側（単独・扉 2 を開けさせない）は時間割が自動で行う。"""
        try:
            local = timezone.localtime()
            slot, mode = schedule.current(local, list(Slot.objects.all()))
            span = f"（{slot.start:%H:%M}-{slot.end:%H:%M}）" if slot else "（時間割の外）"
            if mode != self.mode:
                label, detail = schedule.MODES[mode]
                self.recorder.event("schedule", mode, f"時間割: {label}{span}。{detail}")
                self.mode = mode
            accepting = mode != "standalone"
            if accepting != self.accepting:
                self.accepting = accepting
                self.set_accepting(accepting)
                if accepting:
                    self.door1, self.door1_reason = "unknown", "時間割で店内に戻した。Unity（または代役）がつなぐのを待っている"
                else:
                    self.door1, self.door1_reason = "standalone", f"時間割: 単独{span}。ハブは Unity の接続を受けない"
            if mode == "export" and slot is not None:
                key = schedule.occurrence(local, slot)
                gate = Gate.get()
                if gate.last_export_key != key and gate.state == "store":
                    gate.state, gate.changed_at, gate.last_export_key = "external", timezone.now(), key
                    gate.opened_by = "時間割（人が前もって決めた書き出しの時刻）"
                    gate.save()
                    self.recorder.event("door", "door2", "扉 2 を開けた（時間割の書き出し。Drive 行きにだけ書き、インターネットには出ない）")
                    self.recorder.flush()   # 書き出す要約に、ここまでの記録を入れる
                    path, problem = scheduled_export(gate)
                    self.recorder.event("outside", "drive" if path else "error",
                                        f"Google Drive 行きのフォルダに書いた: {path.name}" if path else f"時間割の書き出しができなかった: {problem}")
                    self.export_note = "前回: 時間割の書き出し" + ("（Drive 行きに書いた）" if path else f"（失敗: {problem}）")
                    self.close_gate_at = now + EXPORT_VISIBLE
        except DatabaseError:
            pass

    def _close_gate(self) -> None:
        try:
            gate = Gate.get()
            if gate.state == "external":
                gate.state, gate.changed_at, gate.last_closed_at = "store", timezone.now(), timezone.now()
                gate.last_result = {"summary": getattr(self, "export_note", "前回: 時間割の書き出し"), "errors": [], "file": ""}
                gate.save()
                self.recorder.event("door", "door2", "扉 2 を閉じた（時間割の書き出しが終わったため、自動）")
        except DatabaseError:
            pass

    def _requests(self, now: float) -> None:
        try:
            pending = list(Request.objects.filter(done_at__isnull=True).order_by("at"))
            for request in pending:
                if request.kind == "drill":
                    with self.lock:
                        ready = self.connected() and self.hub.telemetry is not None and self.accepting
                        if ready:
                            self.scheduled = [(now + 0.4 * k, line) for k, line in enumerate(self.hub.drill_lines())]
                            self.drill_at = now
                    if not ready:
                        self.recorder.event("drill", "skipped", "Unity（または代役）がつながっていないので、扉 1 の訓練はできない")
                request.done_at = timezone.now()
                request.save(update_fields=["done_at"])
        except DatabaseError:
            pass

    def _snapshot(self, now: float) -> dict:
        """画面用の様子。hub の中身を写すだけなので、lock を持って呼ぶ（DB には触らない）。"""
        monitor = self.hub.monitor
        telemetry = self.hub.telemetry or {}
        slowdowns = [{"kind": "area", "name": name, "scale": e["speed_scale"], "reason": e["reason"],
                      "x": NODES[name][0], "z": NODES[name][1], "r": AREA_RADIUS} for name, e in sorted(monitor.area_scale.items())]
        slowdowns += [{"kind": "robot", "name": name, "scale": e["speed_scale"], "reason": e["reason"]}
                      for name, e in sorted(monitor.robot_scale.items())]
        connected = self.connected()
        return dict(
            updated_at=timezone.now(), unity_connected=connected, unity_boot=self.hub.unity_boot,
            last_telemetry_at=self.last_telemetry_at,
            door1="store" if connected else self.door1, door1_reason="" if connected else self.door1_reason,
            ai_state="idle" if monitor.started is None else "learning" if monitor.learning(now) else "ok",
            robots=[{k: r.get(k) for k in ("id", "x", "z", "condition", "stopped", "held", "speed", "speed_scale",
                                            "task", "phase", "table", "carrying", "vibration", "wait", "says")} for r in telemetry.get("robots", [])],
            people=list(telemetry.get("people", [])),
            seats=list(telemetry.get("seats", [])),
            slowdowns=slowdowns,
            proposals=[{k: p.get(k) for k in ("id", "robot", "zone", "reason")} for p in monitor.proposals.values()],
            dispatch=list(self.hub.dispatch), rejected=self.hub.rejected)

    def _note_robot_changes(self, robots: list[dict], connected: bool) -> None:
        """止まった・動き出した・段階が変わったを、出来事として記録する（ルール AI が回数を数える）。
        つながった直後の最初の様子は基準にするだけで、数えない。"""
        if not connected:
            self.robot_seen = None
            return
        now_seen = {str(r.get("id")): (bool(r.get("stopped")), str(r.get("condition") or "Normal")) for r in robots if r.get("id")}
        if self.robot_seen is not None:
            for rid, (stopped, condition) in now_seen.items():
                was = self.robot_seen.get(rid)
                if was is None:
                    continue
                if stopped and not was[0]:
                    self.recorder.event("robot", "stop", f"ロボット {rid} が止まった（段階: {condition}）", {"robot": rid, "condition": condition})
                elif was[0] and not stopped:
                    self.recorder.event("robot", "resume", f"ロボット {rid} が動き出した", {"robot": rid})
                if condition != was[1]:
                    self.recorder.event("robot", "condition", f"ロボット {rid} の段階: {was[1]} → {condition}",
                                        {"robot": rid, "from": was[1], "to": condition})
        self.robot_seen = now_seen

    def _save_live(self, now: float) -> None:
        with self.lock:
            snapshot = self._snapshot(now)
            # 店内の配置は変わったときだけ書く（0.5 秒ごとに数 KB を書き直さない）
            if self.hub.scene is not None and self.hub.scene_rev != self.saved_scene_rev:
                snapshot.update(scene=dict(self.hub.scene), scene_rev=self.hub.scene_rev)
        self._note_robot_changes(snapshot["robots"], snapshot["unity_connected"] and bool(snapshot["robots"]))
        try:
            Live.objects.filter(pk=1).update(**snapshot)
            if "scene" in snapshot:
                self.saved_scene_rev = snapshot["scene_rev"]
        except DatabaseError:
            pass

    def _relay(self) -> None:
        records, switched = self.relay.poll()
        if not records and not switched:
            self.relay_live = True
            return
        try:
            trust = RelayTrust.get()
            if switched:
                trust.file = self.relay.path.name if self.relay.path else ""
                trust.samples = trust.key2 = trust.bad_lines = trust.gaps = 0
                trust.last_level, trust.hold, trust.last_reason, trust.last_sample_seen_at = "", False, "", None
                if self.relay_live:
                    self.recorder.event("relay", "file", f"センサー中継の記録 {trust.file} を読み始めた")
            for record in records:
                kind = record.get("kind")
                if kind == "start":
                    trust.robot = str(record.get("robot", ""))[:8]
                    try:
                        trust.threshold_mg = max(0, int(round(float(record.get("threshold_mg", 0)))))
                    except (TypeError, ValueError):
                        trust.threshold_mg = 0
                if kind == "sample":
                    trust.samples += 1
                    if self.relay_live:
                        remember_peak(trust, record.get("rms_mg"))
                        remember_sample(trust, record)
                    gap = self.relay.gap(record)
                    if gap is not None and gap > GAP_SECONDS:
                        trust.gaps += 1
                        if self.relay_live:
                            self.recorder.event("relay", "gap", f"センサーの値が {gap:.1f} 秒途切れた")
                    if self.relay_live:
                        trust.last_sample_seen_at = timezone.now()
                elif kind == "level":
                    trust.last_level, trust.hold = str(record.get("level", ""))[:16], bool(record.get("hold"))
                    trust.last_reason = str(record.get("reason", ""))[:300]
                elif kind == "hold":
                    trust.hold, trust.last_reason = True, str(record.get("reason", ""))[:300]
                elif kind == "key2" and record.get("accepted"):
                    trust.key2 += 1
                    trust.last_level, trust.hold = "Normal", False
                elif kind in ("bad_line", "unreadable"):
                    trust.bad_lines += 1
                if kind in NOTABLE and self.relay_live:
                    self.recorder.event("relay", kind, describe(record), {k: v for k, v in record.items() if k != "line"})
            if not self.relay_live and records:
                self.recorder.event("relay", "replay", f"中核が起動する前のセンサー中継の記録を {len(records)} 行読んだ（{trust.file}）")
            trust.updated_at = timezone.now()
            trust.save()
        except DatabaseError:
            pass
        self.relay_live = True
