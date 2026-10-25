using System;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// The presentation, in four scenes on robot B, each one the README's strengths happening on screen:
    /// its vibration rises (soup is taken off it, the rest stays), forged "normal" values are refused,
    /// its sensor goes silent (it is held), and when the sensor comes back it still waits for a person.
    /// P starts and ends it; Space (or → / PageDown) moves on. A banner on the left says what is happening
    /// and what to click. While it runs, seeded faults are off and the repair crew starts nothing new;
    /// both come back when it ends. Everything goes through the same checks as the rest of the demo:
    /// it only feeds inputs (a vibration value, forged samples, a silent sensor) and asks the person.
    /// </summary>
    public sealed class GuardianPitchMode : MonoBehaviour
    {
        public KeyCode ToggleKey = KeyCode.P;
        public string Target = "B";
        public bool Active { get; private set; }
        public int Scene { get; private set; }

        private sealed class Step { public string Title, Body; public Func<string> Hint; public Action Enter; }
        private static readonly Color Accent = new Color(.6f, .8f, 1f), HintColor = new Color(1f, .85f, .4f), Dim = new Color(1, 1, 1, .55f);
        private Step[] steps;
        private GuardianSimulator sim;
        private bool restoreFaults, restorePause;
        private string note, hint = "", laidOut;
        private float nextHint, laidWidth, titleHeight, bodyHeight, hintHeight;
        private int laidScene = -1;
        private GUIStyle progressStyle, titleStyle, bodyStyle, hintStyle;
        private Texture2D pixel;
        private Font font;

        public void Initialize(GuardianSimulator simulator)
        {
            sim = simulator;
            steps = new[]
            {
                new Step { Title = "いつもの営業", Body = "3 台のロボットが、配膳と片付けを分担しています。\n右の表は、ロボもりが各ロボットに任せている仕事です。",
                    Hint = () => "Space で次へ　P で終了", Enter = Overview },
                new Step { Title = "① 振動が上がる", Body = "ロボット B の振動が上がりました。こぼれやすいスープだけを外し、料理と皿の片付けは任せたままにします。\nB はいったん止まり、続けるかは人が決めます。",
                    Hint = DecideHint, Enter = () => { RaiseVibration(); Focus(); } },
                new Step { Title = "② 偽の「正常」", Body = "「B は正常」という偽の値を 3 通送りました（古い値の再送・未来の時刻・知らない送り主）。\nどれも判断に使わず、B を止めます。偽の値で、状態がよく見えることはありません。",
                    Hint = () => "表の「受け付けなかった値」が増えます　Space で次へ", Enter = Forge },
                new Step { Title = "③ センサーが途絶える", Body = "ロボット B のセンサーから値が届かなくなりました。\n「異常の知らせ」を待たず、「正常の知らせ」が来なくなったら止めます。",
                    Hint = SilenceHint, Enter = () => Silence(true) },
                new Step { Title = "④ 戻っても、自動では動かない", Body = "センサーは戻りました。それでも B は自動では動き出しません。\n人が確かめて、再開を決めます。",
                    Hint = ResumeHint, Enter = () => { Silence(false); sim.RepairCrew?.CheckNow(Target); } },
                new Step { Title = "まとめ", Body = "・止めずに、できる仕事だけを任せ続ける\n・確かめられない値は、判断に使わない\n・知らせが途絶えたら止める\n・再開は人が決める\n・インターネットなしで、店の PC 1 台で動く",
                    Hint = () => "P で終了", Enter = Overview },
            };
        }

        private bool Usable => sim != null && !sim.DeveloperMode && !sim.CustomPresentationActive && sim.Mode == GuardianSimulationMode.Physics;
        private GuardianRobot Robot => sim.Robots.FirstOrDefault(r => r != null && r.RobotId == Target);
        private GuardianEvidenceChannel Channel => sim.DemoEvidence != null && sim.DemoEvidence.Channels.TryGetValue(Target, out var c) ? c : null;

        private void Update()
        {
            if (!Usable) { if (Active) End(); return; }
            if (GUIUtility.keyboardControl != 0) return;  // typing a question to a robot
            if (Input.GetKeyDown(ToggleKey)) { if (Active) End(); else Begin(); return; }
            if (Active && (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.PageDown))) Next();
            // The hint follows the robot (counting seconds, waiting for a click); refreshed 10 times a second, not per draw.
            if (Active && Scene >= 0 && Time.unscaledTime >= nextHint)
            {
                nextHint = Time.unscaledTime + .1f;
                string text = steps[Scene].Hint?.Invoke() ?? "";
                hint = note != null ? note + "\n" + text : text;
            }
        }

        public void Begin()
        {
            if (Active || !Usable || Robot == null) return;
            Active = true; Scene = -1;
            restoreFaults = sim.Settings.RandomFaults; sim.Settings.RandomFaults = false;
            if (sim.RepairCrew != null) { restorePause = !sim.RepairCrew.Paused; sim.RepairCrew.Paused = true; }
            var board = GetComponent<GuardianCapabilityBoard>();
            if (board != null) board.Visible = true;
            sim.Log("Pitch: started");
            Next();
        }

        public void End()
        {
            if (!Active) return;
            Active = false;
            var c = Channel;
            if (c != null) c.Silenced = false;
            if (restoreFaults) sim.Settings.RandomFaults = true;
            if (restorePause && sim.RepairCrew != null) sim.RepairCrew.Paused = false;
            restoreFaults = restorePause = false;
            sim.Log("Pitch: ended");
        }

        /// <summary>The next scene (Space); ends after the last.</summary>
        public void Advance() { if (Active) Next(); }

        private void Next()
        {
            if (Scene + 1 >= steps.Length) { End(); return; }
            Scene++; note = null; nextHint = 0;
            sim.Log("Pitch: scene " + (Scene + 1) + " " + steps[Scene].Title);
            try { steps[Scene].Enter?.Invoke(); }
            catch (Exception e) { note = "この場面を準備できませんでした"; Debug.LogException(e); }
        }

        // ---- what each scene does ----

        private void Overview() { sim.DemoInterface?.CameraRig?.ResetView(); }
        private void Focus() { var r = Robot; if (r != null) sim.DemoInterface?.CameraRig?.Focus(r.transform); }

        private void RaiseVibration()
        {
            // Middle of the Warning band: soup is off, food and dishes stay.
            sim.SubmitVibration(Target, (sim.Settings.WarningThreshold + sim.Settings.LimitedThreshold) * .5f);
        }

        private void Forge()
        {
            var input = sim.DemoEvidence; var c = Channel;
            if (input == null || c == null) { note = "この画面には模擬の入力がありません"; return; }
            Focus();
            double now = GuardianEvidenceInput.ReceiverNow;
            float normal = c.RawMin + (c.RawMax - c.RawMin) * c.Warning * .4f;
            GuardianEvidenceSample Forged(double stamp, string source) => new GuardianEvidenceSample
            { Timestamp = stamp, Source = source, Metric = c.Metric, Unit = c.Unit, Clock = GuardianEvidenceInput.ReceiverClock, Value = normal, Valid = true, Origin = c.Origin };
            // The last reading sent again, now saying "normal"; one stamped in the future to shut out the real
            // ones; one from a sender nobody registered. Each is checked like any other and refused.
            if (double.IsFinite(c.LastSourceTime)) input.Submit(Target, Forged(c.LastSourceTime, c.Source), now);
            input.Submit(Target, Forged(now + 30, c.Source), now);
            input.Submit(Target, Forged(now, "unregistered-sensor"), now);
        }

        private void Silence(bool silent)
        {
            var c = Channel;
            if (c == null) { note = "この画面には模擬の入力がありません"; return; }
            c.Silenced = silent;
            Focus();
        }

        // ---- what to say or click, as things change ----

        private bool Held(GuardianRobot r)
        {
            GuardianHumanActor a = null;
            if (sim.HumanExperienceActive) sim.HumanExperience.Actors.TryGetValue(r.RobotId, out a);
            var interlock = r.GetComponent<GuardianMotionInterlock>();
            return r.EmergencyStopped || (interlock != null && interlock.Held) || (a != null && (a.AwaitingDecision || a.HoldStopped));
        }

        private string DecideHint()
        {
            var r = Robot;
            if (r == null) return "";
            if (!Held(r)) return "人が判断し、B はできる仕事を続けています　Space で次へ";
            GuardianHumanActor a = null;
            if (sim.HumanExperienceActive) sim.HumanExperience.Actors.TryGetValue(r.RobotId, out a);
            var task = r.CurrentTask ?? a?.HeldTask;
            return task != null && task.Kind == DeliveryKind.Soup
                ? "ロボット B をクリック →「別のロボットへ引き継ぐ」でスープを渡す"
                : "ロボット B をクリック →「できる仕事を続ける」";
        }

        private string SilenceHint()
        {
            var c = Channel;
            if (c == null) return "Space で次へ";
            if (c.Fresh) return "最後の値から " + Math.Max(0, GuardianEvidenceInput.ReceiverNow - c.ReceivedAt).ToString("0.0") + " 秒…";
            return GuardianEvidenceInput.MaximumAgeSeconds.ToString("0.0") + " 秒届かず → B を止めました　Space で次へ";
        }

        private string ResumeHint()
        {
            var r = Robot;
            if (r == null) return "";
            if (sim.RepairCrew != null && sim.RepairCrew.AwaitingConfirm(Target)) return "B の上の「点検完了・再開する」を押す（人が決める）";
            if (sim.RepairCrew != null && sim.RepairCrew.Repairing(Target)) return "人の確認を受けて、待機場所へ戻ってから再開します";
            return Held(r) ? "ロボット B をクリックして、再開を決める" : "人の確認で再開しました　Space で次へ";
        }

        // ---- the banner ----

        private void EnsureStyles()
        {
            if (font != null) return;
            font = GuardianFonts.Create(16);
            progressStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 12, wordWrap = false };
            progressStyle.normal.textColor = Dim;
            titleStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = true };
            titleStyle.normal.textColor = Color.white;
            bodyStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 15, wordWrap = true };
            bodyStyle.normal.textColor = new Color(.9f, .92f, .95f);
            hintStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = true };
            hintStyle.normal.textColor = HintColor;
            pixel = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave }; pixel.SetPixel(0, 0, Color.white); pixel.Apply();
        }

        private void OnGUI()
        {
            if (!Active || !Usable || Scene < 0 || Scene >= steps.Length) return;
            EnsureStyles();
            float s = Mathf.Max(1f, Screen.height / 720f), w = Screen.width / s;
            Matrix4x4 saved = GUI.matrix; Color tint = GUI.color;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            var step = steps[Scene];
            // Left of the captions (centre, 640 wide), below the memory box when it is open.
            float width = Mathf.Clamp((w - 640) / 2 - 24, 260, 420), inner = width - 32;
            float top = GuardianMemoryOverlay.ScreenBottom > 0 ? GuardianMemoryOverlay.ScreenBottom / s + 8 : 12;
            if (!ReferenceEquals(laidOut, hint) || laidWidth != inner || laidScene != Scene)
            {
                laidOut = hint; laidWidth = inner; laidScene = Scene;
                titleHeight = titleStyle.CalcHeight(new GUIContent(step.Title), inner);
                bodyHeight = bodyStyle.CalcHeight(new GUIContent(step.Body), inner);
                hintHeight = hintStyle.CalcHeight(new GUIContent(hint), inner);
            }
            float th = titleHeight, bh = bodyHeight, hh = hintHeight;
            var box = new Rect(12, top, width, 14 + 18 + th + 8 + bh + 12 + hh + 14);
            GUI.color = new Color(.05f, .06f, .08f, .9f); GUI.DrawTexture(box, pixel);
            GUI.color = Accent; GUI.DrawTexture(new Rect(box.x, box.y, 6, box.height), pixel);
            GUI.color = new Color(1, 1, 1, .15f);
            float segment = (inner - (steps.Length - 1) * 4) / steps.Length;
            for (int i = 0; i < steps.Length; i++)
            {
                GUI.color = i <= Scene ? Accent : new Color(1, 1, 1, .15f);
                GUI.DrawTexture(new Rect(box.x + 18 + i * (segment + 4), box.y + 10, segment, 3), pixel);
            }
            GUI.color = Color.white;
            float x = box.x + 18, y = box.y + 14;
            GUI.Label(new Rect(x, y, inner, 18), "発表モード　" + (Scene + 1) + " / " + steps.Length, progressStyle); y += 18;
            GUI.Label(new Rect(x, y, inner, th), step.Title, titleStyle); y += th + 8;
            GUI.Label(new Rect(x, y, inner, bh), step.Body, bodyStyle); y += bh + 12;
            GUI.Label(new Rect(x, y, inner, hh), hint, hintStyle);
            GUI.matrix = saved; GUI.color = tint;
        }

        private void OnDestroy()
        {
            if (sim != null) End();
            if (pixel != null) Destroy(pixel);
            GuardianFonts.Release(font);
        }
    }
}
