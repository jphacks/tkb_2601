using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// "任せている仕事": what ロボもり decides, on screen. For each robot: what it can physically do (from the
    /// vibration), how far its values can be trusted (checked one by one), the stricter of the two that is
    /// actually applied, and which kinds of work it is given (スープ / 料理 / 皿). Underneath, what that
    /// achieved: jobs that kept going on a robot that was not well, hand-overs, refused values, safe stops.
    /// When the Robomori core is connected its states are shown; otherwise Unity's own input check.
    /// B shows or hides it. Reads only; changes nothing.
    /// </summary>
    public sealed class GuardianCapabilityBoard : MonoBehaviour
    {
        public KeyCode ToggleKey = KeyCode.B;
        public bool Visible = true;
        /// <summary>For the film: only each robot's state and soup / food / dishes, top right, nothing else.</summary>
        public bool Compact;

        private static readonly DeliveryKind[] Kinds = { DeliveryKind.Soup, DeliveryKind.Food, DeliveryKind.EmptyDishes };
        private static readonly Color Good = new Color(.4f, .86f, .5f), Caution = new Color(1f, .78f, .25f), Strong = new Color(.98f, .55f, .3f),
            Danger = new Color(1f, .4f, .36f), Dim = new Color(1, 1, 1, .55f), Info = new Color(.6f, .8f, 1f);

        private sealed class Row { public string Ability, Trust, Cap, CapWord; public Color AbilityColor, TrustColor, CapColor, CapWordColor; public bool[] Allowed = new bool[3]; public bool Held; public float FlashUntil; public string Key = ""; }
        private readonly Dictionary<string, Row> rows = new Dictionary<string, Row>();
        private readonly Dictionary<string, float> refusedAt = new Dictionary<string, float>();
        private GuardianSimulator sim;
        private GuardianSoundscape sound;
        private GuardianNetworkBridge bridge;
        private GuardianEvidenceInput hooked;
        private GUIStyle title, head, cell, mark, foot;
        private Texture2D pixel;
        private Font font;
        private float nextRefresh;
        private string footer = "", source = "";

        /// <summary>Where the board sits (GUI pixels), so the presentation banner can keep clear of it.</summary>
        internal static Rect ScreenRect { get; private set; }

        public void Initialize(GuardianSimulator simulator)
        {
            sim = simulator;
            sound = GetComponent<GuardianSoundscape>();
            bridge = GetComponent<GuardianNetworkBridge>();
        }

        private bool Active => sim != null && !sim.DeveloperMode && !sim.CustomPresentationActive && sim.Mode == GuardianSimulationMode.Physics;

        private void Update()
        {
            if (!Active) return;
            if (Input.GetKeyDown(ToggleKey) && GUIUtility.keyboardControl == 0) Visible = !Visible;
            if (hooked != sim.DemoEvidence)
            {
                if (hooked != null) hooked.SampleRejected -= OnRefused;
                hooked = sim.DemoEvidence;
                if (hooked != null) hooked.SampleRejected += OnRefused;
            }
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .2f;
            foreach (var r in sim.Robots) if (r != null) Refresh(r);
            int degraded = sound != null ? sound.DegradedJobs : 0, handovers = sound != null ? sound.Handovers : 0, stops = sound != null ? sound.SafetyStops : 0;
            int refused = sim.DemoEvidence != null ? sim.DemoEvidence.Rejected : 0;
            footer = "不調でも続けた仕事 " + degraded + "　引き継ぎ " + handovers + "\n受け付けなかった値 " + refused + "　安全側の停止 " + stops;
            source = bridge != null && bridge.Live ? "判定：ロボもりコア（この PC の中継からの状態）" : bridge != null && bridge.Silent ? "判定：コアからの知らせが途絶 → " + bridge.HeldNames + "を保持" : "判定：Unity 単体（模擬の値を検査）";
        }

        private void OnRefused(string robot, string status) { refusedAt[robot] = Time.unscaledTime; }

        private void Refresh(GuardianRobot r)
        {
            if (!rows.TryGetValue(r.RobotId, out var row)) rows[r.RobotId] = row = new Row();
            GuardianHumanActor actor = null;
            if (sim.HumanExperienceActive) sim.HumanExperience.Actors.TryGetValue(r.RobotId, out actor);
            var interlock = r.GetComponent<GuardianMotionInterlock>();
            bool held = r.EmergencyStopped || (r.PhysicsDriver != null && r.PhysicsDriver.SafetyHeld) || (interlock != null && interlock.Held)
                || (actor != null && (actor.HoldStopped || actor.AwaitingDecision || actor.MobilityUnsafe));
            RobotCondition ability = r.Condition;
            string trustText = null;
            Color trustColor = Good;
            if (bridge != null && bridge.Live && bridge.TryGetState(r.RobotId, out string capability, out string trust, out string final))
            {
                if (Enum.TryParse(capability, out RobotCondition c)) ability = c;
                trustText = trust == "Trusted" ? "確か" : trust == "Suspect" ? "疑い" : trust == "Untrusted" ? "信用しない" : trust;
                trustColor = trust == "Trusted" ? Good : trust == "Suspect" ? Strong : Danger;
                if (final == "Hold") held = true;
            }
            else
            {
                GuardianEvidenceChannel ch = null;
                sim.DemoEvidence?.Channels.TryGetValue(r.RobotId, out ch);
                bool refusedLately = refusedAt.TryGetValue(r.RobotId, out var at) && Time.unscaledTime - at < 6;
                if (ch == null) { trustText = "—"; trustColor = Dim; }
                else if (refusedLately) { trustText = "不正な値を検出"; trustColor = Danger; }
                else if (!ch.Connected || !ch.Fresh) { trustText = "途絶"; trustColor = Danger; }
                else if (interlock != null && interlock.Latched != GuardianMotionHold.None) { trustText = "確か（人の確認待ち）"; trustColor = Caution; }
                else trustText = "確か";
            }
            RobotCondition cap = Stricter(ability, r.Condition);
            row.Ability = Word(ability); row.AbilityColor = ColorOf(ability);
            row.Trust = trustText; row.TrustColor = trustColor;
            row.Cap = held ? "保持" : Word(cap); row.CapColor = held ? Danger : ColorOf(cap);
            row.CapWord = Word(cap); row.CapWordColor = ColorOf(cap);
            // What it may carry once it moves; dimmed while it is held for a person.
            row.Held = held;
            for (int i = 0; i < Kinds.Length; i++) row.Allowed[i] = GuardianCapabilityPolicy.Allows(cap, Kinds[i]);
            // Compact ignores momentary holds (someone walking by), so a row flashes only when its work changes.
            string key = (Compact ? row.CapWord : row.Ability + row.Trust + row.Cap + row.Held) + row.Allowed[0] + row.Allowed[1] + row.Allowed[2];
            if (key != row.Key) { if (row.Key.Length > 0) row.FlashUntil = Time.unscaledTime + 2.5f; row.Key = key; }
        }

        private static RobotCondition Stricter(RobotCondition a, RobotCondition b) => (int)a >= (int)b ? a : b;
        private static string Word(RobotCondition c) => c == RobotCondition.Normal ? "正常" : c == RobotCondition.Warning ? "注意" : "制限";
        private static Color ColorOf(RobotCondition c) => c == RobotCondition.Normal ? Good : c == RobotCondition.Warning ? Caution : Strong;

        private const string KeysText = "B：表を隠す　P：発表モード";

        private void EnsureStyles()
        {
            if (font != null) return;
            font = GuardianFonts.Create(16);
            title = new GUIStyle(GUI.skin.label) { font = font, fontSize = 16, fontStyle = FontStyle.Bold, wordWrap = false };
            title.normal.textColor = Color.white;
            head = new GUIStyle(GUI.skin.label) { font = font, fontSize = 11, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            head.normal.textColor = Dim;
            cell = new GUIStyle(GUI.skin.label) { font = font, fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            mark = new GUIStyle(cell) { fontSize = 17 };
            foot = new GUIStyle(GUI.skin.label) { font = font, fontSize = 12, wordWrap = false };
            foot.normal.textColor = new Color(.88f, .9f, .94f);
            pixel = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave }; pixel.SetPixel(0, 0, Color.white); pixel.Apply();
        }

        private void Fill(Rect r, Color c) { Color old = GUI.color; GUI.color = c; GUI.DrawTexture(r, pixel); GUI.color = old; }

        private void OnGUI()
        {
            if (!Active || !Visible || rows.Count == 0) { ScreenRect = Rect.zero; return; }
            EnsureStyles();
            if (Compact) { DrawCompact(); return; }
            float s = Mathf.Max(1f, Screen.height / 720f), w = Screen.width / s;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            const float width = 372, rowHeight = 30;
            // Measured with the font in use, so no line is cut at the top or bottom (the Japanese glyphs are taller
            // than the font size suggests).
            float inner = width - 20;
            float titleH = Mathf.Max(22, title.CalcHeight(new GUIContent("任せている仕事"), inner));
            float headH = Mathf.Max(16, head.CalcHeight(new GUIContent("値の確かさ"), 104));
            float footH = foot.CalcHeight(new GUIContent(footer), inner);
            float sourceH = foot.CalcHeight(new GUIContent(source), inner);
            float keysH = foot.CalcHeight(new GUIContent(KeysText), inner);
            float height = 6 + titleH + 4 + headH + 2 + rows.Count * rowHeight + 6 + footH + 2 + sourceH + keysH + 8;
            // Right edge, below the counters and the captions.
            var box = new Rect(w - width - 12, 170, width, height);
            ScreenRect = new Rect(box.x * s, box.y * s, box.width * s, box.height * s);
            Fill(box, new Color(.05f, .06f, .08f, .86f));
            Fill(new Rect(box.x, box.y, box.width, 3), Info);
            float x = box.x + 10, y = box.y + 6;
            GUI.Label(new Rect(x, y, inner, titleH), "任せている仕事", title);
            y += titleH + 4;
            float[] cols = { 34, 58, 104, 50, 40, 40, 40 };
            string[] names = { "", "能力", "値の確かさ", "上限", "スープ", "料理", "皿" };
            float cx = x;
            for (int i = 0; i < cols.Length; i++) { GUI.Label(new Rect(cx, y, cols[i], headH), names[i], head); cx += cols[i]; }
            y += headH + 2;
            float now = Time.unscaledTime;
            foreach (var r in sim.Robots)
            {
                if (r == null || !rows.TryGetValue(r.RobotId, out var row)) continue;
                if (now < row.FlashUntil) Fill(new Rect(box.x + 4, y, width - 8, rowHeight - 2), new Color(1f, .85f, .3f, .22f * Mathf.Clamp01((row.FlashUntil - now) / 1f)));
                cx = x;
                Who(new Rect(cx, y, cols[0], rowHeight), r.RobotId); cx += cols[0];
                Label(new Rect(cx, y, cols[1], rowHeight), row.Ability, row.AbilityColor, cell); cx += cols[1];
                Label(new Rect(cx, y, cols[2], rowHeight), row.Trust, row.TrustColor, cell); cx += cols[2];
                Label(new Rect(cx, y, cols[3], rowHeight), row.Cap, row.CapColor, cell); cx += cols[3];
                for (int i = 0; i < 3; i++)
                {
                    Color c = row.Allowed[i] ? Good : new Color(1f, .4f, .36f, .75f);
                    if (row.Held) c.a *= .35f;
                    Label(new Rect(cx, y, cols[4 + i], rowHeight), row.Allowed[i] ? "✓" : "✕", c, mark);
                    cx += cols[4 + i];
                }
                y += rowHeight;
            }
            Fill(new Rect(box.x + 10, y + 2, inner, 1), new Color(1, 1, 1, .15f));
            y += 6;
            GUI.Label(new Rect(x, y, inner, footH), footer, foot);
            y += footH + 2;
            foot.normal.textColor = Dim;
            GUI.Label(new Rect(x, y, inner, sourceH), source, foot);
            y += sourceH;
            GUI.Label(new Rect(x, y, inner, keysH), KeysText, foot);
            foot.normal.textColor = new Color(.88f, .9f, .94f);
            GUI.matrix = saved;
        }

        private void DrawCompact()
        {
            float s = Mathf.Max(1f, Screen.height / 720f), w = Screen.width / s;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            float[] cols = { 34, 58, 48, 48, 48 };
            string[] names = { "", "状態", "スープ", "料理", "皿" };
            const float rowHeight = 30;
            float width = 20 + cols.Sum();
            float titleH = Mathf.Max(22, title.CalcHeight(new GUIContent("今できる仕事"), width - 20));
            float headH = Mathf.Max(16, head.CalcHeight(new GUIContent("スープ"), 48));
            float height = 6 + titleH + 4 + headH + 2 + rows.Count * rowHeight + 8;
            var box = new Rect(w - width - 16, 16, width, height);
            ScreenRect = new Rect(box.x * s, box.y * s, box.width * s, box.height * s);
            Fill(box, new Color(.05f, .06f, .08f, .82f));
            Fill(new Rect(box.x, box.y, box.width, 3), Info);
            float x = box.x + 10, y = box.y + 6, cx = x;
            GUI.Label(new Rect(x, y, width - 20, titleH), "今できる仕事", title);
            y += titleH + 4;
            for (int i = 0; i < cols.Length; i++) { GUI.Label(new Rect(cx, y, cols[i], headH), names[i], head); cx += cols[i]; }
            y += headH + 2;
            float now = Time.unscaledTime;
            foreach (var r in sim.Robots)
            {
                if (r == null || !rows.TryGetValue(r.RobotId, out var row)) continue;
                if (now < row.FlashUntil) Fill(new Rect(box.x + 4, y, width - 8, rowHeight - 2), new Color(1f, .85f, .3f, .28f * Mathf.Clamp01((row.FlashUntil - now) / 1f)));
                cx = x;
                Who(new Rect(cx, y, cols[0], rowHeight), r.RobotId); cx += cols[0];
                Label(new Rect(cx, y, cols[1], rowHeight), row.CapWord, row.CapWordColor, cell); cx += cols[1];
                for (int i = 0; i < 3; i++)
                {
                    Label(new Rect(cx, y, cols[2 + i], rowHeight), row.Allowed[i] ? "✓" : "✕", row.Allowed[i] ? Good : new Color(1f, .4f, .36f, .85f), mark);
                    cx += cols[2 + i];
                }
                y += rowHeight;
            }
            GUI.matrix = saved;
        }

        /// <summary>The robot's letter beside a chip of its body colour, so a row matches the robot on screen.</summary>
        private void Who(Rect r, string id)
        {
            Fill(new Rect(r.x + 2, r.y + 8, 7, r.height - 16), GuardianRobotLivery.ColorOf(id));
            Label(new Rect(r.x + 8, r.y, r.width - 8, r.height), id, Color.white, cell);
        }

        private static void Label(Rect r, string text, Color color, GUIStyle style)
        {
            style.normal.textColor = color;
            GUI.Label(r, text, style);
        }

        private void OnDestroy()
        {
            if (hooked != null) hooked.SampleRejected -= OnRefused;
            if (pixel != null) Destroy(pixel);
            GuardianFonts.Release(font);
            ScreenRect = Rect.zero;
        }
    }
}
