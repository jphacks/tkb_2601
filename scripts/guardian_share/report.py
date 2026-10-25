"""異常 1 件を言葉にする: Bluesky への投稿文と、Markdown / HTML のレポート。

レポートは「実測した事実」「Guardian の判断」「Bluesky の返信（外部の仮説）」を分けて書く。
返信は第三者が書いた文章なので、HTML では必ずエスケープし、リンクは bsky.app の URL だけにする。
"""
from __future__ import annotations

import html
from datetime import datetime

from bluesky import MAX_BYTES, MAX_GRAPHEMES

LEVEL_WORDS = {"Normal": "正常", "Warning": "注意", "Limited": "制限", "Hold": "保持（停止）"}
# 状態ごとに任せる仕事（README の表と同じ）
TASKS = {
    "Normal": "スープ・料理・空いた皿の回収",
    "Warning": "料理と空いた皿の回収（スープは別の機体へ）",
    "Limited": "空いた皿の回収だけ（スープと料理は別の機体へ）",
    "Hold": "なし（停止して、人の確認を待つ）",
}
NEXT_STEPS = [
    "センサーの固定と取り付け位置を確かめる",
    "同じ条件で振動を測り直し、基準を超えるか確かめる",
    "超えるなら、車輪・モーター・トレーの固定を点検する",
]


def show_time(value: str | None) -> str:
    """ISO の時刻を「2026-10-24 10:15:02」にする。タイムゾーン付き（返信の UTC）はこの PC の時刻に直す。"""
    if not value:
        return ""
    try:
        moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return value
    if moment.tzinfo is not None:
        moment = moment.astimezone()
    return moment.strftime("%Y-%m-%d %H:%M:%S")


def md_escape(value: str) -> str:
    """外部の文字列（表示名など）を Markdown の記号として解釈させない。"""
    return "".join("\\" + ch if ch in "\\`*_[]()!<>#|~" else ch for ch in value.replace("\n", " "))


def peak_rms(incident: dict) -> float | None:
    values = [sample["rms_mg"] for sample in incident.get("samples", [])]
    return max(values) if values else None


def path_text(incident: dict) -> str:
    """状態の移り変わり（例: 注意 → 制限）。"""
    words = []
    for step in incident.get("history", []):
        word = LEVEL_WORDS.get(step["level"], step["level"])
        if not words or words[-1] != word:
            words.append(word)
    return " → ".join(words)


def source_text(incident: dict) -> str:
    return "M5StickS3 で実測した値です（試作中のシステム）。" if incident.get("measured") else "模擬データです（デモ用・実測ではありません）。"


def compose_post(incident: dict, tags: list[str]) -> str:
    """Bluesky に出す本文。ロボットの記号と測定値だけを載せ、場所や機器の識別子は載せない。"""
    level = incident["level"]
    peak = peak_rms(incident)
    path = path_text(incident)

    def build(reason: str) -> str:
        lines = [f"【ロボもり 異常の共有 #{incident['number']:03d}】",
                 f"配膳ロボット {incident['robot']}：{LEVEL_WORDS.get(level, level)}" + (f"（{path}）" if "→" in path else ""),
                 f"理由：{reason}"]
        if peak is not None and level != "Hold":
            lines.append(f"直近の最大振動：{peak:.0f} mg（基準 {incident['threshold_mg']:g} mg）")
        lines.append(f"いまの仕事：{TASKS.get(level, '')}")
        if incident.get("recovered_at"):
            lines.append(f"その後、人が正常に戻しました（{show_time(incident['recovered_at'])[11:16]}）。")
        lines.append("原因や点検のポイントに心当たりがあれば、返信で教えてください。")
        lines.append(source_text(incident))
        if tags:
            lines.append(" ".join("#" + tag for tag in tags))
        return "\n".join(lines)

    reason = incident.get("reason", "")
    text = build(reason)
    while (len(text) > MAX_GRAPHEMES or len(text.encode("utf-8")) > MAX_BYTES) and len(reason) > 10:
        reason = reason[:len(reason) - 10] + "…"
        text = build(reason)
    return text


