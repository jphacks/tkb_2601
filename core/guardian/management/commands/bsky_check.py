"""Bluesky（または社内 PDS）の設定を確かめる: python manage.py bsky_check [--write]

door2 と同じコンテナ・同じ出口で動かす（docker compose run --rm door2 python manage.py bsky_check）。
ログインできるか、自分のリポジトリの店の記録を読めるかだけを確かめる。投稿はしない。
--write のときだけ、中身のない確認用の記録を 1 件書く（会社だけの形。宛先がなければ書かない）。
アプリパスワードは画面にもログにも出さない。
"""
import os

from django.core.management.base import BaseCommand

from bluesky import BlueskyClient, BlueskyError
from door2 import REPORT_COLLECTION, normalize_policy, sealed_record

from guardian.models import ShareSetting


class Command(BaseCommand):
    help = "Bluesky（または社内 PDS）にログインでき、店の記録を読めるかを確かめる（投稿はしない）"

    def add_arguments(self, parser):
        parser.add_argument("--write", action="store_true", help="確認用の暗号化した記録を 1 件書く（宛先が要る）")

    def handle(self, *args, **options):
        handle, password = os.environ.get("BLUESKY_HANDLE", ""), os.environ.get("BLUESKY_APP_PASSWORD", "")
        service = os.environ.get("BLUESKY_SERVICE") or "https://bsky.social"
        appview = os.environ.get("BLUESKY_APPVIEW") or "https://public.api.bsky.app"
        self.stdout.write(f"書き込む先: {service}　返信を読む先: {appview}")
        if not handle or not password:
            self.stderr.write("core\\.env に BLUESKY_HANDLE と BLUESKY_APP_PASSWORD がない（core\\host\\bluesky_setup.ps1 で書ける）")
            raise SystemExit(2)
        client = BlueskyClient(handle, password, service=service, appview=appview)
        try:
            client._login()
            self.stdout.write(f"OK ログイン: @{client.handle}（{client.did}）")
            listed = client._transport("GET", f"{service.rstrip('/')}/xrpc/com.atproto.repo.listRecords?repo={client.did}&collection={REPORT_COLLECTION}&limit=5")
            self.stdout.write(f"OK 店の記録を読めた: いま {len(listed.get('records') or [])} 件（最大 5 件まで数えた）")
            if options["write"]:
                policy = normalize_policy(ShareSetting.get().policy())
                if not policy["recipients"]:
                    self.stderr.write("宛先（会社の人の鍵）がないので、確認用の記録は書かない。画面の「外への共有」で宛先を入れる")
                    raise SystemExit(3)
                snapshot = {"slowdowns": [], "robots": [], "dispatch": [], "records": [], "judgement": None}
                record = sealed_record(snapshot, "確認用の記録（ロボもりの設定の確認）", os.environ.get("STORE_NAME", "ロボもり"), policy["recipients"])
                written = client.create_record(REPORT_COLLECTION, record)
                self.stdout.write(f"OK 確認用の暗号化した記録を書いた: {written['uri']}（宛先 {len(record['sealed']['recipients'])} 人）")
        except (BlueskyError, OSError, ValueError, KeyError) as error:
            self.stderr.write(f"NG {error}")
            raise SystemExit(1)
