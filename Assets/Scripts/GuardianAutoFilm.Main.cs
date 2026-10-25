using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// The 30-second main film, the team's agreed demo: "不調なロボットを止めずに、今できる仕事を任せ続ける".
    /// All three robots start healthy and nothing asks a person to stop them (OperatorSupervisionEnabled off).
    /// Robot B takes a soup order; its vibration rises, so the soup goes to whichever robot the simulator
    /// assigns while B keeps carrying food; it rises again and B clears dishes only. It opens on a "デモシナリオ"
    /// title card, runs at double speed with one subtitle line, the compact "今できる仕事" table, a pulse when B
    /// shakes and a line when the soup changes robots, and ends on a title card with the counts and the voice
    /// credit. Only the vibration values and the orders are staged; who may carry what and who takes over are the
    /// simulator's own decisions.
    /// Everything on screen is also spoken, one voice at a time: a narrator reads the opening, each subtitle line
    /// and the closing title; B, in a face close-up, says it is shaking and worried about the soup, the robot that
    /// takes the soup says so, and later B offers to clear dishes. Voices are VOICEVOX files in
    /// StreamingAssets/Robomori/voice/film (film.tsv, made by scripts/make-voice.ps1 -Film); without them the
    /// lines still show. The robots' automatic event voices are muted meanwhile so nothing talks over the film.
    /// </summary>
    public sealed partial class GuardianAutoFilm
    {
        private DeliveryTask soup;
        private string soupTo, subtitle, endCounts;
        private int stage, degradedAtStart, handoversAtStart;
        private float pulseAt = -1, reassignedAt = -1, subtitleAt, endAt = -1;
        private bool captionsWere = true;
        private GuardianSoundscape sound;
        private GUIStyle subtitleStyle, badgeStyle, effectStyle, endTitleStyle, endLineStyle, endCountStyle, creditStyle, bandStyle, robotWhoStyle,
            openTagStyle, openTitleStyle, openNoteStyle;
        private float styledFor = -1;

        // What is spoken: one line at a time, each waits for the previous one to finish. A robot's line names the
        // robot (who); the narrator's has no speaker and shows its subtitle (shown) while it is read.
        private readonly Dictionary<string, string> lineText = new Dictionary<string, string>();
        private readonly Dictionary<string, AudioClip> lineClip = new Dictionary<string, AudioClip>();
        private readonly Queue<(string key, string who, GuardianRobot face, string robot, string shown)> lines = new Queue<(string, string, GuardianRobot, string, string)>();
        private string talkWho, talkText, credits = "VOICEVOX:ずんだもん";
        private Color talkTint;  // the speaking robot's colour
        private float talkAt, talkEnds = -1, foodAt, openedAt = -1;
        private bool opening;  // the opening title card is up
        private AudioSource talkVoice;
        private GuardianFilmFaceShot faceShot;
        private bool Talking => talkEnds >= 0 || lines.Count > 0;

        private static readonly Color Amber = new Color(1f, .72f, .25f), Sky = new Color(.55f, .82f, 1f);

        private void InitializeMain()
        {
            // The agreed story keeps working robots working: no stop-and-ask on a condition change.
            sim.OperatorSupervisionEnabled = false;
            sim.Settings.RandomFaults = false;
            LoadLines(Path.Combine(Application.streamingAssetsPath, "Robomori", "voice", "film"));
            talkVoice = gameObject.AddComponent<AudioSource>();
            talkVoice.playOnAwake = false; talkVoice.spatialBlend = 0;
            faceShot = gameObject.AddComponent<GuardianFilmFaceShot>();
        }

        /// <summary>film.tsv (key, VOICEVOX speaker, text, speed) and the WAVs beside it; credits.txt names the voices used.</summary>
        private void LoadLines(string folder)
        {
            string list = Path.Combine(folder, "film.tsv");
            if (!File.Exists(list)) { Debug.LogWarning("[Robomori] No film dialogue: " + list); return; }
            foreach (string raw in File.ReadAllLines(list, System.Text.Encoding.UTF8))
            {
                if (raw.Length == 0 || raw.StartsWith("#")) continue;
                string[] cols = raw.Split('\t');
                if (cols.Length < 3) continue;
                lineText[cols[0]] = cols[2].Trim();
                string wav = Path.Combine(folder, cols[0] + ".wav");
                if (!File.Exists(wav)) continue;
                try { lineClip[cols[0]] = GuardianSoundscape.ReadWav(cols[0], File.ReadAllBytes(wav)); }
                catch (System.Exception e) { Debug.LogWarning("[Robomori] " + wav + ": " + e.Message); }
            }
            if (lineClip.Count < lineText.Count)
                Debug.LogWarning("[Robomori] Film dialogue without voice for " + (lineText.Count - lineClip.Count) + " line(s): run scripts/make-voice.ps1 -Film with VOICEVOX open");
            string names = Path.Combine(folder, "credits.txt");
            if (File.Exists(names)) { string c = File.ReadAllText(names, System.Text.Encoding.UTF8).Trim(); if (c.Length > 0) credits = c; }
        }

        private void Line(string key, string who, GuardianRobot face = null, string robot = null) =>
            lines.Enqueue((key, who, face, robot ?? (face != null ? face.RobotId : null), null));

        /// <summary>The narrator reads a line; shown becomes the subtitle as it starts (empty: a title card shows it).</summary>
        private void Narrate(string key, string shown) => lines.Enqueue((key, null, null, null, shown));

        /// <summary>Stops whatever is being said and drops what is waiting (the closing title must start on time).</summary>
        private void CutLines()
        {
            lines.Clear();
            if (talkVoice != null) talkVoice.Stop();
            talkEnds = -1; talkText = null;
            if (faceShot != null && faceShot.Target != null) { faceShot.Target = null; Cut(); }
        }

        /// <summary>Starts the next line when the previous one is over; a robot's line is filmed in a face close-up.</summary>
        private void RunLines(float t)
        {
            if (talkEnds >= 0 && t < talkEnds) return;
            if (talkEnds >= 0)
            {
                talkEnds = -1; talkText = null;
                // Cut back to the overhead view rather than sweeping out of the close-up.
                if (faceShot != null && faceShot.Target != null) { faceShot.Target = null; Cut(); }
            }
            while (lines.Count > 0)
            {
                var (key, who, face, robot, shown) = lines.Dequeue();
                if (!string.IsNullOrEmpty(shown)) Say(shown);
                if (!lineText.TryGetValue(key, out string text)) continue;
                lineClip.TryGetValue(key, out AudioClip clip);
                // Sound plays in film time (AudioRenderer), so a clip's length is its length on screen.
                float length = clip != null ? clip.length : .6f + text.Length * .13f;
                if (clip != null) { talkVoice.clip = clip; talkVoice.Play(); }
                talkEnds = t + length + .25f;
                if (who != null) { talkWho = who; talkText = text; talkAt = t; talkTint = robot != null ? GuardianRobotLivery.ColorOf(robot) : Amber; }
                if (face != null)
                {
                    if (faceShot != null) faceShot.Target = face;
                    // The simulation runs faster than the film, so the face is held for the line's length in game time.
                    var screen = face.GetComponent<GuardianRobotFace>();
                    if (screen != null) screen.Show(GuardianFaceExpression.Worried, (length + .25f) * Speed);
                }
                return;
            }
        }

        private float NormalValue => sim.Settings.WarningThreshold * .4f;
        private float WarningValue => (sim.Settings.WarningThreshold + sim.Settings.LimitedThreshold) * .5f;
        private float LimitedValue => Mathf.Lerp(sim.Settings.LimitedThreshold, 1, .5f);

        /// <summary>Before warming up: every robot healthy, a quiet screen.</summary>
        private void PrepareMain()
        {
            foreach (var r in sim.Robots) if (r != null && r.gameObject.activeInHierarchy) sim.SubmitVibration(r.RobotId, NormalValue);
            sound = GetComponent<GuardianSoundscape>();
            if (sound != null) { captionsWere = sound.ShowCaptions; sound.ShowCaptions = false; sound.EventVoicesMuted = true; }  // captions and counters bar; the dialogue speaks instead
            if (sim.CapabilityBoard != null) { sim.CapabilityBoard.Compact = true; sim.CapabilityBoard.Visible = true; }
            if (sim.RepairCrew != null) sim.RepairCrew.Paused = true;
        }

        /// <summary>Unrecorded: wait until B has taken a soup order (asked of B only until it takes it).</summary>
        private bool MainReady()
        {
            var b = Robot;
            if (b == null) return true;
            if (soup == null) soup = sim.QueueTask(DeliveryKind.Soup, 0, GuardianMenu.Soup, Target, sim.Diners != null ? sim.Diners.GuestPlace(0) : -1);
            if (soup == null) return true;
            if (b.CurrentTask == soup) { soup.RequestedRobotId = null; return true; }
            return soup.Phase == TaskPhase.Completed;
        }

        private void MainStep()
        {
            float t = FilmTime;
            var b = Robot;
            if (b == null) return;
            RunLines(t);
            switch (stage)
            {
                case 0:
                    // The opening card over the restaurant, read by the narrator; the camera settles on B underneath.
                    if (sound != null) { degradedAtStart = sound.DegradedJobs; handoversAtStart = sound.Handovers; }
                    if (sim.CapabilityBoard != null) sim.CapabilityBoard.Visible = false;
                    opening = true; Narrate("n_opening", ""); Follow(b, 6.5f); stage++;
                    break;
                case 1:
                    if (Talking) break;
                    opening = false; openedAt = t;
                    if (sim.CapabilityBoard != null) sim.CapabilityBoard.Visible = true;
                    Narrate("n_open", "3 台で配膳中"); stage++;
                    break;
                case 2:
                    if (Talking) break;
                    Line("b_shake", "ロボット " + Target, face: b); stage++;
                    break;
                case 3:
                    // B has said it is shaking: the vibration reading rises over the overview and the narrator says what follows.
                    if (Talking) break;
                    if (soup != null) soup.RequestedRobotId = null;
                    sim.SubmitVibration(Target, WarningValue); pulseAt = t; foodAt = t + .5f;
                    Narrate("n_shake", GuardianRobotLivery.Label(Target) + "の振動が上がった → スープは任せない"); Follow(b, 4.8f); stage++;
                    break;
                case 4:
                    if (t < foodAt) break;
                    GiveFood(b); stage++;
                    break;
                case 5:
                    var to = soup?.AssignedTo;
                    if (to != null && to.RobotId != Target)
                    {
                        soupTo = to.RobotId; reassignedAt = t;
                        Follow(to, 6f);
                        Line("a_take", "ロボット " + soupTo, robot: soupTo);
                        string key = lineText.ContainsKey("n_soup_" + soupTo) ? "n_soup_" + soupTo : "n_soup_any";
                        Narrate(key, "スープは " + GuardianRobotLivery.Label(soupTo) + "へ。B は止めずに料理を運ぶ"); stage++;
                    }
                    else if (t >= pulseAt + 5) { Narrate("n_soup_any", "スープは手の空いた機体へ。B は止めずに料理を運ぶ"); stage++; }
                    break;
                case 6:
                    if (t < (reassignedAt >= 0 ? reassignedAt + 3 : 0) || Talking) break;
                    Follow(b, 5.5f); stage++;
                    break;
                case 7:
                    // Once B has delivered its food (or at 18.5 s at the latest), B offers to do what it still can.
                    bool carryingFood = b.CurrentTask != null && b.CurrentTask.Kind == DeliveryKind.Food;
                    if (t < 15.5f || (carryingFood && t < 18.5f) || Talking) break;
                    Line("b_dishes", "ロボット " + Target, face: b); stage++;
                    break;
                case 8:
                    if (Talking) break;
                    foreach (var task in sim.Tasks) if (task.RequestedRobotId == Target && task.Phase != TaskPhase.Completed) task.RequestedRobotId = null;
                    sim.SubmitVibration(Target, LimitedValue); pulseAt = t;
                    Narrate("n_dishes", "揺れがさらに強くなった → 皿の片付けだけ任せる"); GiveDishes(); Follow(b, 5.5f); stage++;
                    break;
                case 9:
                    if ((Talking || t < 21) && t < 23.3f) break;
                    Wide(); Narrate("n_keep", "止めずに、できる仕事を続ける"); stage++;
                    break;
                case 10:
                    // The closing title and its line need about four seconds: it starts by 25.8 s whatever is still talking.
                    if ((Talking || t < 24) && t < Seconds - 4.2f) break;
                    CutLines(); endAt = t; Say(null);
                    if (sim.CapabilityBoard != null) sim.CapabilityBoard.Visible = false;
                    Narrate("n_title", ""); stage++;
                    break;
            }
        }

        private void GiveFood(GuardianRobot b)
        {
            if (b.CurrentTask != null && b.CurrentTask.Kind != DeliveryKind.Soup) return;
            if (sim.Tasks.Any(t => t.RequestedRobotId == Target && t.Phase != TaskPhase.Completed)) return;
            sim.QueueTask(DeliveryKind.Food, 1, GuardianMenu.CurryRice, Target, sim.Diners != null ? sim.Diners.GuestPlace(1) : -1);  // for a guest at that table
        }

        private void GiveDishes()
        {
            if (sim.Inventory == null) return;
            for (int table = 0; table < 3; table++)
                if (sim.Inventory.AvailableDirty(table).Any()) { sim.QueueTask(DeliveryKind.EmptyDishes, table, GuardianMenu.CurryRice, Target); return; }
        }

        private void Say(string text) { subtitle = text; subtitleAt = FilmTime; }

        private void Follow(GuardianRobot r, float distance)
        {
            var rig = sim.DemoInterface?.CameraRig;
            if (rig == null || r == null) return;
            // Pan over to the robot: Focus alone moves the pivot at once and swings the view round in one frame.
            Vector3 from = rig.Pivot; rig.Focus(r.transform); rig.Pivot = from; rig.Distance = distance;
        }

        private void Wide()
        {
            var rig = sim.DemoInterface?.CameraRig;
            if (rig == null) return;
            var robots = sim.Robots.Where(r => r != null && r.gameObject.activeInHierarchy).ToArray();
            Vector3 centre = robots.Length == 0 ? Vector3.zero : robots.Aggregate(Vector3.zero, (s, r) => s + r.transform.position) / robots.Length;
            rig.Focus(null); rig.Pivot = new Vector3(centre.x, .8f, centre.z); rig.Distance = 11; rig.Pitch = 56;
            Cut();
        }

        /// <summary>A cut: the rig goes straight to the view it is asked for instead of easing there.</summary>
        private void Cut() => sim.DemoInterface?.CameraRig?.Step(1);

        private void FinishMain()
        {
            if (sound != null) { sound.ShowCaptions = captionsWere; sound.EventVoicesMuted = false; }
            if (faceShot != null) faceShot.Target = null;
            if (sim.CapabilityBoard != null) { sim.CapabilityBoard.Compact = false; sim.CapabilityBoard.Visible = true; }
            if (sim.RepairCrew != null) sim.RepairCrew.Paused = false;
            sim.OperatorSupervisionEnabled = true;
        }

        private void DestroyMain()
        {
            if (sound != null) sound.EventVoicesMuted = false;
            foreach (var clip in lineClip.Values) if (clip != null) Destroy(clip);
            lineClip.Clear();
        }

        // ---- on screen ----

        private void MainStyles(float u)
        {
            if (Mathf.Approximately(styledFor, u)) return;
            styledFor = u;
            GUIStyle Make(int size, FontStyle weight, TextAnchor anchor, Color color)
            {
                var style = new GUIStyle(GUI.skin.label) { font = font, fontSize = Mathf.RoundToInt(size * u), fontStyle = weight, alignment = anchor, wordWrap = false, clipping = TextClipping.Overflow };
                style.normal.textColor = color;
                return style;
            }
            subtitleStyle = Make(46, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
            badgeStyle = Make(24, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
            effectStyle = Make(30, FontStyle.Bold, TextAnchor.MiddleCenter, Amber);
            endTitleStyle = Make(124, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
            endLineStyle = Make(50, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
            endCountStyle = Make(34, FontStyle.Normal, TextAnchor.MiddleCenter, Sky);
            creditStyle = Make(22, FontStyle.Normal, TextAnchor.LowerRight, new Color(1, 1, 1, .7f));
            bandStyle = Make(44, FontStyle.Bold, TextAnchor.MiddleLeft, Color.white);
            robotWhoStyle = Make(28, FontStyle.Bold, TextAnchor.MiddleLeft, Amber);
            openTagStyle = Make(30, FontStyle.Bold, TextAnchor.MiddleCenter, Sky);
            openTitleStyle = Make(66, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
            openNoteStyle = Make(28, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(.7f, .76f, .82f));
        }

        private bool OnScreen(GuardianRobot r, float lift, out Vector2 at)
        {
            at = default;
            var cam = Camera.main;
            if (r == null || cam == null) return false;
            Vector3 p = cam.WorldToScreenPoint(r.transform.position + Vector3.up * lift);
            if (p.z <= 0) return false;
            at = new Vector2(p.x, Screen.height - p.y);
            return true;
        }

        private void Fill(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }

        private void DrawMain()
        {
            float t = FilmTime, w = Screen.width, h = Screen.height, u = h / 1080f;
            MainStyles(u);
            Color tint = GUI.color;

            // B shakes: rings spreading from it, and a word above.
            if (pulseAt >= 0 && t - pulseAt < 1.6f && OnScreen(Robot, .6f, out var b))
            {
                for (int i = 0; i < 3; i++)
                {
                    float k = (t - pulseAt - i * .22f) / .9f;
                    if (k < 0 || k > 1) continue;
                    float size = Mathf.Lerp(60, 260, k) * u;
                    GUI.color = new Color(Amber.r, Amber.g, Amber.b, 1 - k);
                    GUI.DrawTexture(new Rect(b.x - size / 2, b.y - size / 2, size, size), ring);
                }
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01((1.6f - (t - pulseAt)) / .4f));
                GUI.Label(new Rect(b.x - 150 * u, b.y - 190 * u, 300 * u, 50 * u), "振動 ↑", effectStyle);
            }

            // The soup changes robots: a line from B to the robot that took it, with dishes travelling along it.
            if (reassignedAt >= 0 && t - reassignedAt < 3.8f && OnScreen(Robot, .9f, out var from) && OnScreen(RobotById(soupTo), .9f, out var to))
            {
                float fade = Mathf.Clamp01((3.8f - (t - reassignedAt)) / .5f) * Mathf.Clamp01((t - reassignedAt) / .25f);
                Vector2 d = to - from;
                Matrix4x4 saved = GUI.matrix;
                GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, from);
                GUI.color = new Color(Amber.r, Amber.g, Amber.b, .85f * fade);
                GUI.DrawTexture(new Rect(from.x, from.y - 4 * u, d.magnitude, 8 * u), Texture2D.whiteTexture);
                GUI.matrix = saved;
                for (int i = 0; i < 4; i++)
                {
                    float k = Mathf.Repeat((t - reassignedAt) * .9f + i / 4f, 1);
                    Vector2 p = Vector2.Lerp(from, to, k);
                    float size = 26 * u;
                    GUI.color = new Color(1, 1, 1, fade);
                    GUI.DrawTexture(new Rect(p.x - size / 2, p.y - size / 2, size, size), dot);
                }
                Vector2 mid = Vector2.Lerp(from, to, .5f);
                GUI.color = new Color(1, 1, 1, fade);
                GUI.Label(new Rect(mid.x - 200 * u, mid.y - 70 * u, 400 * u, 50 * u), "スープ → " + soupTo, effectStyle);
            }

            if (opening || (openedAt >= 0 && t - openedAt < .4f)) DrawOpening(t, w, h, u);
            else if (endAt < 0)
            {
                // Fast-forward mark, top left.
                var badge = new Rect(24 * u, 24 * u, 150 * u, 46 * u);
                Fill(badge, new Color(0, 0, 0, .55f));
                GUI.Label(badge, "▶▶ 2倍速", badgeStyle);

                // The bottom band: who is speaking and the line, or else the story's one line. Snaps in, then holds.
                bool talk = talkText != null;
                string text = talk ? talkText : subtitle;
                if (!string.IsNullOrEmpty(text))
                {
                    float k = Mathf.Clamp01((t - (talk ? talkAt : subtitleAt)) / .2f);
                    Color accent = talk ? talkTint : new Color(.56f, .64f, .7f);  // the narrator's line: grey
                    float bandH = 150 * u, top = h - bandH;
                    Fill(new Rect(0, top, w, bandH), new Color(.04f, .05f, .06f, .9f));
                    Fill(new Rect(0, top, w * k, 5 * u), accent);
                    GUI.color = new Color(1, 1, 1, k);
                    float textX = 96 * u;
                    if (talk)
                    {
                        // The robot's name in its body colour, beside a chip of that colour.
                        Fill(new Rect(64 * u, top + bandH / 2 - 18 * u, 14 * u, 36 * u), new Color(talkTint.r, talkTint.g, talkTint.b, k));
                        GUI.color = new Color(1, 1, 1, k);
                        robotWhoStyle.normal.textColor = talkTint;
                        GUI.Label(new Rect(96 * u, top, 300 * u, bandH), talkWho, robotWhoStyle);
                        textX = 330 * u;
                    }
                    GUI.Label(new Rect(textX, top + (1 - k) * 18 * u, w - textX - 96 * u, bandH), talk ? "「" + text + "」" : text, bandStyle);
                }
            }
            else
            {
                // End card: the one line, what it did in this film, and the voice credit.
                float k = Mathf.Clamp01((t - endAt) / .6f);
                Fill(new Rect(0, 0, w, h), new Color(.03f, .04f, .06f, .88f * k));
                GUI.color = new Color(1, 1, 1, k);
                GUI.Label(new Rect(0, h * .26f, w, 150 * u), "ロボもり", endTitleStyle);
                GUI.Label(new Rect(0, h * .26f + 170 * u, w, 70 * u), "不調でも、止めずに、できる仕事を任せ続ける", endLineStyle);
                if (endCounts == null)
                {
                    // Counted once, only what actually happened in this film.
                    int kept = sound != null ? sound.DegradedJobs - degradedAtStart : 0, moved = sound != null ? sound.Handovers - handoversAtStart : 0;
                    var parts = new System.Collections.Generic.List<string>();
                    if (kept > 0) parts.Add("止めずに続けた仕事 " + kept + " 件");
                    if (moved > 0) parts.Add("別の機体へ再割り振り " + moved + " 件");
                    endCounts = parts.Count == 0 ? "" : "この動画で　" + string.Join("　／　", parts);
                }
                if (endCounts.Length > 0) GUI.Label(new Rect(0, h * .26f + 270 * u, w, 50 * u), endCounts, endCountStyle);
                GUI.Label(new Rect(0, 0, w - 30 * u, h - 24 * u), "音声：" + credits, creditStyle);
            }
            GUI.color = tint;
        }

        /// <summary>The opening card: what this demo shows, over the restaurant, fading out once it has been read.</summary>
        private void DrawOpening(float t, float w, float h, float u)
        {
            float a = opening ? Mathf.Clamp01(t / .3f) : 1 - Mathf.Clamp01((t - openedAt) / .4f);
            float k = Mathf.SmoothStep(0, 1, Mathf.Clamp01(t / .7f));
            Fill(new Rect(0, 0, w, h), new Color(.03f, .04f, .06f, .95f * (opening ? 1 : a)));
            Fill(new Rect(w / 2 - 260 * u * k, h * .395f, 520 * u * k, 4 * u), new Color(Sky.r, Sky.g, Sky.b, a));
            GUI.color = new Color(1, 1, 1, a);
            float rise = (1 - k) * 24 * u;
            GUI.Label(new Rect(0, h * .30f + rise, w, 60 * u), "D E M O　S C E N A R I O　／　デモシナリオ", openTagStyle);
            GUI.Label(new Rect(0, h * .43f + rise, w, 100 * u), "配膳ロボットが、不調になったら？", openTitleStyle);
            GUI.Label(new Rect(0, h * .56f + rise, w, 50 * u), "ロボもり　｜　3D シミュレーション（想定動作）", openNoteStyle);
            GUI.color = Color.white;
        }
    }
}
