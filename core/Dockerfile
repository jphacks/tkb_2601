# ロボもり 中核（web・hub・sim・door2 で同じイメージを使う）
# ビルドするときだけネットが要る。できたイメージは、会場でネットがなくても動く。
FROM python:3.12-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    GUARDIAN_LIB=/app/lib \
    ROBOMORI_DATA=/data \
    ROBOMORI_STATIC=/app/static \
    TZ=Asia/Tokyo

RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin app \
    && mkdir -p /data /app \
    && chown app:app /data

WORKDIR /app/core
COPY core/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

# 監視ハブの部品は、リポジトリのものをそのまま使う（中核のためだけに書き直さない）
COPY scripts/guardian_hub /app/lib/guardian_hub
COPY scripts/guardian_share /app/lib/guardian_share
COPY core /app/core
RUN DJANGO_SECRET_KEY=only-for-collectstatic python manage.py collectstatic --noinput --verbosity 0

USER app
EXPOSE 8000 47612
CMD ["gunicorn", "robomori.wsgi", "--bind", "0.0.0.0:8000", "--workers", "2", "--threads", "4", "--access-logfile", "-"]
