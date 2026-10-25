from django.apps import AppConfig
from django.db.backends.signals import connection_created


def _sqlite_wal(sender, connection, **kwargs):
    # web・hub・door2 が同じファイルを同時に使う。WAL なら読む側が書く側を待たない
    if connection.vendor == "sqlite":
        with connection.cursor() as cursor:
            cursor.execute("PRAGMA journal_mode=WAL;")
            cursor.execute("PRAGMA synchronous=NORMAL;")


class GuardianConfig(AppConfig):
    name = "guardian"
    verbose_name = "ロボもり 中核"

    def ready(self):
        connection_created.connect(_sqlite_wal)
