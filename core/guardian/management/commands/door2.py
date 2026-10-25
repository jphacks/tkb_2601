"""扉 2 を 1 回だけ開けて、終わったら閉じる: python manage.py door2 [--by "人が PSoC 6 の SW2 を押した"] [--dry-run]

PC 側の PSoC 6 中継が、SW2 が押されたときに `docker compose run --rm door2` で動かす。インターネットに出られるのは
このコンテナだけ（それも許可した宛先だけ）で、終わるとコンテナごと消える。時間割で扉 2 を開けてよい区間（共有可・
書き出し）でなければ、開けない。外から受け取った意見は提案として記録し、ロボットの動きは変えない。
何を外へ出すか・誰の返信を受け取るかは、店が画面で選んだ設定（ShareSetting）に従う。
"""
import os
import sys
import time
from datetime import timedelta

from django.core.management.base import BaseCommand
from django.db import DatabaseError
from django.utils import timezone

from bluesky import BlueskyClient
from door2 import GEMINI_MODEL, GeminiClient, Outside

from guardian import schedule
from guardian.core_loop import save_event
from guardian.models import Gate, Opinion, ShareSetting, Slot
from guardian.outbound import build_snapshot, carried_opinions, export, export_dir, tags

STALE_OPEN = timedelta(minutes=10)   # これより前から「開」のままなら、前の door2 が途中で止まったとみなす
REFUSED = 3                          # 時間割で開けられないときの終了コード（PC 側がランプで知らせる）


def make_clients(dry_run: bool):
    gemini = bluesky = None
    if not dry_run and os.environ.get("GEMINI_API_KEY"):
        gemini = GeminiClient(os.environ["GEMINI_API_KEY"], os.environ.get("GEMINI_MODEL", GEMINI_MODEL))
    if not dry_run and os.environ.get("BLUESKY_HANDLE") and os.environ.get("BLUESKY_APP_PASSWORD"):
        # 書き込む先の PDS。既定は bsky.social。社内の PDS を立てたら、.env の BLUESKY_SERVICE にその住所を書く
        bluesky = BlueskyClient(os.environ["BLUESKY_HANDLE"], os.environ["BLUESKY_APP_PASSWORD"],
                                service=os.environ.get("BLUESKY_SERVICE") or "https://bsky.social",
                                appview=os.environ.get("BLUESKY_APPVIEW") or "https://public.api.bsky.app")
    return gemini, bluesky


