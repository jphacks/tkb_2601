"""中核の記録。SQLite 1 ファイルに置き、web（画面）・hub（監視 AI）・door2（扉 2）が読み書きする。"""
from django.db import models
from django.utils import timezone


class Event(models.Model):
    """監査ログ。足すだけで、直さない。"""

    at = models.DateTimeField(default=timezone.now, db_index=True)
    source = models.CharField(max_length=16)   # ai / dispatch / door / drill / relay / outside / hub / sim
    kind = models.CharField(max_length=24)
    text = models.TextField(blank=True)
    data = models.JSONField(default=dict, blank=True)

    class Meta:
        ordering = ["-at", "-id"]

    def __str__(self):
        return f"{self.at:%H:%M:%S} [{self.source}] {self.text[:60]}"


class Live(models.Model):
    """いまの様子（1 行だけ）。hub が 0.5 秒ごとに書き、画面が読む。"""

    updated_at = models.DateTimeField(default=timezone.now)
    unity_connected = models.BooleanField(default=False)
    unity_boot = models.CharField(max_length=64, blank=True)
    last_telemetry_at = models.DateTimeField(null=True, blank=True)
    door1 = models.CharField(max_length=16, default="unknown")   # store / standalone / unknown
    door1_reason = models.CharField(max_length=200, blank=True)
    ai_state = models.CharField(max_length=16, default="idle")    # idle / learning / ok
    robots = models.JSONField(default=list, blank=True)
    people = models.JSONField(default=list, blank=True)
    slowdowns = models.JSONField(default=list, blank=True)
    proposals = models.JSONField(default=list, blank=True)
    dispatch = models.JSONField(default=list, blank=True)
    rejected = models.IntegerField(default=0)
    seats = models.JSONField(default=list, blank=True)        # 椅子ごとの空き具合（Unity から）
    scene = models.JSONField(default=dict, blank=True)        # Unity のシーンから読んだ店内の配置（boot つき）
    scene_rev = models.IntegerField(default=0)                # 配置を受け取り直すたびに増える（画面が描き直す合図）

    @classmethod
    def get(cls) -> "Live":
        return cls.objects.get_or_create(pk=1)[0]


class Gate(models.Model):
    """扉 2（店内 ⇄ インターネット）。開け閉めするのは door2 コンテナだけ（画面からは開けない）。"""

    state = models.CharField(max_length=16, default="store")   # store / external
    changed_at = models.DateTimeField(default=timezone.now)
    opened_by = models.CharField(max_length=64, blank=True)
    last_closed_at = models.DateTimeField(null=True, blank=True)
    last_result = models.JSONField(default=dict, blank=True)
    last_export_key = models.CharField(max_length=64, blank=True)   # 時間割の書き出しを、区間ごとに 1 回だけにする

    @classmethod
    def get(cls) -> "Gate":
        return cls.objects.get_or_create(pk=1)[0]


class Opinion(models.Model):
    """扉 2 で外から受け取った意見（Gemini の見立て、Bluesky の返信）と、共有した投稿。提案であり、ロボットは変えない。"""

    at = models.DateTimeField(default=timezone.now, db_index=True)
    source = models.CharField(max_length=16)   # gemini / reply / post / record
    text = models.TextField(blank=True)
    uri = models.CharField(max_length=300, blank=True, db_index=True)
    url = models.CharField(max_length=300, blank=True)
    detail = models.JSONField(default=dict, blank=True)

    class Meta:
        ordering = ["-at", "-id"]


