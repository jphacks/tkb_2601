"""画面に入るワンタイムパスワードを出す: python manage.py issue_otp

PC 側の start.ps1 / core\\host\\otp.ps1（Mac / Linux は start.sh）が `docker compose exec web` で呼び、
出てきた合言葉を PC の画面にだけ表示する。DB にはハッシュだけを置き、平文はどこにも残さない。
新しく出すと、まだ使っていない前の合言葉は使えなくなる。--logout-all で、入っている画面もすべて閉じさせる。
"""
from django.contrib.sessions.models import Session
from django.core.management.base import BaseCommand
from django.utils import timezone

from guardian import auth
from guardian.models import Event


class Command(BaseCommand):
    help = "画面に入るワンタイムパスワードを出す（PC の画面にだけ表示する）"

    def add_arguments(self, parser):
        parser.add_argument("--minutes", type=int, default=None, help="使える時間（分、1〜60。既定は 10）")
        parser.add_argument("--logout-all", action="store_true", help="いま入っている画面も、すべて入り直しにする")
        parser.add_argument("--plain", action="store_true", help="合言葉だけを 1 行で出す（スクリプトから読むとき）")

    def handle(self, *args, **options):
        minutes = options["minutes"]
        if minutes is not None:
            minutes = max(1, min(60, minutes))
        if options["logout_all"]:
            Session.objects.all().delete()
        code, item = auth.issue(minutes)
        Event.objects.create(source="door", kind="otp", text="画面に入るワンタイムパスワードを新しく出した（PC の画面にだけ表示）"
                             + ("。入っていた画面はすべて入り直し" if options["logout_all"] else ""))
        shown = auth.display(code)
        if options["plain"]:
            self.stdout.write(shown)
            return
        until = timezone.localtime(item.expires_at)
        self.stdout.write(f"ワンタイムパスワード: {shown}（{until:%H:%M} まで・1 回だけ）")