def _sample_rows(incident: dict) -> list[tuple[str, str, str]]:
    return [(show_time(s.get("at")), f"{s['rms_mg']:.1f}", "" if s.get("peak_mg") is None else f"{s['peak_mg']:.1f}")
            for s in incident.get("samples", [])]


def _facts(incident: dict) -> list[tuple[str, str]]:
    peak = peak_rms(incident)
    threshold = incident["threshold_mg"]
    facts = [
        ("ロボット", incident["robot"]),
        ("検知した時刻", show_time(incident["detected_at"])),
        ("データの出どころ", "M5StickS3（実測）" if incident.get("measured") else "模擬データ（デモ用）"),
        ("判定の基準", f"{threshold:g} mg 以上が {incident['warning_count']} 回続くと注意、{incident['limited_count']} 回続くと制限"),
    ]
    if peak is not None:
        facts.append(("直近の最大振動（RMS）", f"{peak:.1f} mg（基準の {peak / threshold:.1f} 倍）"))
    return facts


def _decision(incident: dict) -> list[tuple[str, str]]:
    level = incident["level"]
    rows = [("判定", LEVEL_WORDS.get(level, level)), ("理由", incident.get("reason", "")),
            ("任せる仕事", TASKS.get(level, ""))]
    if "→" in path_text(incident):
        rows.append(("経過", path_text(incident)))
    rows.append(("その後", f"{show_time(incident['recovered_at'])} に人が正常に戻した" if incident.get("recovered_at")
                 else "まだ戻していない（または、この記録の時点では不明）"))
    return rows


def _share_lines(incident: dict) -> tuple[str, str | None]:
    shared = incident.get("shared")
    if not shared:
        return "まだ共有していない。", None
    if shared.get("dry_run"):
        return f"{show_time(shared.get('at'))} に共有を承認（試し運転のため投稿はしていない）。", None
    return f"{show_time(shared.get('at'))} に @{shared.get('handle', '')} で投稿。", shared.get("url")


def render_markdown(incident: dict) -> str:
    def cell(value: str) -> str:
        return value.replace("|", "\\|").replace("\n", " ")

    out = [f"# ロボット {incident['robot']} 異常レポート（{incident['id']}）", ""]
    out += ["## 実測データ", "", "| 項目 | 内容 |", "|---|---|"]
    out += [f"| {k} | {cell(v)} |" for k, v in _facts(incident)]
    rows = _sample_rows(incident)
    if rows:
        out += ["", "| 時刻 | 振動 RMS (mg) | ピーク (mg) |", "|---|---|---|"]
        out += [f"| {t} | {rms} | {peak} |" for t, rms, peak in rows]
    out += ["", "## Guardian の判断", "", "| 項目 | 内容 |", "|---|---|"]
    out += [f"| {k} | {cell(v)} |" for k, v in _decision(incident)]
    line, url = _share_lines(incident)
    out += ["", "## Bluesky での共有", "", line + (f" {url}" if url else "")]
    shared = incident.get("shared") or {}
    if shared.get("text"):
        out += ["", "```", shared["text"], "```"]
    out += ["", "## 返信（外部からの意見。検証していない仮説）", ""]
    replies = incident.get("replies", [])
    if not replies:
        out.append("返信はまだない。")
    for reply in replies:
        who = f"@{reply['handle']}" + (f"（{reply['name']}）" if reply.get("name") else "")
        out += [f"### {md_escape(who)} {show_time(reply.get('created_at'))}", ""]
        if reply.get("url"):
            out += [reply["url"], ""]
        # 本文は字下げしたコードブロックにする: 外部の文章を Markdown として解釈させない（画像やリンクを埋め込ませない）
        out += ["    " + text for text in (reply.get("text") or "").splitlines() or [""]]
        out.append("")
    out += ["", "## 次に確かめること（Guardian の定型の手順）", ""]
    out += [f"{i}. {step}" for i, step in enumerate(NEXT_STEPS, 1)]
    out += ["", "原因はまだ確定していない。返信は実測で確かめるまで仮説として扱い、Guardian の安全の設定は自動では変えない。", ""]
    return "\n".join(out)


