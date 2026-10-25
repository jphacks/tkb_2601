"""時間割を見る・決める（人が前もって決める）。

  python manage.py timetable                       いまの時間割と、次に切り替わる時刻
  python manage.py timetable --set "00:00-09:00 standalone, 09:00-21:00 store, 21:00-21:10 export, 21:10-24:00 share"
  python manage.py timetable --demo                発表用: 今から 共有可 → 書き出し → 営業中 → 単独 → 共有可 と切り替わる
  python manage.py timetable --clear               時間割を消す（一日中「店内・共有可」）

mode: standalone（単独）/ store（店内・営業中）/ share（店内・共有可）/ export（書き出し）
"""
from datetime import datetime, timedelta

from django.core.management.base import BaseCommand, CommandError
from django.db import transaction
from django.utils import timezone

from guardian import schedule
from guardian.core_loop import save_event
from guardian.models import Slot


class Command(BaseCommand):
    help = "時間割を見る・決める（人が前もって決める）"

    def add_arguments(self, parser):
        group = parser.add_mutually_exclusive_group()
        group.add_argument("--set", metavar="区間", help='例: "00:00-09:00 standalone, 09:00-21:00 store, 21:00-21:10 export, 21:10-24:00 share"')
        group.add_argument("--demo", action="store_true", help="発表用の時間割を今から作る")
        group.add_argument("--clear", action="store_true", help="時間割を消す")
        parser.add_argument("--minutes", type=int, default=2, help="--demo の 1 区間の長さ（分、既定: 2）")

    def handle(self, *args, **options):
        if options["set"]:
            try:
                spans = schedule.parse(options["set"])
            except ValueError as error:
                raise CommandError(str(error)) from None
            self._replace(spans, "人が時間割を決めた")
        elif options["demo"]:
            now = timezone.localtime().replace(second=0, microsecond=0)
            m = max(1, options["minutes"])
            marks = [now + timedelta(minutes=k) for k in (0, m, m + 1, 2 * m + 1, 2 * m + 2)]
            spans = [(marks[0].time(), marks[1].time(), "share"), (marks[1].time(), marks[2].time(), "export"),
                     (marks[2].time(), marks[3].time(), "store"), (marks[3].time(), marks[4].time(), "standalone"),
                     (marks[4].time(), marks[0].time(), "share")]
            self._replace(spans, "人が発表用の時間割を決めた")
        elif options["clear"]:
            Slot.objects.all().delete()
            save_event("schedule", "set", "人が時間割を消した（一日中「店内・共有可」）")
        self._show()

    @transaction.atomic
    def _replace(self, spans, note):
        Slot.objects.all().delete()
        for start, end, mode in spans:
            Slot.objects.create(start=start, end=end, mode=mode)
        save_event("schedule", "set", note + ": " + ", ".join(f"{a:%H:%M}-{b:%H:%M} {schedule.MODES[m][0]}" for a, b, m in spans))

    def _show(self):
        slots = list(Slot.objects.all())
        now = timezone.localtime()
        current, mode = schedule.current(now, slots)
        if not slots:
            self.stdout.write("時間割なし（一日中「店内・共有可」）")
        for slot in slots:
            mark = "▶" if slot == current else " "
            self.stdout.write(f"{mark} {slot.start:%H:%M}-{slot.end:%H:%M}  {schedule.MODES[slot.mode][0]}  {schedule.MODES[slot.mode][1]}")
        change = schedule.next_change(now, slots)
        self.stdout.write(f"いま: {schedule.MODES[mode][0]}" + (f"（{change[0]:%H:%M} に {schedule.MODES[change[1]][0]} へ）" if change else ""))
