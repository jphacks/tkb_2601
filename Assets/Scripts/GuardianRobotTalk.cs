using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Robomori;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// Talk to a robot: click it (or press 1-3), then pick a question or press T and ask out loud. The robot
    /// answers from its own state (RobomoriTalk.cs, no language model) in a VOICEVOX voice assembled from
    /// recorded phrases, with a speech bubble above it. Questions from paired phones arrive through the
    /// gateway and are answered the same way. Listening on the PC microphone is done by the gateway
    /// (scripts/start-phone.cmd); without it the quick questions still work.
    /// The only effect a question can have is a stop ("止まって"); moving again is never done by voice.
    /// </summary>
    public sealed class GuardianRobotTalk : MonoBehaviour
    {
        public KeyCode VoiceKey = KeyCode.T;
        [Range(0, 1)] public float Volume = 1;

        private static readonly string[] Quick = { "今の状況は？", "なんで？", "何を運んでるの？", "何ができる？", "実績は？", "どうすればいい？" };
        // A to E: each robot sounds a little different from the same recorded voice.
        private static readonly float[] Pitches = { 1f, 1.1f, .92f, 1.05f, .96f };

        private GuardianSimulator sim;
        private GuardianSoundscape sound;
        private GuardianPhoneLink link;
        private string folder;
        private string target, listeningFor;
        private float listeningUntil;
        private readonly Dictionary<string, float> stoppedSince = new Dictionary<string, float>();

        // The answer being said; its audio is assembled on a worker thread.
        private sealed class Prepared { public int Ticket; public float[] Samples; public int Rate; }
        private Prepared prepared;
        private int ticket;
        private AudioSource speaker;
        private AudioClip clip;
        private string speaking, said = "", asked = "", askedBy = "";
        private int exchanges;
        private float saidAt, saidLength, shownUntil;
        private string notice = "";
        private float noticeUntil;

        private bool Active => sim != null && !sim.DeveloperMode && !sim.CustomPresentationActive && sim.Mode == GuardianSimulationMode.Physics;

        public void Initialize(GuardianSimulator simulator)
        {
            sim = simulator;
            sound = GetComponent<GuardianSoundscape>();
            link = GetComponent<GuardianPhoneLink>();
            folder = Path.Combine(Application.streamingAssetsPath, "Robomori", "voice", "talk");
            string list = Path.Combine(folder, "talk.tsv");
            try { if (Talk.LoadPhrases(File.ReadAllText(list)) == 0) Debug.LogWarning("[GuardianTalk] no phrases in " + list); }
            catch (IOException e) { Debug.LogWarning("[GuardianTalk] " + e.Message); }
            if (speaker == null)
            {
                var go = new GameObject("Robot talk voice"); go.transform.SetParent(transform, false);
                speaker = go.AddComponent<AudioSource>();
                speaker.playOnAwake = false; speaker.spatialBlend = .45f; speaker.minDistance = 6; speaker.maxDistance = 40; speaker.rolloffMode = AudioRolloffMode.Linear;
            }
            if (sim.DemoInterface != null) { sim.DemoInterface.RobotClicked -= OnClicked; sim.DemoInterface.RobotClicked += OnClicked; sim.DemoInterface.ExtraUi = IsUi; }
            GuardianPhoneLink.Heard -= OnHeard; GuardianPhoneLink.Heard += OnHeard;
            GuardianPhoneLink.PhoneAsked -= OnPhoneAsked; GuardianPhoneLink.PhoneAsked += OnPhoneAsked;
        }

        // ---- what a robot knows about itself ----

        private bool Stopped(GuardianRobot r, out string cause, out GuardianHumanActor actor)
        {
            actor = null;
            if (sim.HumanExperienceActive) sim.HumanExperience.Actors.TryGetValue(r.RobotId, out actor);
            bool unsafeMotion = actor != null && actor.MobilityUnsafe, held = r.PhysicsDriver != null && r.PhysicsDriver.SafetyHeld;
            cause = unsafeMotion ? "unsafe" : held ? "safety" : r.EmergencyStopped ? "estop" : "";
            return cause.Length > 0;
        }

        public RobotFacts Facts(GuardianRobot r)
        {
            var f = new RobotFacts { Id = r.RobotId, Condition = r.Condition.ToString() };
            f.Stopped = Stopped(r, out f.Cause, out var actor);
            f.StoppedFor = f.Stopped && stoppedSince.TryGetValue(r.RobotId, out var since) ? Time.unscaledTime - since : 0;
            var task = r.CurrentTask ?? actor?.HeldTask;
            if (task != null)
            {
                f.Kind = task.Kind == DeliveryKind.Soup ? "soup" : task.Kind == DeliveryKind.Food ? "food" : "dishes";
                f.Table = task.TableIndex + 1;
            }
            if (sound != null) { var t = sound.Tally(r.RobotId); f.Served = t[0]; f.Cleared = t[1]; f.TookOver = t[2]; }
            return f;
        }

        // ---- read by GuardianHubLink (the core's screen); nothing here changes what a robot does ----

        /// <summary>What the robot would answer to "今の状況は？" right now, as text (empty without recorded phrases).</summary>
        public string Explain(GuardianRobot r) => r == null ? "" : Talk.Answer("今の状況は？", Facts(r)).Text ?? "";

        /// <summary>The last question a person asked and the robot's answer; Count goes up by one per exchange.</summary>
        public int LastExchange(out string robot, out string question, out string answer, out string by)
        {
            robot = speaking ?? ""; question = asked; answer = said; by = askedBy;
            return exchanges;
        }

        private GuardianRobot Find(string id)
        {
            foreach (var r in sim.Robots) if (r != null && r.RobotId == id) return r;
            return null;
        }

        // ---- asking ----

        /// <summary>Answers a question to one robot aloud. A stop request stops it.</summary>
        public void Ask(string robot, string question, string by, float confidence = 1)
        {
            var r = Find(robot);
            if (r == null) return;
            var facts = Facts(r);
            var answer = Talk.Answer(question, facts);
            if (answer.Stop && confidence < .35f) answer = Talk.Answer("", facts);  // too unsure to stop on
            if (answer.Stop) sim.SetEmergencyStop(r.RobotId, true);
            sim.Log("Talk " + r.RobotId + " '" + question + "' -> " + answer.Intent + (answer.Stop ? " (stopped)" : ""));
            Say(r.RobotId, answer.Keys, question, by);
        }

        private void OnClicked(string robot) { target = robot; }

        private void OnPhoneAsked(string robot, string keys, string question, string by)
        {
            var list = new List<string>();
            foreach (var key in keys.Split(' ')) if (Talk.HasPhrase(key)) list.Add(key);  // only recorded phrases can be said
            if (list.Count > 0 && Find(robot) != null) Say(robot, list.ToArray(), question, "スマホ（" + by + "）");
        }

        private void Listen()
        {
            if (target == null) { Notice("先に話しかけるロボットをクリックしてください。"); return; }
            if (link == null || !link.RequestListen(target)) { Notice("声で聞くには scripts/start-phone.cmd を起動してください（質問ボタンはそのまま使えます）。"); return; }
            listeningFor = target; listeningUntil = Time.unscaledTime + 10;
            sound?.HoldVoice(8);  // keep the robots' own lines out of the microphone
        }

        private void OnHeard(string robot, string text, float confidence, string error)
        {
            if (listeningFor == null) return;
            listeningFor = null; listeningUntil = 0;
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(text)) { Notice(string.IsNullOrEmpty(error) ? "聞き取れませんでした。もう一度どうぞ。" : error); return; }
            Ask(robot, text, "あなた", confidence);
        }

        private void Notice(string text) { notice = text; noticeUntil = Time.unscaledTime + 6; }

        // ---- speaking ----

        private void Say(string robot, string[] keys, string question, string by)
        {
            speaking = robot; said = Talk.TextOf(keys); asked = question ?? ""; askedBy = by ?? ""; exchanges++;
            saidAt = Time.unscaledTime; saidLength = Mathf.Max(2, said.Length * .13f); shownUntil = saidAt + saidLength + 5;
            int mine = ++ticket;
            string dir = folder;
            // Reading and joining the phrases takes a few milliseconds; keep it off the frame.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var p = Assemble(dir, keys); p.Ticket = mine;
                Volatile.Write(ref prepared, p);
            });
        }

        private static Prepared Assemble(string dir, string[] keys)
        {
            var p = new Prepared();
            var parts = new List<float[]>();
            int total = 0;
            foreach (var key in keys)
            {
                try
                {
                    string path = Path.Combine(dir, key + ".wav");
                    if (!File.Exists(path)) continue;
                    var data = GuardianSoundscape.DecodeWav(File.ReadAllBytes(path), out int channels, out int rate);
                    if (data == null || channels != 1) continue;
                    if (p.Rate == 0) p.Rate = rate; else if (rate != p.Rate) continue;
                    parts.Add(data); total += data.Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (total == 0) return p;
            int gap = p.Rate / 40;  // 25 ms between phrases
            p.Samples = new float[total + gap * (parts.Count - 1)];
            int at = 0;
            float peak = 0;
            foreach (var part in parts) { Array.Copy(part, 0, p.Samples, at, part.Length); at += part.Length + gap; }
            foreach (float v in p.Samples) peak = Math.Max(peak, Math.Abs(v));
            if (peak > .01f) for (int i = 0; i < p.Samples.Length; i++) p.Samples[i] *= .9f / peak;
            return p;
        }

        private void Play(Prepared p)
        {
            if (p.Ticket != ticket) return;  // a newer answer replaced it
            speaker.Stop();
            if (clip != null) { Destroy(clip); clip = null; }
            if (p.Samples == null) return;  // no recordings: the bubble alone
            clip = AudioClip.Create("Robot talk", p.Samples.Length, 1, p.Rate, false);
            clip.SetData(p.Samples, 0);
            int index = speaking != null && speaking.Length == 1 ? speaking[0] - 'A' : 0;
            speaker.pitch = index >= 0 && index < Pitches.Length ? Pitches[index] : 1;
            speaker.volume = Volume; speaker.clip = clip; speaker.Play();
            saidAt = Time.unscaledTime; saidLength = clip.length / speaker.pitch; shownUntil = saidAt + saidLength + 5;
            sound?.HoldVoice(saidLength + .3f);
        }

        private void Update()
        {
            if (sim == null) return;
            float now = Time.unscaledTime;
            foreach (var r in sim.Robots)
            {
                if (r == null) continue;
                if (!Stopped(r, out _, out _)) stoppedSince.Remove(r.RobotId);
                else if (!stoppedSince.ContainsKey(r.RobotId)) stoppedSince[r.RobotId] = now;
            }
            var p = Volatile.Read(ref prepared);
            if (p != null) { Volatile.Write(ref prepared, null); Play(p); }
            var who = speaking != null ? Find(speaking) : null;
            if (who != null) speaker.transform.position = who.transform.position + Vector3.up;
            if (listeningFor != null && now > listeningUntil) { listeningFor = null; Notice("聞き取れませんでした。もう一度どうぞ。"); }
            if (target != null && Find(target) == null) target = null;
            if (!Active) return;
            if (Input.GetKeyDown(KeyCode.Escape)) target = null;
            for (int i = 0; i < sim.Robots.Count && i < 9; i++) if (Input.GetKeyDown(KeyCode.Alpha1 + i)) target = sim.Robots[i].RobotId;
            if (Input.GetKeyDown(VoiceKey)) Listen();
        }

        // ---- screen: a talk bar over the bottom card, and a speech bubble over the robot ----

        private static float Scale => Mathf.Max(1f, Screen.height / 720f);
        private Rect bar;
        private GUIStyle titleStyle, chipStyle, micStyle, closeStyle, noticeStyle, bubbleStyle, nameStyle, askedStyle;
        private Texture2D pixel;
        private Font font;
        private readonly List<Texture2D> textures = new List<Texture2D>();

        private void Layout()
        {
            float s = Scale, w = Screen.width / s, h = Screen.height / s, width = Mathf.Min(860, w - 24);
            float top = sim.DemoInterface != null && sim.DemoInterface.CardVisible ? h - 226 : h - 92;
            bar = new Rect((w - width) / 2, top - 104, width, 96);
        }

        private bool IsUi(Vector2 screen)
        {
            if (!Active || target == null) return false;
            Layout();
            return bar.Contains(new Vector2(screen.x, Screen.height - screen.y) / Scale);
        }

        private Texture2D Solid(Color c) { var t = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave }; t.SetPixel(0, 0, c); t.Apply(); textures.Add(t); return t; }

        private GUIStyle Button(int size, Color c)
        {
            var st = new GUIStyle(GUI.skin.button) { font = font, fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            st.normal.background = Solid(c); st.hover.background = Solid(Color.Lerp(c, Color.white, .15f)); st.active.background = Solid(Color.Lerp(c, Color.black, .25f));
            st.normal.textColor = st.hover.textColor = st.active.textColor = Color.white;
            return st;
        }

        private void EnsureStyles()
        {
            if (font != null) return;
            font = GuardianFonts.Create(18);
            pixel = Solid(Color.white);
            titleStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 19, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            noticeStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 13, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            noticeStyle.normal.textColor = new Color(1f, .82f, .5f);
            chipStyle = Button(15, new Color(.2f, .25f, .32f));
            micStyle = Button(16, new Color(.17f, .42f, .78f));
            closeStyle = Button(18, new Color(.25f, .27f, .3f));
            bubbleStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 18, wordWrap = true };
            bubbleStyle.normal.textColor = new Color(.1f, .11f, .13f);
            nameStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = false };
            askedStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 13, wordWrap = true };
            askedStyle.normal.textColor = new Color(.35f, .38f, .43f);
        }

        private void Fill(Rect r, Color c) { Color old = GUI.color; GUI.color = c; GUI.DrawTexture(r, pixel); GUI.color = old; }

        private Color StateColor(GuardianRobot r)
        {
            if (r == null) return Color.gray;
            return Stopped(r, out _, out _) ? new Color(.95f, .3f, .27f) : r.Condition == RobotCondition.Limited ? new Color(.93f, .52f, .35f)
                : r.Condition == RobotCondition.Warning ? new Color(.98f, .7f, .1f) : new Color(.3f, .8f, .4f);
        }

        private void OnGUI()
        {
            if (!Active) return;
            EnsureStyles();
            Matrix4x4 saved = GUI.matrix;
            float s = Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            Action action = null;
            try
            {
                if (target != null) DrawBar(ref action);
                if (speaking != null && Time.unscaledTime < shownUntil) DrawBubble(s);
            }
            finally { GUI.matrix = saved; }
            action?.Invoke();
        }

        private void DrawBar(ref Action action)
        {
            Layout();
            var robot = Find(target);
            Fill(bar, new Color(.05f, .06f, .08f, .92f));
            Fill(new Rect(bar.x, bar.y, 7, bar.height), StateColor(robot));
            float x = bar.x + 20, y = bar.y + 8, w = bar.width - 32;
            titleStyle.normal.textColor = StateColor(robot);
            GUI.Label(new Rect(x, y, 300, 34), "ロボット " + target + " に話しかける", titleStyle);
            bool listening = listeningFor != null;
            bool linked = link != null && link.Connected;
            Rect mic = new Rect(bar.xMax - 50 - 216, y, 206, 34), close = new Rect(bar.xMax - 50, y, 36, 34);
            if (Time.unscaledTime < noticeUntil) GUI.Label(new Rect(x + 290, y - 4, mic.x - x - 296, 42), notice, noticeStyle);
            Color old = GUI.color;
            if (!linked && !listening) GUI.color = new Color(1, 1, 1, .5f);
            if (GUI.Button(mic, listening ? "● 聞いています…話してください" : "◎ 声で聞く（T キー）", micStyle) && !listening) action = Listen;
            GUI.color = old;
            if (GUI.Button(close, "×", closeStyle)) action = () => target = null;
            y += 44;
            const float gap = 8;
            float cw = (w - gap * (Quick.Length - 1)) / Quick.Length;
            for (int i = 0; i < Quick.Length; i++)
            {
                string q = Quick[i], id = target;
                if (GUI.Button(new Rect(x + i * (cw + gap), y, cw, 38), q, chipStyle)) action = () => Ask(id, q, "");
            }
        }

        private void DrawBubble(float s)
        {
            var robot = Find(speaking);
            var cam = Camera.main;
            if (robot == null || cam == null) return;
            float w = Screen.width / s, h = Screen.height / s;
            Vector3 p = cam.WorldToScreenPoint(robot.transform.position + Vector3.up * 1.3f);
            Vector2 at = p.z > 0 ? new Vector2(p.x / s, (Screen.height - p.y) / s) : new Vector2(w / 2, 120);
            float progress = saidLength > 0 ? Mathf.Clamp01((Time.unscaledTime - saidAt) / saidLength) : 1;
            string shown = said.Substring(0, Mathf.Min(said.Length, Mathf.CeilToInt(said.Length * progress)));
            float width = Mathf.Min(380, w - 24), inner = width - 28;
            string question = asked.Length > 0 ? (askedBy.Length > 0 ? askedBy + "：" : "") + "「" + asked + "」" : "";
            float qh = question.Length > 0 ? askedStyle.CalcHeight(new GUIContent(question), inner) + 2 : 0;
            float th = bubbleStyle.CalcHeight(new GUIContent(said), inner);
            float height = 12 + qh + 20 + th + 12;
            var box = new Rect(Mathf.Clamp(at.x - width / 2, 12, w - width - 12), Mathf.Clamp(at.y - height - 16, 12, h - height - 12), width, height);
            Fill(new Rect(box.x + 3, box.y + 4, box.width, box.height), new Color(0, 0, 0, .35f));
            Fill(box, new Color(.98f, .98f, .96f, .97f));
            Color state = StateColor(robot);
            Fill(new Rect(box.x, box.y, box.width, 5), state);
            if (box.yMax < at.y) Fill(new Rect(Mathf.Clamp(at.x, box.x + 12, box.xMax - 24) - 6, box.yMax, 12, Mathf.Min(14, at.y - box.yMax)), new Color(.98f, .98f, .96f, .97f));
            float y = box.y + 12;
            if (qh > 0) { GUI.Label(new Rect(box.x + 14, y, inner, qh), question, askedStyle); y += qh; }
            nameStyle.normal.textColor = Color.Lerp(state, Color.black, .35f);
            GUI.Label(new Rect(box.x + 14, y, inner, 20), "ロボット " + speaking + (speaker.isPlaying ? "　♪" : ""), nameStyle);
            GUI.Label(new Rect(box.x + 14, y + 20, inner, th), shown, bubbleStyle);
        }

        private void OnDestroy()
        {
            GuardianPhoneLink.Heard -= OnHeard;
            GuardianPhoneLink.PhoneAsked -= OnPhoneAsked;
            if (sim != null && sim.DemoInterface != null) { sim.DemoInterface.RobotClicked -= OnClicked; if (sim.DemoInterface.ExtraUi == (Func<Vector2, bool>)IsUi) sim.DemoInterface.ExtraUi = null; }
            if (clip != null) Destroy(clip);
            foreach (var t in textures) if (t != null) Destroy(t);
            GuardianFonts.Release(font);
        }
    }
}