class Command(BaseCommand):
    help = "扉 2 を 1 回だけ開ける（Gemini の見立て・Bluesky の共有と返信・AT プロトコルの記録・Google Drive への書き出し）"

    def add_arguments(self, parser):
        parser.add_argument("--by", default=os.environ.get("OPENED_BY", "人が開けた"), help="誰が・何で開けたか（記録に残す）")
        parser.add_argument("--dry-run", action="store_true", help="試し運転。Gemini と Bluesky には送らない")

    def handle(self, *args, **options):
        dry_run = options["dry_run"] or os.environ.get("DOOR2_DRY_RUN") == "1"
        started = time.monotonic()
        slot, mode = schedule.current(timezone.localtime(), list(Slot.objects.all()))
        if mode not in schedule.DOOR2_ALLOWED:
            label = schedule.MODES[mode][0]
            span = f"{slot.start:%H:%M}-{slot.end:%H:%M}" if slot else ""
            save_event("door", "refused", f"扉 2 は開けなかった（時間割: {label} {span}。{options['by']}）")
            self.stderr.write(f"時間割が「{label}」のあいだは、扉 2 を開けない")
            sys.exit(REFUSED)
        gate = Gate.get()
        if gate.state == "external" and timezone.now() - gate.changed_at < STALE_OPEN:
            self.stdout.write("扉 2 はもう開いている。何もしない")
            return
        gate.state, gate.changed_at, gate.opened_by = "external", timezone.now(), options["by"][:64]
        gate.save()
        save_event("door", "door2", f"扉 2 を開けた（{gate.opened_by}）")
        snapshot = build_snapshot(gate)
        gemini, bluesky = make_clients(dry_run)
        outside = Outside(gemini, bluesky, tags(), os.environ.get("STORE_NAME", "ロボもり デモ店"), ShareSetting.get().policy())
        outside.posts = [{"uri": o.uri, "url": o.url, "handle": o.detail.get("handle"), "created_at": o.at.isoformat()}
                         for o in Opinion.objects.filter(source="post")[:10]][::-1]
        outside.seen_replies = set(Opinion.objects.filter(source="reply").values_list("uri", flat=True))
        last_record = Opinion.objects.filter(source="record").first()
        outside.previous = {"uri": last_record.uri, "cid": last_record.detail.get("cid", "")} if last_record else None
        carried = carried_opinions()
        outside.carry = [{"author": c["author"], "post": c["post"]} for c in carried]
        try:
            result = outside.exchange(snapshot)
        except Exception as error:  # 想定外の失敗でも、扉は必ず閉じる
            result = {"replies": [], "judgement": None, "post": None, "record": None, "text": "", "notable": False,
                      "gemini": "off", "bluesky": "off", "errors": [f"想定外の失敗（{error}）"]}

        judgement, post, record = result.get("judgement"), result.get("post"), result.get("record")
        if judgement:
            Opinion.objects.create(source="gemini", text=judgement["assessment"], detail=judgement)
            save_event("outside", "gemini", f"Gemini の見立て（提案）: {judgement['assessment']} / 勧める対応: {judgement['recommendation']}")
        in_record = record["uri"] if record else ""
        for reply in result["replies"]:
            Opinion.objects.create(source="reply", text=reply["text"], uri=reply["uri"], url=reply.get("url") or "",
                                   detail={"handle": reply["handle"], "did": reply.get("did", ""), "cid": reply.get("cid", ""),
                                           "post": reply.get("post"), "created_at": reply.get("created_at"), "in_record": in_record})
            save_event("outside", "reply", f"Bluesky の返信 @{reply['handle']}: {reply['text']}")
        if in_record:
            for item in Opinion.objects.filter(pk__in=[c["id"] for c in carried]):
                item.detail = {**item.detail, "in_record": in_record}
                item.save(update_fields=["detail"])
        skipped = []
        if result.get("not_allowed"):
            skipped.append(f"受け取る相手ではない返信 {result['not_allowed']} 件")
        if result.get("late"):
            skipped.append(f"受付期限を過ぎた返信 {result['late']} 件")
        if skipped:
            save_event("outside", "reply_skipped", "取り込まなかった（店の設定）: " + "、".join(skipped) + "。文は残していない")
        if post:
            Opinion.objects.create(source="post", text=result["text"], uri=post["uri"], url=post.get("url") or "", detail={"handle": post.get("handle")})
            save_event("outside", "post", f"Bluesky に共有した: {post.get('url') or post['uri']}")
        elif result.get("text"):
            save_event("outside", "draft", "共有文を作った（" + ("試し運転のため送っていない" if result["bluesky"] == "off" else "送れなかった") + "）")
        if record:
            Opinion.objects.create(source="record", text="店の記録を、店のアカウントのリポジトリに残した", uri=record["uri"],
                                   detail={**record, "share": result.get("share", "post")})
            save_event("outside", "record", f"AT プロトコルの記録を残した（店のアカウントのリポジトリ、意見 {record.get('opinions', 0)} 件）: {record['uri']}")
        elif result.get("text") and result.get("share") == "off":
            save_event("outside", "share_off", "店の設定（共有しない）で、Bluesky には投稿も記録も出していない")
        if not result["notable"]:
            save_event("outside", "skip", "減速も故障もないので、Gemini にも Bluesky にも送っていない")

        written = ""
        if export_dir().is_dir():
            try:
                written = export(export_dir(), snapshot, result).name
                save_event("outside", "drive", f"Google Drive 行きのフォルダに書いた: {written}")
            except OSError as error:
                result["errors"].append(f"Google Drive 行きのフォルダに書けなかった（{error}）")
        for error in result["errors"]:
            save_event("outside", "error", error)

        parts = [("Gemini の見立てあり" if judgement else "Gemini には聞いていない"),
                 ("Bluesky に共有した" if post else "Bluesky には送っていない"), f"返信 {len(result['replies'])} 件"]
        if result.get("not_allowed") or result.get("late"):
            parts.append(f"取り込まなかった返信 {result.get('not_allowed', 0) + result.get('late', 0)} 件")
        if record:
            parts.append("AT プロトコルの記録あり")
        if written:
            parts.append("Drive 行きに書いた")
        # 開いていたことが Unity と画面に出るよう、少しだけ開けておいてから閉じる
        time.sleep(max(0.0, float(os.environ.get("DOOR2_MIN_OPEN_SECONDS", "3")) - (time.monotonic() - started)))
        try:
            gate = Gate.get()
            gate.state, gate.changed_at, gate.last_closed_at = "store", timezone.now(), timezone.now()
            gate.last_result = {"summary": "前回: " + " / ".join(parts), "errors": result["errors"], "file": written}
            gate.save()
        except DatabaseError as error:
            self.stderr.write(f"扉 2 の状態を書けなかった: {error}")
        save_event("door", "door2", "扉 2 を閉じた（外とのやりとりが終わったため、自動）")
        self.stdout.write(" / ".join(parts))
        if result["errors"]:
            for error in result["errors"]:
                self.stderr.write(error)
            sys.exit(1)
