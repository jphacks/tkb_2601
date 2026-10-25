using System;
using System.Collections.Generic;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// Maintenance staff for the Hack Day screen. A robot that is held stopped (red) and left alone for a
    /// while, or one that keeps working with a fault, is repaired: the staff keep it stopped and repair it
    /// (a progress bar over the robot). Then it waits, still stopped, until a person confirms the
    /// inspection ("点検完了・再開" over the robot or on its card); only then does it drive home and take
    /// work again. Every step goes through the same human-decision checks as the decision card
    /// (GuardianInterventionExperience): this stands in for staff at the robot, not a shortcut around the
    /// person who decides. "修理を呼ぶ" on the card starts a repair at once. F6 (or -robomoriAutoRepair)
    /// lets repaired robots resume without the confirmation, for long unattended runs.
    /// Robots the Robomori link drives are not the staff's (see Serves).
    /// </summary>
    public sealed class GuardianRepairCrew : MonoBehaviour
    {
        public float StoppedDelay = 20, FaultDelay = 40, RepairSeconds = 10, GiveUpSeconds = 30;
        public KeyCode AutoResumeKey = KeyCode.F6;
        /// <summary>Resume after a repair without a person's confirmation (unattended runs only).</summary>
        public bool AutoResume;
        /// <summary>No new repairs start while set (the presentation mode drives the robots itself).</summary>
        public bool Paused;
        /// <summary>A repair started (robot, where): for the caption and sound.</summary>
        public static event Action<string, Vector3> RepairStarted;
        /// <summary>A repair finished and the robot waits for a person (robot, where).</summary>
        public static event Action<string, Vector3> RepairFinished;

        private enum Stage { Waiting, Repairing, Repaired, Inspected }
        private sealed class Job { public Stage Stage; public float Since, StartedAt, RetryAt, ConfirmedAt; public bool Requested, Confirmed, CheckOnly; public string Note; }
        private readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
        private readonly Dictionary<string, Rect> buttons = new Dictionary<string, Rect>();
        private GuardianSimulator sim;
        private GuardianNetworkBridge link;
        private GUIStyle style, noteStyle, buttonStyle;
        private Texture2D pixel;
        private Font font;

        public void Initialize(GuardianSimulator simulator)
        {
            sim = simulator;
            link = sim.GetComponent<GuardianNetworkBridge>();
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-robomoriAutoRepair") >= 0) AutoResume = true;
        }

        /// <summary>
        /// The staff look after this robot: not one the Robomori link drives. The relay owns that robot's condition
        /// and hold, and keeps sending them until KEY2, so a repair here would only stop a robot that may keep working
        /// and be undone by the next packet, or release a hold the link still keeps.
        /// </summary>
        public bool Serves(string robot) => link == null || !link.Drives(robot);

        /// <summary>Send the staff to this robot now (the card's "修理を呼ぶ").</summary>
        public bool RequestNow(string robot)
        {
            var r = Find(robot);
            if (r == null || !NeedsRepair(r, out _)) return false;
            if (!jobs.TryGetValue(robot, out var job)) jobs[robot] = job = new Job { Since = Time.unscaledTime };
            job.Requested = true;
            return true;
        }

        /// <summary>
        /// Keep the robot stopped and wait for a person, with nothing to repair (an input dropped out and came
        /// back): the same confirmation as after a repair.
        /// </summary>
        public bool CheckNow(string robot)
        {
            var r = Find(robot);
            var human = sim.HumanExperience;
            if (r == null || human == null || !human.Active || !Serves(robot)) return false;
            if (!jobs.TryGetValue(robot, out var job)) jobs[robot] = job = new Job { Since = Time.unscaledTime };
            if (job.Stage != Stage.Waiting) return job.Stage == Stage.Repaired;
            var a = Actor(r);
            if (a != null && (a.AwaitingDecision || !r.EmergencyStopped))
            {
                var kept = human.Submit(robot, GuardianHumanCommand.KeepStopped, a.Revision);
                if (!kept.Accepted) { jobs.Remove(robot); return false; }
            }
            job.Stage = Stage.Repaired; job.CheckOnly = true; job.RetryAt = 0;
            sim.Log("Repair: " + robot + " waits for a person to check it");
            return true;
        }

        public bool Repairing(string robot) => jobs.TryGetValue(robot, out var j) && j.Stage != Stage.Waiting;
        /// <summary>Repaired, still stopped, waiting for a person to confirm.</summary>
        public bool AwaitingConfirm(string robot) => jobs.TryGetValue(robot, out var j) && j.Stage == Stage.Repaired && !j.Confirmed;

        /// <summary>A person confirms the inspection: the robot drives home and takes work again.</summary>
        public bool Confirm(string robot)
        {
            if (!jobs.TryGetValue(robot, out var job) || job.Stage != Stage.Repaired || job.Confirmed) return false;
            job.Confirmed = true; job.ConfirmedAt = Time.unscaledTime; job.RetryAt = 0; job.Note = null;
            sim.Log("Repair: a person confirmed " + robot + "; resuming");
            return true;
        }

        /// <summary>The confirm buttons over the robots, so a click there does not select or drag.</summary>
        public bool IsUi(Vector2 mouse)
        {
            float s = Mathf.Max(1f, Screen.height / 720f);
            var p = new Vector2(mouse.x, Screen.height - mouse.y) / s;
            foreach (var b in buttons.Values) if (b.Contains(p)) return true;
            return false;
        }

        /// <summary>Where the robot's "点検完了・再開する" button was last drawn, in screen pixels from the top left.</summary>
        internal bool ConfirmButtonRect(string robot, out Rect screen)
        {
            float s = Mathf.Max(1f, Screen.height / 720f);
            bool found = buttons.TryGetValue(robot, out var b);
            screen = new Rect(b.x * s, b.y * s, b.width * s, b.height * s);
            return found;
        }

        private GuardianRobot Find(string id)
        {
            foreach (var r in sim.Robots) if (r != null && r.RobotId == id) return r;
            return null;
        }

        private GuardianHumanActor Actor(GuardianRobot r) =>
            sim.HumanExperienceActive && sim.HumanExperience.Actors.TryGetValue(r.RobotId, out var a) ? a : null;

        /// <summary>Held stopped (red), or working with a fault or an unsafe-motion flag; never a robot the link drives.</summary>
        private bool NeedsRepair(GuardianRobot r, out bool stopped)
        {
            // A waiting job is dropped and a repair under way is called off; the person decides on the card.
            if (!Serves(r.RobotId)) { stopped = false; return false; }
            var a = Actor(r);
            stopped = r.EmergencyStopped || (a != null && (a.HoldStopped || a.AwaitingDecision));
            return stopped || r.Condition != RobotCondition.Normal || (a != null && a.MobilityUnsafe);
        }

        private void Update()
        {
            if (sim == null || sim.DeveloperMode || sim.CustomPresentationActive || sim.Mode != GuardianSimulationMode.Physics) return;
            if (Input.GetKeyDown(AutoResumeKey) && GUIUtility.keyboardControl == 0)
            {
                AutoResume = !AutoResume;
                sim.Log("Repair: " + (AutoResume ? "repaired robots resume on their own (unattended run)" : "a person confirms each repair"));
            }
            float now = Time.unscaledTime;
            foreach (var r in sim.Robots)
            {
                if (r == null) continue;
                bool needs = NeedsRepair(r, out bool stopped);
                jobs.TryGetValue(r.RobotId, out var job);
                if (job == null)
                {
                    if (needs && !Paused) jobs[r.RobotId] = new Job { Since = now };
                    continue;
                }
                switch (job.Stage)
                {
                    case Stage.Waiting:
                        if (!needs) { jobs.Remove(r.RobotId); break; }  // a person decided on the card, or it recovered
                        if (Paused) { job.Since = now; job.Requested = false; break; }
                        // A stopped robot gets help after a while; a working one with a fault once it is idle.
                        if (job.Requested || (stopped && now - job.Since >= StoppedDelay) || (!stopped && now - job.Since >= FaultDelay && r.IsIdle))
                            Begin(r, job, now);
                        break;
                    case Stage.Repairing:
                        if (!stopped) { jobs.Remove(r.RobotId); break; }  // a person chose otherwise on the card
                        if (now - job.StartedAt < RepairSeconds) break;
                        job.Stage = Stage.Repaired; job.RetryAt = 0;
                        sim.Log("Repair: " + r.RobotId + " repaired; stays stopped until a person confirms");
                        RepairFinished?.Invoke(r.RobotId, r.transform.position);
                        break;
                    case Stage.Repaired:
                        if (!stopped) { jobs.Remove(r.RobotId); break; }
                        if ((job.Confirmed || AutoResume) && now >= job.RetryAt)
                        {
                            if (!job.Confirmed) job.ConfirmedAt = now;
                            job.Confirmed = true;
                            Inspect(r, job, now);
                        }
                        break;
                    case Stage.Inspected:
                        // Driving home after the inspection; work resumes once it is parked there.
                        if (now < job.RetryAt) break;
                        job.RetryAt = now + .5f;
                        var human = sim.HumanExperience;
                        if (human == null || !human.Active) { jobs.Remove(r.RobotId); break; }
                        if (r.IsIdle && !r.EmergencyStopped)
                        {
                            var result = human.Submit(r.RobotId, GuardianHumanCommand.ResumeWork);
                            if (result.Accepted || r.DispatchEnabled) { sim.Log("Repair: " + r.RobotId + " back to work"); jobs.Remove(r.RobotId); }
                        }
                        else if (now - job.ConfirmedAt > GiveUpSeconds) { sim.Log("Repair: " + r.RobotId + " did not get home; left to the operator"); jobs.Remove(r.RobotId); }
                        break;
                }
            }
        }

        private void Begin(GuardianRobot r, Job job, float now)
        {
            var human = sim.HumanExperience;
            if (human == null || !human.Active) { job.Since = now; return; }
            var a = Actor(r);
            // Keep it stopped while the staff work on it (this also answers an open alert).
            if (a != null && (a.AwaitingDecision || !r.EmergencyStopped))
            {
                var kept = human.Submit(r.RobotId, GuardianHumanCommand.KeepStopped, a.Revision);
                if (!kept.Accepted) { job.Since = now; job.Requested = false; return; }
            }
            job.Stage = Stage.Repairing; job.StartedAt = now; job.RetryAt = 0;
            sim.Log("Repair: staff inspecting and repairing " + r.RobotId);
            RepairStarted?.Invoke(r.RobotId, r.transform.position);
        }

        private void Inspect(GuardianRobot r, Job job, float now)
        {
            var human = sim.HumanExperience;
            if (human == null || !human.Active) { jobs.Remove(r.RobotId); return; }
            sim.TryAcknowledgeDemoEvidence(r.RobotId);  // clears a latched input hold once the input is good again
            var result = human.Submit(r.RobotId, GuardianHumanCommand.Inspect);
            if (result.Accepted)
            {
                job.Stage = Stage.Inspected; job.RetryAt = now + 1; job.Note = null;
                sim.Log("Repair: " + r.RobotId + " inspected; returning home");
                return;
            }
            job.RetryAt = now + .5f;  // still rolling to a stop, or a person is close: try again shortly
            if (now - job.ConfirmedAt > GiveUpSeconds)
            {
                // Back to waiting for the person, with the reason it could not resume.
                job.Confirmed = false; job.Note = "再開できませんでした（人や入力の確認が必要）";
                sim.Log("Repair: " + r.RobotId + " could not be confirmed (" + result.Reason + "); waiting for a person");
            }
        }

        // ---- over each robot: when help comes, the repair's progress, and the person's confirmation ----

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout) buttons.Clear();
            if (sim == null || jobs.Count == 0 || sim.DeveloperMode || sim.CustomPresentationActive || sim.Mode != GuardianSimulationMode.Physics) { buttons.Clear(); return; }
            var cam = Camera.main;
            if (cam == null) return;
            if (style == null)
            {
                font = GuardianFonts.Create(16);
                style = new GUIStyle(GUI.skin.label) { font = font, fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };
                style.normal.textColor = Color.white;
                noteStyle = new GUIStyle(style) { fontSize = 11, fontStyle = FontStyle.Normal };
                noteStyle.normal.textColor = new Color(1f, .8f, .5f);
                buttonStyle = new GUIStyle(GUI.skin.button) { font = font, fontSize = 14, fontStyle = FontStyle.Bold };
                pixel = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave }; pixel.SetPixel(0, 0, Color.white); pixel.Apply();
            }
            float s = Mathf.Max(1f, Screen.height / 720f), now = Time.unscaledTime;
            Matrix4x4 saved = GUI.matrix; Color tint = GUI.color;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            string confirm = null;
            foreach (var pair in jobs)
            {
                var r = Find(pair.Key);
                if (r == null) continue;
                var job = pair.Value;
                bool stopped = NeedsRepair(r, out bool red) && red;
                string text; float progress = -1; bool ask = false;
                if (job.Stage == Stage.Waiting)
                {
                    if (!stopped || Paused) continue;  // working with a fault: nothing to show until it is idle
                    float left = Mathf.Max(0, StoppedDelay - (now - job.Since));
                    text = job.Requested ? "修理スタッフが向かっています" : "修理スタッフ到着まで " + Mathf.CeilToInt(left) + " 秒";
                }
                else if (job.Stage == Stage.Repairing)
                { text = "修理中"; progress = Mathf.Clamp01((now - job.StartedAt) / RepairSeconds); }
                else if (job.Stage == Stage.Repaired)
                { ask = !job.Confirmed; text = job.Confirmed ? "確認済み・再開しています" : job.CheckOnly ? "止めたまま・人の確認待ち" : "修理済み・人の確認待ち"; }
                else text = "待機場所へ戻って再開";
                Vector3 p = cam.WorldToScreenPoint(r.transform.position + Vector3.up * 1.75f);
                if (p.z <= 0) continue;
                float height = progress >= 0 ? 38 : ask ? (job.Note != null ? 82 : 66) : 26;
                var box = new Rect(p.x / s - 105, (Screen.height - p.y) / s - 40 - (height - 26), 210, height);
                GUI.color = new Color(.08f, .09f, .11f, .9f); GUI.DrawTexture(box, pixel);
                GUI.color = job.Stage == Stage.Inspected || (job.Stage == Stage.Repaired && job.Confirmed) ? new Color(.35f, .85f, .45f)
                    : job.Stage == Stage.Repaired ? new Color(.6f, .8f, 1f) : new Color(1f, .7f, .25f);
                GUI.DrawTexture(new Rect(box.x, box.y, 4, box.height), pixel);
                GUI.color = Color.white;
                GUI.Label(new Rect(box.x + 6, box.y + 2, box.width - 10, 22), text, style);
                if (progress >= 0)
                {
                    var bar = new Rect(box.x + 12, box.y + 26, box.width - 24, 6);
                    GUI.color = new Color(1, 1, 1, .15f); GUI.DrawTexture(bar, pixel);
                    GUI.color = new Color(.35f, .85f, .45f); GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * progress, bar.height), pixel);
                    GUI.color = Color.white;
                }
                if (ask)
                {
                    var button = new Rect(box.x + 14, box.y + 28, box.width - 28, 32);
                    buttons[pair.Key] = button;
                    if (GUI.Button(button, "点検完了・再開する", buttonStyle)) confirm = pair.Key;
                    if (job.Note != null) GUI.Label(new Rect(box.x + 6, box.y + 62, box.width - 10, 18), job.Note, noteStyle);
                }
            }
            GUI.matrix = saved; GUI.color = tint;
            if (confirm != null) Confirm(confirm);
        }

        private void OnDestroy()
        {
            if (pixel != null) Destroy(pixel);
            GuardianFonts.Release(font);
        }
    }
}