def render_html(incident: dict) -> str:
    e = html.escape
    level = incident["level"]

    def table(rows: list[tuple[str, str]]) -> str:
        return "<table>" + "".join(f"<tr><th>{e(k)}</th><td>{e(v)}</td></tr>" for k, v in rows) + "</table>"

    parts = [
        "<!doctype html>",
        '<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
        f"<title>{e(incident['id'])} ロボット {e(incident['robot'])} 異常レポート</title>",
        "<style>body{font-family:system-ui,sans-serif;max-width:860px;margin:2rem auto;padding:0 16px;line-height:1.6;color:#1d232b}"
        "table{border-collapse:collapse;width:100%;margin:.5rem 0 1rem}th,td{border:1px solid #d4d9e0;padding:.35rem .6rem;text-align:left;vertical-align:top}"
        "th{background:#f3f5f8;width:30%}.badge{display:inline-block;padding:.1rem .6rem;border-radius:4px;background:#315d7d;color:#fff}"
        ".level-Warning{background:#b7791f}.level-Limited{background:#c05621}.level-Hold{background:#c53030}"
        "blockquote{margin:.3rem 0 1rem;padding:.3rem .8rem;border-left:4px solid #9fb3c8;background:#f7f9fb;white-space:pre-wrap}"
        ".note{color:#5b6573;font-size:.92rem}</style></head><body>",
        f"<h1>ロボット {e(incident['robot'])} 異常レポート <span class=\"badge level-{e(level)}\">{e(LEVEL_WORDS.get(level, level))}</span></h1>",
        f"<p class=\"note\">{e(incident['id'])} / {e(source_text(incident))}</p>",
        "<h2>実測データ</h2>", table(_facts(incident)),
    ]
    rows = _sample_rows(incident)
    if rows:
        parts.append("<table><tr><th>時刻</th><th>振動 RMS (mg)</th><th>ピーク (mg)</th></tr>"
                     + "".join(f"<tr><td>{e(t)}</td><td>{e(r)}</td><td>{e(p)}</td></tr>" for t, r, p in rows) + "</table>")
    parts += ["<h2>Guardian の判断</h2>", table(_decision(incident)), "<h2>Bluesky での共有</h2>"]
    line, url = _share_lines(incident)
    parts.append(f"<p>{e(line)}" + (f' <a href="{e(url)}" rel="noopener noreferrer">投稿を開く</a>' if url else "") + "</p>")
    shared = incident.get("shared") or {}
    if shared.get("text"):
        parts.append(f"<blockquote>{e(shared['text'])}</blockquote>")
    parts.append("<h2>返信（外部からの意見。検証していない仮説）</h2>")
    replies = incident.get("replies", [])
    if not replies:
        parts.append("<p>返信はまだない。</p>")
    for reply in replies:
        who = "@" + reply["handle"] + (f"（{reply['name']}）" if reply.get("name") else "")
        link = f' <a href="{e(reply["url"])}" rel="noopener noreferrer">開く</a>' if reply.get("url") else ""
        parts.append(f"<p><strong>{e(who)}</strong> {e(show_time(reply.get('created_at')))}{link}</p>"
                     f"<blockquote>{e(reply.get('text') or '')}</blockquote>")
    parts.append("<h2>次に確かめること（Guardian の定型の手順）</h2><ol>"
                 + "".join(f"<li>{e(step)}</li>" for step in NEXT_STEPS) + "</ol>")
    parts.append("<p class=\"note\">原因はまだ確定していない。返信は実測で確かめるまで仮説として扱い、"
                 "Guardian の安全の設定は自動では変えない。</p></body></html>")
    return "\n".join(parts)
