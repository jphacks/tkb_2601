// ロボもり 中核の画面。1 秒ごとに /api/state を読み、描き直す。
// 店内の地図は /api/layout で描く。Unity がつないでいる間は、Unity のシーンにある物から読んだ配置（GuardianHubLink の scene）。
// 配置が変わったら（scene.rev）描き直す。向きは Unity のカメラと同じ（北が上、東が右）。
// 外から来た文章（ロボットの答え、Bluesky の返信、Gemini の見立て、書き出したファイル）は、すべて textContent で入れる。
"use strict";
(() => {
  const SVG = "http://www.w3.org/2000/svg";
  const S = 54; // 1 m あたりの画素
  const $ = (id) => document.getElementById(id);
  // Unity の「任せている仕事」と同じ: 行は能力、列はスープ・料理・皿（GuardianCapabilityPolicy）
  const MATRIX = { Normal: [true, true, true], Warning: [false, true, true], Limited: [false, false, true] };
  const WORD = { Normal: ["正常", "good"], Warning: ["注意", "warn"], Limited: ["制限", "strong"] };
  const RING = { Normal: "#3fa06a", Warning: "#f2b33d", Limited: "#e8743b" };
  const TASK = { Soup: "スープ", Food: "料理", EmptyDishes: "下げ膳" };
  const PHASE = { ToPickup: "受け取りに向かっている", ToDropoff: "運んでいる", ReturningCargo: "受け渡し場所へ戻している" };
  const GUEST = { 来店: "#5b7fb0", 席待ち: "#5b7fb0", ご案内: "#3f6aa3", 注文待ち: "#c98a1b", 食事中: "#3f8f5b",
                  食べ終わり: "#6b7280", 席を立つ: "#6b7280", お会計: "#6b7280", お帰り: "#8b929c" };
  const THING = { table: "#9c7a55", chair: "#b29676", booth: "#8c6e50", kitchen: "#a58a6b", pickup: "#8c6e50", return: "#8c6e50",
                  kiosk: "#5f7894", board: "#7d6a55", mark: "#e3b341", door: "#2b7a57", other: "#b19b80" };
  const SEAT = { ready: "#7fbf8f", clearing: "#e0a84a", reserved: "#7aa2d6" };
  const WARN_LEVEL = 0.30, LIMIT_LEVEL = 0.70;   // Unity の振動指標のしきい値（GuardianSimulationSettings の既定）
  const LEVEL = { Normal: "正常", Warning: "注意", Limited: "制限" };
  const HALO = { stroke: "#d3bc9e", "stroke-width": 4, "stroke-linejoin": "round", "paint-order": "stroke" };   // 床の色の縁取りで、重なっても読める
  const HISTORY = 18;
  const SCREEN_KINDS = ["login", "login_failed", "otp"];   // 画面に入った・合言葉を出した（監査ログにだけ出す）
  const gateEvent = (e) => (e.source === "door" && !SCREEN_KINDS.includes(e.kind)) || e.source === "schedule";

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function svg(tag, attrs, text) {
    const node = document.createElementNS(SVG, tag);
    for (const [key, value] of Object.entries(attrs || {})) node.setAttribute(key, String(value));
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function fill(list, items, render, emptyText) {
    list.replaceChildren();
    if (!items.length) {
      list.append(el("li", "empty", emptyText));
      return;
    }
    for (const item of items) list.append(render(item));
  }

  const time = (iso) => (iso ? iso.slice(11, 19) : "—");
  const short = (iso) => (iso ? iso.slice(11, 16) : "—");
  const ago = (seconds) => (seconds == null ? "まだ" : seconds < 1.5 ? "いま" : seconds < 90 ? `${Math.round(seconds)} 秒前` : `${Math.round(seconds / 60)} 分前`);

  // ---- 店内の地図: 動かない部分（Unity から読んだ配置） ----
  const map = $("map");
  const floor = svg("g", {});
  const live = svg("g", {});
  map.append(floor, live);
  let room = null;
  let layoutKey = null;
  let layoutInfo = { source: "copy", count: 0 };
  let chairs = new Map();
  const px = (x, z) => [(x - room.west) * S, (room.north - z) * S];

  function textWidth(text, size) {
    let width = 0;
    for (const ch of text) width += ch.charCodeAt(0) < 128 ? size * 0.58 : size;
    return width;
  }

  function label(x, y, text, size, color, anchor = "middle", weight = 700) {
    floor.append(svg("text", { x, y, "text-anchor": anchor, "font-size": size, "font-weight": weight, fill: color, ...(color === "#fffaf0" ? {} : HALO) }, text));
  }

  function drawFloor(layout) {
    room = layout.room;
    const width = (room.east - room.west) * S;
    const height = (room.north - room.south) * S;
    map.setAttribute("viewBox", `-4 -4 ${width + 8} ${height + 8}`);
    floor.replaceChildren();
    chairs = new Map();
    const defs = svg("defs", {});
    const arrow = svg("marker", { id: "arrow", viewBox: "0 0 10 10", refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: "auto-start-reverse" });
    arrow.append(svg("path", { d: "M0,0 L10,5 L0,10 z", fill: "#5b4630" }));
    defs.append(arrow);
    floor.append(defs);
    floor.append(svg("rect", { x: 0, y: 0, width, height, rx: 10, fill: "#d3bc9e", stroke: "#7a6650", "stroke-width": 6 }));
    const order = { mark: 0, chair: 1, table: 2, booth: 2, kitchen: 2, pickup: 3, return: 3, kiosk: 3, board: 3, other: 3, door: 4 };
    const sorted = [...layout.things].sort((a, b) => (order[a.kind] ?? 3) - (order[b.kind] ?? 3));
    for (const t of sorted) {
      const [cx, cy] = px(t.x, t.z);
      const w = t.w * S, d = t.d * S;
      const color = THING[t.kind] || THING.other;
      if (t.kind === "door") {
        floor.append(svg("rect", { x: cx - w / 2, y: cy - 6, width: w, height: 12, rx: 3, fill: color }));
        label(cx, cy - 12, t.label, 16, "#1f4d37");
        continue;
      }
      const shape = svg("rect", { x: cx - w / 2, y: cy - d / 2, width: w, height: d, rx: t.kind === "chair" ? 4 : 8, fill: color,
                                 opacity: t.kind === "mark" ? 0.85 : 1 });
      shape.append(svg("title", {}, t.label || t.id));
      floor.append(shape);
      if (t.kind === "chair") { chairs.set(t.id, t); continue; }
      if (!t.label) continue;
      const size = t.kind === "mark" ? 12 : t.kind === "table" ? 16 : 14;
      const fits = textWidth(t.label, size) <= w - 8 && d >= size + 6;
      const ink = t.kind === "mark" ? "#3b2f22" : "#fffaf0";
      if (fits) {
        label(cx, cy + size * 0.36, t.label, size, ink);
        continue;
      }
      // 箱に入らない名前は外に書き、線で結ぶ（部屋のまん中の側へ出す。家具の場所は Unity のまま動かさない）
      const toRight = t.x < (room.west + room.east) / 2;
      const tx = toRight ? cx + w / 2 + 34 : cx - w / 2 - 34;
      const ty = cy + (t.z > (room.south + room.north) / 2 ? 34 : -26);
      floor.append(svg("line", { x1: tx + (toRight ? -4 : 4), y1: ty - 5, x2: toRight ? cx + w / 2 : cx - w / 2, y2: cy, stroke: "#5b4630",
                                "stroke-width": 2, "marker-end": "url(#arrow)" }));
      label(tx, ty, t.label, size, "#3b2f22", toRight ? "start" : "end");
    }
    for (const s of layout.spots) {
      const [sx, sy] = px(s.x, s.z);
      if (s.kind === "home") {
        floor.append(svg("circle", { cx: sx, cy: sy, r: 26, fill: "none", stroke: "#6b5b45", "stroke-width": 2.5, "stroke-dasharray": "6 6" }));
        label(sx + 32, sy + 5, s.label, 13, "#5b4630", "start", 600);
      } else if (s.kind === "service") {
        const dot = svg("rect", { x: sx - 5, y: sy - 5, width: 10, height: 10, transform: `rotate(45 ${sx} ${sy})`, fill: "#5b4630", opacity: 0.55 });
        dot.append(svg("title", {}, s.label));
        floor.append(dot);
      } else if (s.kind === "cook") {
        floor.append(svg("circle", { cx: sx, cy: sy, r: 10, fill: "#6b5b45" }));
        label(sx, sy + 28, s.label, 13, "#5b4630");
      } else if (s.kind === "staff") {
        floor.append(svg("circle", { cx: sx, cy: sy, r: 14, fill: "none", stroke: "#1f4d37", "stroke-width": 2, "stroke-dasharray": "4 4" }));
      }
    }
    layoutInfo = { source: layout.source, count: layout.things.length };
  }

  // ---- 店内の地図: 動く部分（0.5 秒ごとの位置） ----
  function drawLive(state) {
    live.replaceChildren();
    if (!room) return;
    for (const seat of state.seats || []) {
      const chair = chairs.get(seat.id);
      if (!chair || !SEAT[seat.state]) continue;
      const [cx, cy] = px(chair.x, chair.z);
      live.append(svg("rect", { x: cx - (chair.w * S) / 2 - 3, y: cy - (chair.d * S) / 2 - 3, width: chair.w * S + 6, height: chair.d * S + 6, rx: 6,
                                fill: "none", stroke: SEAT[seat.state], "stroke-width": 4 }));
    }
    for (const zone of state.slowdowns.filter((s) => s.kind === "area")) {
      const [cx, cy] = px(zone.x, zone.z);
      live.append(svg("circle", { cx, cy, r: zone.r * S, fill: "rgba(43,122,87,0.20)", stroke: "#2b7a57", "stroke-width": 3, "stroke-dasharray": "12 8" }));
      live.append(svg("text", { x: cx, y: cy - zone.r * S - 8, "text-anchor": "middle", "font-size": 18, "font-weight": 800, fill: "#1f4d37", ...HALO },
                      `${zone.name} 速度上限 ×${zone.scale}（監視AI）`));
    }
    const where = Object.fromEntries(state.robots.map((r) => [r.id, r]));
    for (const d of state.dispatch) {
      const points = [];
      if (d.from) points.push(px(d.from.x, d.from.z));
      for (const p of d.points || []) points.push(px(p.x, p.z));
      if (where[d.robot]) points.push(px(where[d.robot].x, where[d.robot].z));
      if (points.length > 1) {
        live.append(svg("polyline", { points: points.map((p) => p.join(",")).join(" "), fill: "none", stroke: "#2b7a57", "stroke-width": 6,
                                      "stroke-dasharray": "14 10", "stroke-linecap": "round" }));
        live.append(svg("text", { x: points[0][0], y: points[0][1] - 14, "text-anchor": "middle", "font-size": 17, "font-weight": 800, fill: "#1f4d37", ...HALO }, d.person));
      }
    }
    for (const person of state.people) {
      const [x, y] = px(person.x, person.z);
      if (person.role === "guest") {
        const dot = svg("circle", { cx: x, cy: y, r: 9, fill: GUEST[person.state] || "#5b7fb0", stroke: "#fff", "stroke-width": 1.5 });
        dot.append(svg("title", {}, `お客さん: ${person.state || "—"}`));
        live.append(dot);
      } else {
        live.append(svg("circle", { cx: x, cy: y, r: 11, fill: "#1f4d37", stroke: "#fff", "stroke-width": 2 }));
        live.append(svg("text", { x, y: y + 28, "text-anchor": "middle", "font-size": 13, "font-weight": 700, fill: "#1f4d37", ...HALO }, person.busy ? "店員（ご案内中）" : "店員"));
      }
    }
    for (const robot of state.robots) {
      const [x, y] = px(robot.x, robot.z);
      const stopped = robot.stopped || robot.held;
      const ring = stopped ? "#e5484d" : RING[robot.condition] || "#64748b";
      const node = svg("g", {});
      node.append(svg("circle", { cx: x, cy: y, r: 24, fill: "#ffffff", stroke: ring, "stroke-width": 5 }));
      node.append(svg("text", { x, y: y + 8, "text-anchor": "middle", "font-size": 22, "font-weight": 800, fill: "#1f2937" }, robot.id));
      node.append(svg("title", {}, `ロボット ${robot.id}: ${robot.says || WORD[robot.condition]?.[0] || ""}`));
      live.append(node);
      const tags = [];
      if (stopped) tags.push(["停止", "#e5484d"]);
      else if (robot.condition !== "Normal") tags.push([WORD[robot.condition]?.[0] || robot.condition, ring]);
      if (robot.speed_scale < 0.999) tags.push([`×${robot.speed_scale.toFixed(1)}`, "#9fd9bc"]);
      if (tags.length) {
        const text = `${robot.id} ${tags.map((t) => t[0]).join("・")}`;
        const w = textWidth(text, 13) + 16;
        live.append(svg("rect", { x: x - w / 2, y: y + 30, width: w, height: 22, rx: 5, fill: "rgba(15,23,42,0.9)" }));
        live.append(svg("text", { x, y: y + 46, "text-anchor": "middle", "font-size": 13, "font-weight": 700, fill: tags[0][1] }, text));
      }
    }
  }

  // ---- ① 測る ----
  const history = {};
  function sensorRow(from, value, note, freshClass) {
    const row = el("div", "sensor");
    row.append(el("div", "from", from), el("div", "value", value));
    if (note) row.append(el("div", freshClass || "note", note));
    return row;
  }

  // M5StickS3 の実測（センサー中継の記録を中核が読んだもの）: 段階、揺れの推移としきい値、最大、経過
  const M5_EVENT = { level: "段階", hold: "保持", key2: "KEY2", gap: "途切れ", unity_connected: "Unity", unity_disconnected: "Unity",
                     unity_lost: "Unity", serial_open: "USB", serial_lost: "USB", serial_missing: "USB", start: "中継", stop: "中継" };
  function m5Row(relay) {
    const relayText = { fresh: "測定値が届いている", stale: "途切れている", none: "記録なし（センサー中継が動いていない）" }[relay.state];
    const row = sensorRow(`M5StickS3 → センサー中継 → ロボット ${relay.robot || "B"}`,
      relay.level ? `段階: ${LEVEL[relay.level] || relay.level}${relay.hold ? "（保持中）" : ""}` : relayText,
      relay.state === "fresh" ? `${ago(relay.age)}に受信・実測` : relay.state === "stale" ? `${relayText}（${ago(relay.age)}）` : relayText,
      relay.state === "fresh" ? "fresh" : "stale");
    const recent = (relay.recent || []).slice(-30);
    if (recent.length) {
      const limit = relay.threshold_mg || 300;
      const top = Math.max(limit * 2, ...recent.map((s) => s[1]));
      const spark = el("div", "spark m5");
      spark.setAttribute("role", "img");
      spark.setAttribute("aria-label", `直近 ${recent.length} 件の揺れ（mg）。点線はしきい値 ${limit} mg`);
      for (const [wall, mg] of recent) {
        const bar = el("i", mg >= limit ? "strong" : "");
        bar.style.height = `${Math.max(6, Math.min(100, (mg / top) * 100))}%`;
        bar.title = `${wall}  ${mg} mg`;
        spark.append(bar);
      }
      const line = el("span", "line");
      line.style.bottom = `${(limit / top) * 100}%`;
      spark.append(line);
      const now = recent[recent.length - 1][1];
      row.append(el("div", "note", `揺れ（mg・1 秒ごと）　いま ${now}　10 分の最大 ${relay.peak_mg || now}　点線はしきい値 ${limit}`), spark);
    }
    const steps = el("ol", "mini-log m5-log");
    for (const e of (relay.events || []).slice(0, 4)) {
      const li = el("li");
      li.append(el("span", "src", M5_EVENT[e.kind] || e.kind), document.createTextNode(`${time(e.at)}　${e.text}`));
      steps.append(li);
    }
    if (steps.childNodes.length) row.append(steps);
    return row;
  }

  function renderSensors(state) {
    const box = $("sensors");
    box.replaceChildren();
    const unity = state.unity;
    const unityRow = sensorRow(unity.stand_in ? "Unity の代役（模擬）→ 中核" : "Unity → 中核",
      unity.connected ? `ロボット ${state.robots.length} 台・お客さん ${state.people.filter((p) => p.role === "guest").length} 人` : "つながっていない",
      unity.connected ? `${ago(unity.age)}に受信` : "Unity を再生すると、自動でつながる", unity.connected && unity.age != null && unity.age <= 3 ? "fresh" : "stale");
    const bars = el("div", "bars");
    for (const robot of state.robots) {
      const series = (history[robot.id] = (history[robot.id] || []).concat([robot.vibration ?? 0]).slice(-HISTORY));
      const spark = el("div", "spark");
      spark.setAttribute("aria-hidden", "true");
      for (const v of series) {
        const bar = el("i", v >= LIMIT_LEVEL ? "strong" : v >= WARN_LEVEL ? "warn" : "");
        bar.style.height = `${Math.max(8, Math.min(100, v * 100))}%`;
        spark.append(bar);
      }
      const line = el("span", "line");
      line.style.bottom = `${WARN_LEVEL * 100}%`;
      spark.append(line);
      const v = robot.vibration ?? 0;
      bars.append(el("span", "who", robot.id), spark, el("span", "val " + (v >= LIMIT_LEVEL ? "strong" : v >= WARN_LEVEL ? "warn" : ""), v.toFixed(2)));
    }
    if (state.robots.length) {
      unityRow.append(el("div", "note", `振動指標（点線は注意 ${WARN_LEVEL.toFixed(2)}・制限は ${LIMIT_LEVEL.toFixed(2)}）`), bars);
    }
    box.append(unityRow);
    box.append(m5Row(state.relay));
    const waiting = state.proposals.length;
    box.append(sensorRow("PSoC 6 → 人の鍵（承認ボタン）", waiting ? `承認待ち ${waiting} 件` : "承認待ちはない",
      state.door2.pending ? "外へ出す候補あり（PSoC 6 のランプがゆっくり点滅）" : "外へ出す候補はない"));
  }

  // ---- 接続ゲート ----
  function renderGate(state) {
    const plan = state.schedule;
    const external = state.door2.state === "external";
    const standalone = state.door1.state === "standalone";
    const mode = external ? "external" : standalone ? "standalone" : "store";
    $("gate-now").textContent = `いまは「${{ external: "外部", standalone: "単独", store: "店内" }[mode]}」`;
    for (const key of ["standalone", "store", "external"]) {
      const seg = $(`mode-${key}`);
      seg.classList.toggle("on", key === mode);
      seg.classList.toggle("danger", key === mode && key === "standalone");
    }
    const rows = $("gate-rows");
    rows.replaceChildren();
    const row = (name, what, tag, tagClass, now) => {
      const li = el("li", now ? "now" : "");
      const text = el("span");
      text.append(el("b", "", name), document.createTextNode(what));
      li.append(text);
      if (tag) li.append(el("span", "state-tag " + (tagClass || ""), tag));
      return li;
    };
    rows.append(row("単独", "ロボットだけで安全側に動く", standalone ? "いま" : "", "", standalone));
    rows.append(row("店内", "ロボット・センサー・監視AI", state.unity.connected && !standalone ? "開" : "閉", state.unity.connected && !standalone ? "open" : "", mode === "store"));
    rows.append(row("外部", "Google Drive・Gemini・Bluesky", external ? "開" : plan.door2_allowed ? "閉" : "閉・鍵", external ? "open" : plan.door2_allowed ? "" : "lock", external));
    const next = plan.next ? `。${plan.next.at} に「${plan.next.label}」へ` : "";
    $("schedule").textContent = `時間割: ${plan.label}${plan.span ? `（${plan.span}）` : ""}${next}。${standalone ? state.door1.reason : plan.door2_allowed ? "扉 2 は PSoC 6 の SW2 で開けられる" : "扉 2 は鍵がかかっている"}`;
    const doors = state.events.filter(gateEvent).slice(0, 3);
    fill($("gate-log"), doors, (e) => {
      const li = el("li");
      li.append(el("time", "", short(e.at)), document.createTextNode(e.text));
      return li;
    }, "扉の記録はまだない");
  }

  // ---- ② 判断する ----
  function jobText(robot) {
    if (robot.stopped || robot.held) return "止まっている（人の確認を待つ）";
    if (!robot.task) return "待機中";
    const where = robot.table ? `テーブル${robot.table} ` : "";
    return `${where}${TASK[robot.task] || robot.task}・${PHASE[robot.phase] || robot.phase || ""}${robot.carrying ? "（積んでいる）" : ""}`;
  }

  function renderJobs(state) {
    const body = $("jobs").querySelector("tbody");
    body.replaceChildren();
    for (const robot of state.robots) {
      const tr = el("tr");
      const held = robot.stopped || robot.held;
      const [word, tone] = held ? ["停止", "bad"] : WORD[robot.condition] || [robot.condition, ""];
      const id = el("td", "id", robot.id);
      id.style.color = RING[robot.condition] || "inherit";
      tr.append(id, el("td", tone, word));
      for (const ok of MATRIX[robot.condition] || [false, false, false]) tr.append(el("td", ok && !held ? "good" : "bad", ok && !held ? "✓" : "✕"));
      body.append(tr);
    }
    if (!state.robots.length) {
      const tr = el("tr");
      const td = el("td", "empty", "Unity（または代役）からの様子がまだない");
      td.colSpan = 5;
      tr.append(td);
      body.append(tr);
    }
    fill($("says"), state.robots, (robot) => {
      const li = el("li");
      li.append(el("span", "who", robot.id));
      if (robot.says) li.append(el("q", "", robot.says));
      else li.append(el("span", "", jobText(robot)));
      li.append(el("span", "job", `いま: ${jobText(robot)}${robot.speed_scale < 0.999 ? `・速度上限 ×${robot.speed_scale.toFixed(2)}` : ""}`));
      return li;
    }, "ロボットの説明はまだない");
  }

  // ---- ③ 改善する ----
  function renderImprove(state) {
    $("ai-state").textContent = { idle: "まだ様子が届いていない", learning: "学習中（ふだんの揺れを覚えている）", ok: "見張り中（全台の動きを見て、その場で直す。ネットは使わない）" }[state.ai] || state.ai;
    fill($("slowdowns"), state.slowdowns, (s) => {
      const li = el("li");
      li.append(el("b", "", `${s.kind === "area" ? s.name : "ロボット " + s.name}　速度上限 ×${s.scale}`), el("span", "why", s.reason));
      return li;
    }, "速度を下げているところはない");
    fill($("proposals"), state.proposals, (p) => {
      const li = el("li");
      li.append(el("b", "", `${p.zone || "ロボット " + p.robot} を元の速度に戻す`), el("span", "why", p.reason), el("span", "how", "Unity の画面で人が承認"));
      return li;
    }, "人の承認を待つものはない");
    fill($("dispatch"), state.dispatch, (d) => {
      const li = el("li");
      li.append(el("b", "", `${d.robot}　${d.situation} → ${d.person}（約 ${Math.round(d.eta_s)} 秒）`),
        el("span", "why", `経路 ${(d.route || []).join(" → ")}${d.backup ? "　次点 " + d.backup : ""}`));
      return li;
    }, "故障しているロボットはない");
  }

  // ---- 上の帯・知らせ ----
  function renderTop(state) {
    $("now").textContent = time(state.now);
    const notable = state.events.find((e) => ["ai", "dispatch", "talk"].includes(e.source) || (e.source === "door" && !SCREEN_KINDS.includes(e.kind)));
    $("headline").textContent = notable ? `${short(notable.at)}　${notable.text}` : state.unity.connected ? "いつもどおり" : "Unity の様子を待っています";
    const step = state.slowdowns.length || state.proposals.length ? "improve"
      : state.dispatch.length || state.robots.some((r) => r.condition !== "Normal" || r.stopped || r.held) ? "judge" : "measure";
    for (const key of ["measure", "judge", "improve"]) $(`step-${key}`).classList.toggle("on", key === step);
    const alerts = [];
    if (state.hub_age !== null && state.hub_age > 3) alerts.push("監視AI（hub）が止まっている");
    if (state.unity.stand_in) alerts.push("Unity の代役（模擬データ）で動いています");
    const alert = $("alert");
    alert.hidden = !alerts.length;
    alert.textContent = alerts.join(" / ");
    const now = Date.parse(state.now);
    const recent = state.events.filter((e) => ["ai", "dispatch", "talk"].includes(e.source) || (e.source === "door" && e.kind === "door1"))
      .filter((e) => now - Date.parse(e.at) < 120000).slice(0, 2);
    fill($("banners"), recent, (e) => {
      const li = el("li");
      const [word, tone] = { ai: ["改善", "improve"], dispatch: ["注意", "warn"], talk: ["会話", "talk"], door: ["扉", "door"] }[e.source];
      li.append(el("span", "tag " + tone, word), el("span", "", e.text));
      return li;
    }, "");
    if (!recent.length) $("banners").replaceChildren();
    const caption = { unity: `Unity のシーンから読んだ配置（物 ${layoutInfo.count} 個）と、0.5 秒ごとの位置`,
                      stand_in: "Unity の代役（模擬）が送った配置と位置",
                      copy: "Unity がまだつながっていないので、Unity の配置の写しを表示しています" }[layoutInfo.source] || "";
    $("map-caption").textContent = caption + (state.unity.connected ? `。最後の受信: ${ago(state.unity.age)}` : state.robots.length ? "。いまはつながっていないので、最後に受け取った位置" : "");
  }

  // ---- 下の段 ----
  function renderLower(state) {
    fill($("talk"), state.talk || [], (t) => {
      const li = el("li");
      li.append(el("span", "q", `${t.by || "人"}: ${t.question}`), el("span", "a", `ロボット ${t.robot}: ${t.answer || "（答えなし）"}`), el("span", "meta", time(t.at)));
      return li;
    }, "まだ話しかけていない（Unity でロボットをクリックして質問するか、T キーで声で聞く）");
    fill($("opinions"), state.opinions, (o) => {
      const li = el("li");
      if (o.source === "gemini") li.append(el("b", "", `Gemini の見立て: ${o.text}`), el("span", "why", `勧める対応: ${(o.detail && o.detail.recommendation) || "—"}　${time(o.at)}`));
      else if (o.source === "reply") li.append(el("b", "", `Bluesky の返信 @${(o.detail && o.detail.handle) || "?"}: ${o.text}`),
                                               el("span", "why", `${time(o.at)} に取り込み${o.detail && o.detail.in_record ? "　店の記録から参照した" : ""}`));
      else if (o.source === "record") li.append(el("b", "", "AT プロトコルの記録（店のアカウントのリポジトリ）"), el("span", "why", `${o.uri || ""}　${time(o.at)}`));
      else li.append(el("b", "", "Bluesky に共有した"), el("span", "why", `${o.url || ""}　${time(o.at)}`));
      return li;
    }, "まだない（扉 2 を開けたときにだけ届く）");
    fill($("events"), state.events, (e) => {
      const li = el("li");
      li.append(el("span", "src", e.source), document.createTextNode(`${time(e.at)}　${e.text}`));
      return li;
    }, "記録はまだない");
  }

  async function getJson(url) {
    const response = await fetch(url, { cache: "no-store", credentials: "same-origin" });
    if (response.status === 401) {
      window.location.assign("/login");   // 入り直し（セッションが切れた）
      throw new Error("login");
    }
    if (!response.ok) throw new Error(String(response.status));
    return response.json();
  }

  async function poll() {
    try {
      const state = await getJson("api/state");
      const key = `${state.scene.source}:${state.scene.rev}:${state.scene.boot}`;
      if (key !== layoutKey) {
        drawFloor(await getJson("api/layout"));
        layoutKey = key;
      }
      renderTop(state);
      renderSensors(state);
      renderGate(state);
      renderJobs(state);
      renderImprove(state);
      renderLower(state);
      drawLive(state);
    } catch (error) {
      if (error.message !== "login") {
        const alert = $("alert");
        alert.hidden = false;
        alert.textContent = "中核（web）に届かない。docker compose が動いているか確かめる";
      }
    } finally {
      setTimeout(poll, 1000);
    }
  }

  $("drill").addEventListener("click", async () => {
    const token = document.querySelector('meta[name="csrf-token"]').content;
    const note = $("drill-note");
    try {
      const response = await fetch("api/drill", { method: "POST", credentials: "same-origin", headers: { "X-CSRFToken": token } });
      const body = await response.json();
      note.textContent = body.queued ? "頼んだ。Unity（または代役）が 3 回捨てると扉 1 が自動で閉じる" : body.reason;
    } catch (error) {
      note.textContent = "頼めなかった";
    }
  });

  // ---- ルール AI: rules.json の数字だけで判断し、聞かれたことに根拠つきで答える ----
  function renderJudge(data) {
    const words = data.levels || {};
    const list = $("rule-levels");
    list.replaceChildren();
    const chip = (name, level, findings) => {
      const li = el("li", `level ${level}`);
      li.append(el("b", "", name), document.createTextNode(` ${words[level] || level}`));
      li.title = findings.length ? findings.map((f) => `${f.label}: ${f.value}${f.unit || ""}`).join(" / ") : "基準を超えたものはない";
      return li;
    };
    if (data.store) list.append(chip("店全体", data.store.level, data.store.findings));
    for (const robot of data.robots || []) list.append(chip(`ロボット ${robot.id}`, robot.level, robot.findings));
    if (!(data.robots || []).length) list.append(el("li", "empty", "Unity（または代役）がつながると判断を始める"));
    $("rule-problem").hidden = !data.problem;
    $("rule-problem").textContent = data.problem || "";
  }

  async function loadJudge() {
    try { renderJudge(await getJson("api/judge")); } catch (error) { /* 次でまた試す */ }
  }

  async function ask(question) {
    const chat = $("rule-chat");
    const li = el("li");
    li.append(el("span", "q", `あなた: ${question}`));
    const reply = el("span", "a", "考えています…");
    li.append(reply);
    chat.prepend(li);
    while (chat.children.length > 8) chat.lastChild.remove();
    try {
      const response = await fetch("api/ask", { method: "POST", credentials: "same-origin", cache: "no-store",
        headers: { "Content-Type": "application/json", "X-CSRFToken": csrf() }, body: JSON.stringify({ q: question }) });
      const body = await response.json();
      reply.textContent = response.ok ? `ルール AI: ${body.answer}` : (body.error || "答えられなかった");
      reply.className = `a rule ${response.ok ? body.level : "caution"}`;
    } catch (error) {
      reply.textContent = "中核に届かなかった";
    }
  }

  document.querySelectorAll(".ruleai .quick button").forEach((b) => b.addEventListener("click", () => ask(b.dataset.q)));
  $("rule-form").addEventListener("submit", (event) => {
    event.preventDefault();
    const input = $("rule-q");
    const q = input.value.trim();
    if (q) { ask(q); input.value = ""; }
  });
  loadJudge();
  setInterval(loadJudge, 3000);

  // ---- 外への共有（AT プロトコル）: 何を出し、誰の返信を受け取るかを店が選ぶ ----
  const shareForm = $("share-form");
  let shareDirty = false;
  const csrf = () => document.querySelector('meta[name="csrf-token"]').content;

  function shareControls(setting) {
    if (shareDirty) return;   // 書きかけの選択は上書きしない
    for (const input of shareForm.elements.share) input.checked = input.value === setting.share;
    for (const input of shareForm.elements.accept) input.checked = input.value === setting.accept;
    shareForm.elements.gemini.checked = setting.gemini;
    shareForm.elements.allow.value = setting.allow.join("\n");
    shareForm.elements.window_days.value = setting.window_days;
    shareForm.elements.recipients.value = (setting.recipients || []).map((r) => `${r.name} ${r.key}`).join("\n");
    $("allow-box").hidden = setting.accept !== "listed";
    $("recipients-box").hidden = setting.share !== "sealed";
  }

  function renderShare(data) {
    shareControls(data.setting);
    const preview = data.preview;
    const plan = $("share-plan");
    const post = $("share-preview");
    const recordBox = $("share-record-box");
    const going = [];
    if (preview.notable && preview.gemini) going.push("Gemini に要約を送る");
    const sealed = data.setting.share === "sealed";
    if (preview.notable && data.setting.share === "post") going.push("Bluesky に投稿する（公開）");
    if (preview.notable && sealed) going.push((data.setting.recipients || []).length
      ? `宛先 ${data.setting.recipients.length} 人だけが開ける、暗号化した記録を 1 件残す（投稿はしない）` : "宛先がいないので、何も残さない");
    else if (preview.notable && data.setting.share !== "off") going.push("店のリポジトリに記録を 1 件残す（公開）");
    plan.textContent = !preview.notable
      ? "いまは減速も故障もないので、外には何も出さない（前の投稿への返信だけ読む）"
      : going.length ? `${going.join("、")}。` : "外には何も出さない（共有しない）";
    post.hidden = !preview.post;
    post.textContent = preview.post || "";
    recordBox.hidden = !preview.record;
    $("share-record").textContent = preview.record ? JSON.stringify(preview.record, null, 2) : "";
    fill($("share-records"), data.records, (r) => {
      const li = el("li");
      li.append(el("b", "", `${time(r.at)}　${{ record: "記録だけ（公開）", sealed: "会社だけ（暗号化）" }[r.share] || "投稿と記録（公開）"}　意見 ${r.opinions} 件`), el("span", "uri", r.uri));
      return li;
    }, "まだない（SW2 で扉 2 を開け、減速か故障があると 1 件残る）");
    const who = data.handle ? `@${data.handle}` : data.repo;
    $("share-viewer").textContent = who
      ? `ロボもりを使わずに読む: core\\viewer\\index.html をブラウザで開き、${who} を入れる（PDS から直接読む）`
      : `記録の形: ${data.collection}（定義は core/lexicons）`;
  }

  async function loadShare() {
    try {
      renderShare(await getJson("api/share"));
    } catch (error) { /* 次の読み込みでまた試す */ }
  }

  shareForm.addEventListener("input", () => {
    shareDirty = true;
    $("allow-box").hidden = shareForm.elements.accept.value !== "listed";
    $("recipients-box").hidden = shareForm.elements.share.value !== "sealed";
    $("share-note").textContent = "まだ反映していない";
  });
  shareForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    const f = shareForm.elements;
    const body = { share: f.share.value, accept: f.accept.value, gemini: f.gemini.checked,
                   allow: f.allow.value.split(/\s+/).filter(Boolean), window_days: Number(f.window_days.value),
                   recipients: f.recipients.value };
    const note = $("share-note");
    try {
      const response = await fetch("api/share", { method: "POST", credentials: "same-origin", cache: "no-store",
                                                  headers: { "Content-Type": "application/json", "X-CSRFToken": csrf() }, body: JSON.stringify(body) });
      const data = await response.json();
      if (!response.ok) {
        note.textContent = data.dropped && data.dropped.length ? `使えない値: ${data.dropped.join(" ")}` : (data.error || "反映できなかった");
        return;
      }
      shareDirty = false;
      renderShare(data);
      note.textContent = "反映した（監査ログに残した）";
    } catch (error) {
      note.textContent = "反映できなかった";
    }
  });
  loadShare();
  setInterval(loadShare, 5000);

  // ---- 右から出る: Google Drive の受信箱 ----
  const drive = { data: null, folder: "all", open: null, read: new Set() };
  try { drive.read = new Set(JSON.parse(localStorage.getItem("robomori.drive.read") || "[]")); } catch (error) { drive.read = new Set(); }
  const saveRead = () => { try { localStorage.setItem("robomori.drive.read", JSON.stringify([...drive.read].slice(-500))); } catch (error) { /* 保存できなくても使える */ } };
  const FOLDERS = [
    ["all", "すべて", () => true],
    ["sent", "Drive へ送った", (i) => i.synced === "sent"],
    ["waiting", "まだ送っていない", (i) => i.synced === "waiting"],
    ["failed", "うまくいかなかった", (i) => i.synced === "failed" || i.kind === "error"],
    ["scheduled", "時間割の書き出し", (i) => i.kind === "scheduled"],
    ["door2", "扉 2（SW2 で開けた）", (i) => i.kind !== "scheduled"],
    ["log", "写しの記録（PC 側）", null],
  ];
  const SYNC = { sent: ["Drive へ送った", "sent"], waiting: ["まだ", "waiting"], failed: ["失敗", "failed"] };

  function renderDrive() {
    const data = drive.data;
    if (!data) return;
    const unread = data.items.filter((i) => !drive.read.has(i.id)).length;
    const badge = $("drive-badge");
    badge.hidden = !unread;
    badge.textContent = String(unread);
    const status = $("drive-status");
    status.textContent = data.drive.to
      ? `写す先: ${data.drive.to}${data.drive.found ? "" : "（見つからない）"}　/　この PC: ${data.folder}　/　送った ${data.counts.sent} 件・まだ ${data.counts.waiting} 件・失敗 ${data.counts.failed} 件`
      : `Google Drive for desktop がまだ見つかっていない（PC で入れて店のアカウントでログインすると、start.ps1 が自動で写す）。この PC: ${data.folder}`;
    const folders = $("drive-folders");
    folders.replaceChildren();
    for (const [key, name, test] of FOLDERS) {
      const button = el("button", "", null);
      button.type = "button";
      button.setAttribute("aria-pressed", String(drive.folder === key));
      button.append(el("span", "", name), el("span", "count", test ? String(data.items.filter(test).length) : String(data.log.length)));
      button.addEventListener("click", () => { drive.folder = key; renderDrive(); });
      const li = el("li");
      li.append(button);
      folders.append(li);
    }
    const dirs = $("drive-dirs");
    dirs.replaceChildren();
    const here = el("li");
    here.append(el("b", "", "この PC"), document.createTextNode(`${data.folder}（${data.items.length} 件）`));
    const there = el("li");
    there.append(el("b", "", "Google Drive"), document.createTextNode(data.drive.to ? `${data.drive.to}（送った ${data.counts.sent} 件）` : "まだ決まっていない"));
    dirs.append(here, there);
    const list = $("drive-list");
    list.replaceChildren();
    if (drive.folder === "log") {
      fill(list, data.log, (entry) => {
        const li = el("li");
        const button = el("button");
        button.type = "button";
        button.append(el("span", "subject", entry.kind === "drive" ? (entry.ok ? "Drive の同期フォルダを見つけた" : "Drive の同期フォルダがない") : entry.file),
          el("span", "line2", ""), el("span", "preview", entry.ok ? (entry.to || "") : (entry.error || "")));
        button.querySelector(".line2").append(el("span", "", time(entry.at)), el("span", "sync " + (entry.ok ? "sent" : "failed"), entry.ok ? "できた" : "できなかった"));
        li.append(button);
        return li;
      }, "PC 側の鍵（start.ps1）がまだ何も写していない");
      return;
    }
    const test = (FOLDERS.find((f) => f[0] === drive.folder) || FOLDERS[0])[2];
    fill(list, data.items.filter(test), (item) => {
      const li = el("li");
      const button = el("button", drive.read.has(item.id) ? "" : "unread");
      button.type = "button";
      if (drive.open === item.id) button.setAttribute("aria-current", "true");
      const [word, tone] = SYNC[item.synced] || ["—", ""];
      const line2 = el("span", "line2");
      line2.append(el("span", "", `${item.at.replace("T", " ").slice(0, 16)}・記録 ${item.records} 件`), el("span", "sync " + tone, word));
      button.append(el("span", "subject", item.title), line2, el("span", "preview", item.preview || ""));
      button.addEventListener("click", () => openItem(item.id));
      li.append(button);
      return li;
    }, "このフォルダには、まだ何もない");
  }

  function renderDoc(target, text) {
    // 書き出した Markdown を、文字のまま見やすく並べる（HTML としては解釈しない）
    let list = null, code = null;
    for (const line of text.split("\n")) {
      if (line.startsWith("```")) {
        if (code) { code = null; } else { code = el("pre"); target.append(code); }
        list = null;
        continue;
      }
      if (code) { code.textContent += line + "\n"; continue; }
      if (line.startsWith("    ")) { target.append(el("pre", "", line.trim())); list = null; continue; }
      if (line.startsWith("# ")) { list = null; continue; }   // 件名として上に出している
      if (line.startsWith("## ")) { target.append(el("h4", "", line.slice(3))); list = null; continue; }
      if (line.startsWith("### ")) { target.append(el("h5", "", line.slice(4))); list = null; continue; }
      if (line.startsWith("- ")) {
        if (!list) { list = el("ul"); target.append(list); }
        list.append(el("li", "", line.slice(2)));
        continue;
      }
      list = null;
      if (line.trim()) target.append(el("p", "", line));
    }
  }

  async function openItem(id) {
    drive.open = id;
    drive.read.add(id);
    saveRead();
    renderDrive();
    const pane = $("drive-read");
    pane.replaceChildren(el("p", "sub", "読み込んでいます…"));
    try {
      const item = await getJson(`api/drive/${encodeURIComponent(id)}`);
      const summary = (drive.data.items || []).find((i) => i.id === id) || {};
      pane.replaceChildren();
      pane.append(el("h3", "", summary.title || id), el("p", "meta", `${item.at.replace("T", " ")}　記録 ${item.records} 件`));
      const files = el("ul", "files");
      for (const f of summary.files || []) {
        const li = el("li");
        const state = f.sent ? `Drive へ送った（${time(f.sent.at)}）` : f.failed ? `失敗: ${f.failed.error}` : "まだ送っていない";
        li.append(el("span", "", `${f.name}（${Math.max(1, Math.round(f.size / 1024))} KB）`), el("span", "sync " + (f.sent ? "sent" : f.failed ? "failed" : "waiting"), state));
        files.append(li);
      }
      pane.append(files);
      const box = (title, text) => {
        const b = el("div", "box");
        b.append(el("h4", "", title), el("div", "", text));
        pane.append(b);
      };
      if (item.post_url) {
        const b = el("div", "box");
        const a = el("a", "", item.post_url);
        if (/^https:\/\/bsky\.app\//.test(item.post_url)) { a.href = item.post_url; a.rel = "noopener noreferrer"; a.target = "_blank"; }
        b.append(el("h4", "", "Bluesky の投稿"), a);
        pane.append(b);
      }
      if (item.errors.length) box("うまくいかなかったこと", item.errors.join("\n"));
      const doc = el("div", "doc");
      renderDoc(doc, item.text);
      if (item.cut) doc.append(el("p", "sub", "（長いので途中までを表示）"));
      pane.append(doc);
    } catch (error) {
      if (error.message !== "login") pane.replaceChildren(el("p", "bad", "読めなかった（消されたか、書き出しフォルダに届かない）"));
    }
  }

  async function pollDrive() {
    try {
      drive.data = await getJson("api/drive");
      renderDrive();
    } catch (error) { /* 次の回に読み直す */ }
    setTimeout(pollDrive, $("drive").hidden ? 15000 : 5000);
  }

  function setDrive(open) {
    const panel = $("drive");
    panel.hidden = !open;
    $("drive-open").setAttribute("aria-expanded", String(open));
    if (open) {
      $("drive-title").focus();
      getJson("api/drive").then((data) => { drive.data = data; renderDrive(); }).catch(() => {});
    } else {
      $("drive-open").focus();
    }
  }
  $("drive-open").addEventListener("click", () => setDrive($("drive").hidden));
  $("drive-close").addEventListener("click", () => setDrive(false));
  document.addEventListener("keydown", (event) => { if (event.key === "Escape" && !$("drive").hidden) setDrive(false); });

  poll();
  pollDrive();
})();
