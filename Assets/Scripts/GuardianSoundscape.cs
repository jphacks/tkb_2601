using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// Makes what happens audible and readable: procedurally generated sound effects (no audio assets),
    /// short robot voice lines (VOICEVOX:ずんだもん, StreamingAssets/Robomori/voice), a quiet room tone, a
    /// motor hum that follows each robot's speed, captions for each event and running counters.
    /// It only observes the simulation (robot states, tasks, the decision card, the diners) and changes
    /// nothing. M mutes, N hides the captions and counters.
    /// </summary>
    public sealed class GuardianSoundscape : MonoBehaviour
    {
        public KeyCode MuteKey = KeyCode.M, CaptionKey = KeyCode.N;
        [Range(0, 1)] public float Volume = .85f;
        public bool ShowCaptions = true;

        private const int Rate = 24000;
        private GuardianSimulator sim;
        private AudioSource ui, voice, room;
        private readonly Dictionary<string, AudioSource> robotSources = new Dictionary<string, AudioSource>();
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(), voices = new Dictionary<string, AudioClip>();
        private readonly System.Random random = new System.Random(2026);
        private float nextClink;

        private sealed class Track { public RobotCondition Condition; public bool Stopped; }
        private readonly Dictionary<string, Track> robots = new Dictionary<string, Track>();
        private readonly Dictionary<string, string> owners = new Dictionary<string, string>();
        private readonly Dictionary<string, TaskPhase> phases = new Dictionary<string, TaskPhase>();
        private int accepted, rejected, selection;
        private GuardianDinerFlow subscribed;
        private GuardianOpeningScene? scene;

        private int delivered, cleared, handovers, safetyStops, paid, shown = -1;
        // Per robot: dishes served, tables cleared, jobs taken over from another robot (what a robot says when asked).
        private readonly Dictionary<string, int[]> tally = new Dictionary<string, int[]>();
        private static readonly int[] NoTally = new int[3];
        /// <summary>Jobs finished by a robot that was not in full health: work that kept going instead of stopping.</summary>
        internal int DegradedJobs { get; private set; }
        internal int Handovers => handovers;
        internal int SafetyStops => safetyStops;
        private float lastClock;
        private struct Caption { public string Text; public Color Color; public float Until; }
        private readonly List<Caption> captions = new List<Caption>();
        private string counters = "";
        private GUIStyle captionStyle, counterStyle;
        private Texture2D pixel;
        private Font font;

        // One voice at a time: important lines wait their turn, light ones are dropped while busy.
        private readonly Queue<(string key, Vector3 at)> waiting = new Queue<(string, Vector3)>();
        private float voiceFreeAt;
        private static readonly HashSet<string> Important = new HashSet<string> { "stop", "warning", "limited", "recovered" };

        private static readonly Color Good = new Color(.4f, .86f, .5f), Caution = new Color(1f, .78f, .25f), Strong = new Color(.98f, .55f, .3f),
            Danger = new Color(1f, .4f, .36f), Info = new Color(.55f, .78f, 1f);

        public void Initialize(GuardianSimulator simulator)
        {
            sim = simulator;
            BuildClips();
            LoadVoices(Path.Combine(Application.streamingAssetsPath, "Robomori", "voice"));
            ui = Source2D("UI sounds", .9f);
            voice = Source2D("Robot voice", 1f);
            room = Source2D("Room tone", .09f);
            room.clip = clips["room"]; room.loop = true; room.Play();
            GuardianPhoneLink.RemoteStop += OnPhoneStop;
            GuardianRepairCrew.RepairStarted += OnRepair;
            GuardianRepairCrew.RepairFinished += OnRepaired;
            nextClink = Time.time + 3;
        }

        // ---- observing the simulation ----
        private void LateUpdate()
        {
            if (sim == null) return;
            if (Input.GetKeyDown(MuteKey)) AudioListener.volume = AudioListener.volume > 0 ? 0 : 1;
            if (Input.GetKeyDown(CaptionKey)) ShowCaptions = !ShowCaptions;
            if (subscribed != sim.Diners && sim.Diners != null)
            {
                if (subscribed != null) Unsubscribe();
                subscribed = sim.Diners; subscribed.GuestEntered += OnGuestEntered; subscribed.GuestPaying += OnGuestPaying; subscribed.GuestCalled += OnGuestCalled;
                subscribed.GuestLeaving += OnGuestLeaving; subscribed.GuestLeft += OnGuestLeft;
            }
            if (sim.DemoClock < lastClock) ResetTracking();  // the scenario was reset (task ids start again)
            lastClock = sim.DemoClock;
            WatchScene();
            WatchRobots();
            WatchTasks();
            WatchCard();
            Ambience();
            PlayWaitingVoice();
        }

        private void ResetTracking()
        {
            robots.Clear(); owners.Clear(); phases.Clear(); captions.Clear(); waiting.Clear();
            delivered = cleared = handovers = safetyStops = paid = 0; shown = -1; scene = null;
            tally.Clear(); DegradedJobs = 0;
        }

        /// <summary>Served, cleared and taken over, for one robot since the scenario started.</summary>
        internal int[] Tally(string robot) => robot != null && tally.TryGetValue(robot, out var t) ? t : NoTally;

        private void Count(string robot, int what)
        {
            if (robot == null) return;
            if (!tally.TryGetValue(robot, out var t)) tally[robot] = t = new int[3];
            t[what]++;
        }

        /// <summary>Keeps event lines quiet while a robot answers a question.</summary>
        internal void HoldVoice(float seconds) => voiceFreeAt = Mathf.Max(voiceFreeAt, Time.unscaledTime + seconds);

        /// <summary>A caption each time the shop's opening moves on to its next scene (GuardianDinerFlow).</summary>
        private void WatchScene()
        {
            var diners = sim.Diners;
            if (diners == null || !diners.Running || diners.Scene == scene) return;
            scene = diners.Scene;
            switch (diners.Scene)
            {
                case GuardianOpeningScene.Clearing: Add("▶ シーン1　前のお客様のお皿を、ロボットが片付けに向かいます", Info); break;
                case GuardianOpeningScene.Seating: Add("▶ シーン2　片付いたお席へ、お客様をご案内します", Info); break;
                case GuardianOpeningScene.Serving: Add("▶ シーン3　ご注文の料理を、ロボットがお席へお運びします", Info); break;
                default: Add("▶ ここから通常営業　ご案内・配膳・お会計・片付けが続きます", Info); break;
            }
        }

        private void WatchRobots()
        {
            foreach (var r in sim.Robots)
            {
                if (r == null || r.PhysicsDriver == null) continue;
                Hum(r);
                bool stopped = r.EmergencyStopped || r.PhysicsDriver.SafetyHeld;
                if (!robots.TryGetValue(r.RobotId, out var t)) { robots[r.RobotId] = new Track { Condition = r.Condition, Stopped = stopped }; continue; }
                Vector3 at = r.transform.position;
                if (stopped && !t.Stopped)
                { safetyStops++; Effect("buzz", at); Say("stop", at); Add("■ ロボット " + r.RobotId + "　安全のため停止しました", Danger); }
                else if (!stopped && t.Stopped)
                { Effect("resolved", at); Add("▶ ロボット " + r.RobotId + "　動き出しました", Good); }
                if (r.Condition != t.Condition)
                {
                    if (r.Condition == RobotCondition.Warning && t.Condition == RobotCondition.Normal)
                    { Effect("warn", at); Say("warning", at); Add("⚠ ロボット " + r.RobotId + "　振動↑　スープの配膳を休みます", Caution); }
                    else if (r.Condition == RobotCondition.Limited)
                    { Effect("limited", at); Say("limited", at); Add("⚠ ロボット " + r.RobotId + "　揺れ大　お皿の片付けだけ続けます", Strong); }
                    else if (r.Condition == RobotCondition.Normal)
                    { Effect("resolved", at); Say("recovered", at); Add("✓ ロボット " + r.RobotId + "　すべての仕事に戻りました", Good); }
                    else
                    { Effect("warn", at); Add("⚠ ロボット " + r.RobotId + "　スープ以外の仕事を続けます", Caution); }
                }
                t.Condition = r.Condition; t.Stopped = stopped;
            }
        }

        private void WatchTasks()
        {
            foreach (var task in sim.Tasks)
            {
                if (task == null || task.Id == null) continue;
                if (!phases.TryGetValue(task.Id, out var before)) { if (task.Phase == TaskPhase.Completed) continue; before = task.Phase; }
                string owner = task.AssignedTo != null ? task.AssignedTo.RobotId : null;
                if (owner != null)
                {
                    if (owners.TryGetValue(task.Id, out var previous) && previous != owner)
                    {
                        handovers++; Count(owner, 2);
                        Vector3 at = task.AssignedTo.transform.position;
                        Effect("handover", at); Say("handover", at);
                        Add("⇄ ロボット " + owner + " が ロボット " + previous + " の" + Job(task.Kind) + "を引き継ぎました", Info);
                    }
                    owners[task.Id] = owner;
                }
                if (task.Phase == TaskPhase.Completed && before != TaskPhase.Completed)
                {
                    string robot = owner ?? (owners.TryGetValue(task.Id, out var last) ? last : "?");
                    if (task.AssignedTo != null && task.AssignedTo.Condition != RobotCondition.Normal) DegradedJobs++;
                    Vector3 at = task.AssignedTo != null ? task.AssignedTo.transform.position : task.Destination;
                    if (task.Kind == DeliveryKind.EmptyDishes)
                    { cleared++; Count(robot, 1); Effect("clink", at); Add("✓ ロボット " + robot + "　" + (task.TableIndex + 1) + "番テーブルのお皿を片付けました", Good); }
                    else
                    { delivered++; Count(robot, 0); Effect("ding", at); Say("served", at); Add("✓ ロボット " + robot + "　" + Job(task.Kind) + "を" + (task.TableIndex + 1) + "番テーブルへ", Good); }
                    owners.Remove(task.Id); phases.Remove(task.Id);
                    continue;
                }
                if (task.Phase == TaskPhase.ToDropoff && before == TaskPhase.ToPickup && task.AssignedTo != null) Effect("pop", task.AssignedTo.transform.position);
                if (task.Phase != TaskPhase.Completed) phases[task.Id] = task.Phase;
            }
            int total = delivered + cleared * 7 + handovers * 53 + safetyStops * 311 + paid * 1597;  // rebuild the text only when a count changes
            if (total != shown) { shown = total; counters = "配膳 " + delivered + "　片付け " + cleared + "　引き継ぎ " + handovers + "　安全停止 " + safetyStops + "　お会計 " + paid; }
        }

        private void WatchCard()
        {
            var card = sim.DemoInterface;
            if (card == null) return;
            if (card.SelectionVersion != selection) { selection = card.SelectionVersion; if (card.SelectedId != null) Effect("tick", null); }
            if (card.AcceptedCount != accepted) { accepted = card.AcceptedCount; Effect("confirm", null); }
            if (card.RejectedCount != rejected) { rejected = card.RejectedCount; Effect("reject", null); }
        }

        private void OnPhoneStop(string robot, string by, string via, string heard)
        {
            Effect("confirm", null);
            string how = via == "voice" ? "ボイスメモ「" + heard + "」" : via == "talk" ? "「" + heard + "」" : "ボタン";
            Add("📱 " + by + "（" + how + "）→ " + (robot == "all" ? "全部のロボット" : "ロボット " + robot) + " を停止", Danger);
        }

        private void OnGuestEntered(Vector3 at) { Effect("door", at); Say("welcome", at); Add("🔔 お客様がいらっしゃいました", Info); }
        private void Unsubscribe()
        {
            subscribed.GuestEntered -= OnGuestEntered; subscribed.GuestPaying -= OnGuestPaying; subscribed.GuestCalled -= OnGuestCalled;
            subscribed.GuestLeaving -= OnGuestLeaving; subscribed.GuestLeft -= OnGuestLeft;
        }
        private void OnGuestLeaving(Vector3 at, int table) { Effect("pop", at, .5f); Add("↩ " + table + "番テーブルのお客様がお食事を終えて、お帰りです", Info); }
        private void OnGuestLeft(Vector3 at) { Add("またお越しくださいませ。お席が片付きしだい、次のお客様を店員がご案内します", Info); }
        private void OnRepair(string robot, Vector3 at) { Effect("tick", at); Add("◆ ロボット " + robot + "：修理スタッフが点検・修理しています（約10秒）", Caution); }
        private void OnRepaired(string robot, Vector3 at) { Effect("ding", at, .5f); Add("◆ ロボット " + robot + "：修理完了。人が確認するまで止まったまま", Info); }
        private void OnGuestCalled(Vector3 at, int table) { Effect("ding", null, .6f); Say("call", at); Add("♪ 店員：お待たせしました。" + table + "番テーブルへご案内します", Info); }
        private void OnGuestPaying(Vector3 at) { paid++; Effect("register", at); Say("thanks", at); Add("¥ お会計（セルフレジ）　ありがとうございました", Info); }

        private static string Job(DeliveryKind kind) => kind == DeliveryKind.Soup ? "スープ" : kind == DeliveryKind.Food ? "料理" : "お皿の片付け";

        // ---- playing ----
        private AudioSource Source2D(string name, float volume)
        {
            var go = new GameObject(name); go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>(); s.playOnAwake = false; s.spatialBlend = 0; s.volume = volume * Volume;
            return s;
        }

        private void Effect(string key, Vector3? at, float volume = .8f, float pitch = 1)
        {
            if (!clips.TryGetValue(key, out var clip)) return;
            if (at == null) { ui.pitch = pitch; ui.PlayOneShot(clip, volume); return; }
            // A short-lived positional one-shot: louder near the camera, panned to where it happens.
            var go = new GameObject("Sound " + key); go.transform.position = at.Value + Vector3.up;
            var s = go.AddComponent<AudioSource>();
            s.clip = clip; s.volume = volume * Volume; s.pitch = pitch; s.spatialBlend = .55f; s.minDistance = 4; s.maxDistance = 30; s.rolloffMode = AudioRolloffMode.Linear;
            s.Play(); Destroy(go, clip.length / Mathf.Max(.1f, pitch) + .1f);
        }

        /// <summary>The film's dialogue speaks instead: event voices are skipped (not queued) while set.</summary>
        internal bool EventVoicesMuted;

        private void Say(string key, Vector3 at)
        {
            if (EventVoicesMuted || !voices.ContainsKey(key)) return;
            if (Time.unscaledTime < voiceFreeAt)
            {
                if (Important.Contains(key) && waiting.Count < 2) waiting.Enqueue((key, at));
                return;
            }
            Speak(key);
        }

        private void PlayWaitingVoice() { if (waiting.Count > 0 && Time.unscaledTime >= voiceFreeAt) Speak(waiting.Dequeue().key); }

        private void Speak(string key)
        {
            var clip = voices[key];
            voice.volume = Volume; voice.PlayOneShot(clip);
            voiceFreeAt = Time.unscaledTime + clip.length + .25f;
        }

        private void Hum(GuardianRobot r)
        {
            if (!robotSources.TryGetValue(r.RobotId, out var s) || s == null)  // new robot, or the robot was rebuilt
            {
                s = r.gameObject.AddComponent<AudioSource>();
                s.clip = clips["hum"]; s.loop = true; s.spatialBlend = .7f; s.minDistance = 3; s.maxDistance = 25; s.rolloffMode = AudioRolloffMode.Linear;
                s.volume = 0; s.Play(); robotSources[r.RobotId] = s;
            }
            float speed = r.PhysicsDriver.PlanarVelocity.magnitude;
            s.volume = Mathf.MoveTowards(s.volume, Mathf.Clamp01(speed / 1.6f) * .16f * Volume, Time.deltaTime * .6f);
            s.pitch = .85f + Mathf.Clamp(speed, 0, 2.5f) * .22f;
        }

        private void Ambience()
        {
            if (Time.time < nextClink) return;
            nextClink = Time.time + 3 + (float)random.NextDouble() * 7;
            // Cutlery from one of the tables.
            Vector3 table = new Vector3(6.1f, .9f, new[] { -3.4f, 0f, 3.4f }[random.Next(3)]);
            Effect("clink", table, .22f + (float)random.NextDouble() * .15f, .9f + (float)random.NextDouble() * .3f);
        }

        // ---- captions and counters ----
        private void Add(string text, Color color)
        {
            captions.Insert(0, new Caption { Text = text, Color = color, Until = Time.unscaledTime + 5 });
            if (captions.Count > 3) captions.RemoveAt(captions.Count - 1);
        }

        private void OnGUI()
        {
            if (!ShowCaptions || sim == null) return;
            if (font == null)
            {
                font = GuardianFonts.Create(18);
                captionStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false };
                counterStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 14, alignment = TextAnchor.MiddleRight };
                counterStyle.normal.textColor = new Color(.92f, .94f, .97f);
                pixel = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave }; pixel.SetPixel(0, 0, Color.white); pixel.Apply();
            }
            float s = Mathf.Max(1f, Screen.height / 720f), w = Screen.width / s;
            Matrix4x4 saved = GUI.matrix; Color tint = GUI.color;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            // Counters, top right.
            var box = new Rect(w - 470, 10, 460, 28);
            GUI.color = new Color(0, 0, 0, .55f); GUI.DrawTexture(box, pixel); GUI.color = tint;
            GUI.Label(new Rect(box.x + 8, box.y, box.width - 16, box.height), counters, counterStyle);
            // Captions, top centre, newest first, fading out.
            float y = 46, now = Time.unscaledTime;
            foreach (var c in captions)
            {
                float alpha = Mathf.Clamp01((c.Until - now) / .6f);
                if (alpha <= 0) continue;
                float width = Mathf.Min(640, w - 40);
                var r = new Rect((w - width) / 2, y, width, 34);
                GUI.color = new Color(0, 0, 0, .7f * alpha); GUI.DrawTexture(r, pixel);
                GUI.color = new Color(c.Color.r, c.Color.g, c.Color.b, alpha); GUI.DrawTexture(new Rect(r.x, r.y, 5, r.height), pixel);
                captionStyle.normal.textColor = new Color(c.Color.r, c.Color.g, c.Color.b, alpha);
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x + 14, r.y, r.width - 20, r.height), c.Text, captionStyle);
                y += 38;
            }
            for (int i = captions.Count - 1; i >= 0; i--) if (captions[i].Until < now) captions.RemoveAt(i);
            GUI.matrix = saved; GUI.color = tint;
        }

        // ---- sound synthesis (no audio files) ----
        private void BuildClips()
        {
            const float TwoPi = Mathf.PI * 2;
            float Bell(float t, float f, float start, float decay)
            {
                if (t < start) return 0;
                float u = t - start;
                return Mathf.Sin(TwoPi * f * u) * Mathf.Exp(-decay * u) * (1 - Mathf.Exp(-u * 600));
            }
            clips["door"] = Make("door", 1.7f, t => .45f * Bell(t, 659.3f, 0, 2.4f) + .1f * Bell(t, 1318.5f, 0, 4) + .45f * Bell(t, 523.3f, .4f, 2.2f) + .1f * Bell(t, 1046.5f, .4f, 4));
            clips["ding"] = Make("ding", 1f, t => .4f * Bell(t, 1318.5f, 0, 5) + .2f * Bell(t, 1975.5f, 0, 7) + .1f * Bell(t, 2637f, 0, 9));
            clips["pop"] = Make("pop", .09f, t => .35f * Mathf.Sin(TwoPi * (900 - 4000 * t) * t) * Mathf.Exp(-t * 45));
            clips["warn"] = Make("warn", .7f, t => .38f * Bell(t, 880, 0, 6) + .38f * Bell(t, 698.5f, .17f, 6));
            clips["limited"] = Make("limited", .9f, t => .32f * (Bell(t, 784, 0, 7) + Bell(t, 659.3f, .14f, 7) + Bell(t, 523.3f, .28f, 6)));
            clips["buzz"] = Make("buzz", .5f, t =>
            {
                bool on = t < .18f || (t > .26f && t < .44f);
                float wave = Mathf.Sin(TwoPi * 330 * t) + Mathf.Sin(TwoPi * 990 * t) / 3 + Mathf.Sin(TwoPi * 1650 * t) / 5;
                return on ? .26f * wave : 0;
            });
            clips["resolved"] = Make("resolved", 1f, t => .26f * (Bell(t, 523.3f, 0, 5) + Bell(t, 659.3f, .09f, 5) + Bell(t, 784, .18f, 5) + Bell(t, 1046.5f, .27f, 4)));
            clips["handover"] = Make("handover", .7f, t => (t < .25f ? .3f * Mathf.Sin(TwoPi * (587 + 600 * t) * t) * (1 - t * 2) : 0) + .35f * Bell(t, 1174.7f, .22f, 6));
            clips["register"] = Make("register", .9f, t =>
            {
                float beep = (t < .07f || (t > .11f && t < .18f)) ? .3f * Mathf.Sin(TwoPi * 2093 * t) : 0;
                return beep + .3f * Bell(t, 2637, .28f, 6) + .2f * Bell(t, 3520, .3f, 8);
            });
            clips["tick"] = Make("tick", .05f, t => .3f * Mathf.Sin(TwoPi * 1500 * t) * Mathf.Exp(-t * 120));
            clips["confirm"] = Make("confirm", .35f, t => .3f * (Bell(t, 659.3f, 0, 12) + Bell(t, 987.8f, .08f, 10)));
            clips["reject"] = Make("reject", .25f, t => .22f * (Mathf.Sin(TwoPi * 220 * t) + Mathf.Sin(TwoPi * 233 * t)) * Mathf.Exp(-t * 6));
            clips["clink"] = Make("clink", .3f, t => .3f * Bell(t, 3135.9f, 0, 25) + .15f * Bell(t, 4186f, .01f, 30));
            // A seamless one-second motor loop (whole numbers of cycles) and a soft room tone.
            clips["hum"] = Make("hum", 1f, t => .45f * Mathf.Sin(TwoPi * 95 * t) + .22f * Mathf.Sin(TwoPi * 190 * t) + .08f * Mathf.Sin(TwoPi * 285 * t));
            clips["room"] = RoomTone(4f);
        }

        private static AudioClip Make(string name, float seconds, Func<float, float> wave)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(wave(i / (float)Rate), -1, 1);
            int fade = Mathf.Min(n / 4, Rate / 100);  // 10 ms fade-out so no clip ends with a click
            for (int i = 0; i < fade; i++) data[n - 1 - i] *= i / (float)fade;
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Low, filtered noise that loops without a seam (the tail is cross-faded into the head).</summary>
        private AudioClip RoomTone(float seconds)
        {
            int n = (int)(seconds * Rate), overlap = Rate / 2;
            var raw = new float[n + overlap];
            float brown = 0, smooth = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                brown = Mathf.Clamp(brown + ((float)random.NextDouble() * 2 - 1) * .02f, -1, 1) * .998f;
                smooth += (brown - smooth) * .08f;
                raw[i] = smooth;
            }
            var data = new float[n];
            float peak = 1e-4f;
            for (int i = 0; i < n; i++)
            {
                float v = raw[i];
                if (i < overlap) { float k = i / (float)overlap; v = raw[n + i] * (1 - k) + raw[i] * k; }
                data[i] = v; peak = Mathf.Max(peak, Mathf.Abs(v));
            }
            for (int i = 0; i < n; i++) data[i] *= .8f / peak;
            var clip = AudioClip.Create("room", n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ---- voice lines ----
        private void LoadVoices(string folder)
        {
            string list = Path.Combine(folder, "lines.tsv");
            if (!File.Exists(list)) { Debug.LogWarning("[GuardianSound] no voice lines at " + folder); return; }
            foreach (string line in File.ReadAllLines(list))
            {
                if (line.StartsWith("#") || !line.Contains("\t")) continue;
                string key = line.Substring(0, line.IndexOf('\t'));
                string path = Path.Combine(folder, key + ".wav");
                if (!File.Exists(path)) continue;
                try { var clip = ReadWav(key, File.ReadAllBytes(path)); if (clip != null) voices[key] = clip; }
                catch (Exception e) { Debug.LogWarning("[GuardianSound] " + key + ".wav: " + e.Message); }
            }
        }

        /// <summary>16-bit PCM WAV (as VOICEVOX writes it) to an AudioClip.</summary>
        internal static AudioClip ReadWav(string name, byte[] bytes)
        {
            var data = DecodeWav(bytes, out int channels, out int rate);
            if (data == null) return null;
            // Same loudness for every line, whatever voice or settings made the file.
            float peak = 0;
            foreach (float v in data) peak = Mathf.Max(peak, Mathf.Abs(v));
            if (peak > .01f) for (int i = 0; i < data.Length; i++) data[i] *= .9f / peak;
            var clip = AudioClip.Create(name, data.Length / channels, channels, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Samples of a 16-bit PCM RIFF/WAVE file, or null for anything else.</summary>
        internal static float[] DecodeWav(byte[] bytes, out int channels, out int rate)
        {
            channels = 1; rate = 24000;
            if (bytes == null || bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' || bytes[8] != 'W') return null;
            int bits = 16, format = 1, at = 12;
            while (at + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                int size = BitConverter.ToInt32(bytes, at + 4);
                if (size < 0) return null;
                if (id == "fmt " && at + 24 <= bytes.Length)
                {
                    format = BitConverter.ToInt16(bytes, at + 8); channels = BitConverter.ToInt16(bytes, at + 10);
                    rate = BitConverter.ToInt32(bytes, at + 12); bits = BitConverter.ToInt16(bytes, at + 22);
                }
                else if (id == "data")
                {
                    if (format != 1 || bits != 16 || channels < 1 || rate <= 0) return null;
                    size = Math.Min(size, bytes.Length - at - 8);
                    var data = new float[size / (2 * channels) * channels];
                    for (int i = 0; i < data.Length; i++) data[i] = BitConverter.ToInt16(bytes, at + 8 + i * 2) / 32768f;
                    return data;
                }
                at += 8 + size + (size & 1);
            }
            return null;
        }

        private void OnDestroy()
        {
            if (subscribed != null) Unsubscribe();
            GuardianPhoneLink.RemoteStop -= OnPhoneStop;
            GuardianRepairCrew.RepairStarted -= OnRepair;
            GuardianRepairCrew.RepairFinished -= OnRepaired;
            foreach (var c in clips.Values) if (c != null) Destroy(c);
            foreach (var c in voices.Values) if (c != null) Destroy(c);
            if (pixel != null) Destroy(pixel);
            GuardianFonts.Release(font);
        }
    }
}