class ShareSetting(models.Model):
    """扉 2 で外へ何を出し、誰の返信を受け取るか。店が画面で選ぶ（変えるたびに監査ログに残す）。
    扉 2 を開けるのは、これまでどおり人（PSoC 6 の SW2）だけ。ここで選ぶのは、開けたときに何をするか。"""

    SHARE = [("off", "共有しない"), ("sealed", "会社だけ（暗号化した記録）"), ("record", "記録だけ（公開）"), ("post", "投稿と記録（公開）")]
    ACCEPT = [("following", "店がフォローしている人"), ("listed", "指定した人"), ("anyone", "だれでも")]

    share = models.CharField(max_length=8, choices=SHARE, default="sealed")
    gemini = models.BooleanField(default=False)                     # 要約をクラウド AI（Gemini）に送って見立てを聞くか
    accept = models.CharField(max_length=12, choices=ACCEPT, default="following")
    allow = models.JSONField(default=list, blank=True)              # 指定した人（DID かハンドル）
    window_days = models.IntegerField(default=7)                    # 投稿から何日までの返信を受け取るか
    recipients = models.JSONField(default=list, blank=True)         # 会社だけの記録を開ける人 [{"name", "key"}]（公開の鍵だけ）
    updated_at = models.DateTimeField(default=timezone.now)

    @classmethod
    def get(cls) -> "ShareSetting":
        return cls.objects.get_or_create(pk=1)[0]

    def policy(self) -> dict:
        return {"share": self.share, "gemini": self.gemini, "accept": self.accept, "allow": list(self.allow or []),
                "window_days": self.window_days, "recipients": list(self.recipients or [])}


class RelayTrust(models.Model):
    """センサー中継の記録から見た信頼状態（1 行だけ）。中継には何も送らず、記録を読むだけ。"""

    updated_at = models.DateTimeField(default=timezone.now)
    file = models.CharField(max_length=200, blank=True)
    last_sample_seen_at = models.DateTimeField(null=True, blank=True)   # 新しい測定値の行を読んだ時刻
    last_level = models.CharField(max_length=16, blank=True)
    hold = models.BooleanField(default=False)
    last_reason = models.CharField(max_length=300, blank=True)
    samples = models.IntegerField(default=0)
    key2 = models.IntegerField(default=0)
    bad_lines = models.IntegerField(default=0)
    gaps = models.IntegerField(default=0)
    robot = models.CharField(max_length=8, blank=True)          # 中継が安全レベルを変えるロボット（start の記録）
    threshold_mg = models.IntegerField(default=0)               # 中継のしきい値（start の記録）
    peaks = models.JSONField(default=list, blank=True)          # 分ごとの最大の揺れ [[中核の時刻（分）, mg], …]（12 時間分）
    recent = models.JSONField(default=list, blank=True)         # 直近の測定値 [[中継の時刻 HH:MM:SS, mg], …]（画面のグラフ）

    @classmethod
    def get(cls) -> "RelayTrust":
        return cls.objects.get_or_create(pk=1)[0]


class Request(models.Model):
    """画面から頼んだ操作（扉 1 の訓練）。hub が拾って実行する。"""

    at = models.DateTimeField(default=timezone.now)
    kind = models.CharField(max_length=16)
    done_at = models.DateTimeField(null=True, blank=True)


class Pass(models.Model):
    """画面に入るワンタイムパスワード。平文は持たず、塩つきの scrypt のハッシュだけを持つ。
    1 回使うか、期限が来るか、続けて間違えると使えなくなる。出すのは PC 側（python manage.py issue_otp）だけ。"""

    created_at = models.DateTimeField(default=timezone.now)
    expires_at = models.DateTimeField()
    used_at = models.DateTimeField(null=True, blank=True)   # 入るのに使った、または取り消した時刻
    failures = models.IntegerField(default=0)
    salt = models.CharField(max_length=32)
    digest = models.CharField(max_length=64)

    class Meta:
        ordering = ["-created_at", "-id"]


class Slot(models.Model):
    """時間割の 1 区間。人が前もって決める（管理画面か python manage.py timetable）。"""

    MODES = [("standalone", "単独"), ("store", "店内（営業中）"), ("share", "店内・共有可"), ("export", "書き出し")]
    start = models.TimeField()
    end = models.TimeField()
    mode = models.CharField(max_length=16, choices=MODES)
    note = models.CharField(max_length=100, blank=True)

    class Meta:
        ordering = ["id"]

    def __str__(self):
        return f"{self.start:%H:%M}-{self.end:%H:%M} {self.get_mode_display()}"
