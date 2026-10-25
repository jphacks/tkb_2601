"""デモ用: 宛先の鍵を 1 つ作り、「会社だけ（暗号化）」とその宛先を登録する: python manage.py demo_setup [--name 保守会社]

core\\host\\demo.ps1 が呼ぶ。秘密の鍵（JWK）は、最後の 1 行に JSON で出す（呼んだ側が Logs\\ のファイルにだけ書き、
画面には出さない）。本番では、宛先の人が自分のブラウザ（core/viewer）で鍵を作る。これはデモを 1 回で動かすための近道。
"""
import json

from cryptography.hazmat.primitives.asymmetric import ec
from django.core.management.base import BaseCommand

from seal import b64u, public_of

from guardian.core_loop import save_event
from guardian.models import ShareSetting


class Command(BaseCommand):
    help = "デモ用の宛先の鍵を作り、会社だけ（暗号化）とその宛先を登録する（秘密の鍵は最後の行に出す）"

    def add_arguments(self, parser):
        parser.add_argument("--name", default="保守会社（デモ）", help="宛先の名前")

    def handle(self, *args, **options):
        key = ec.generate_private_key(ec.SECP256R1())
        numbers = key.private_numbers()
        public = public_of(key)
        setting = ShareSetting.get()
        keep = [r for r in setting.recipients or [] if r.get("name") != options["name"]]
        setting.share, setting.gemini = "sealed", False
        setting.recipients = keep + [{"name": options["name"][:32], "key": public}]
        setting.save()
        save_event("share", "setting", f"デモ: 会社だけ（暗号化）にし、宛先「{options['name']}」の鍵を登録した（宛先 {len(setting.recipients)} 人）")
        jwk = {"kty": "EC", "crv": "P-256", "x": public.split(":")[-1].split(".")[0], "y": public.split(".")[-1],
               "d": b64u(numbers.private_value.to_bytes(32, "big"))}
        self.stdout.write(f"宛先「{options['name']}」を登録した（会社だけ・暗号化）")
        self.stdout.write(json.dumps(jwk))
