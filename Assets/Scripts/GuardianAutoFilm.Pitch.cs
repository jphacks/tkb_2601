using System;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// The 40-second presentation-mode film: the presentation mode on a fixed timeline, and an on-screen pointer
    /// that makes the person's clicks (robot B, the card's choice, "点検完了・再開する") through the same card and
    /// repair-crew calls as a real click.
    /// </summary>
    public sealed partial class GuardianAutoFilm
    {
        private sealed class Cue { public float At; public Action Run; }
        private sealed class Pointer { public float From, Click, To; public Func<Rect?> Where; }
        private Cue[] cues;
        private Pointer[] pointers;
        private int cue;
        private GuardianDemoChoice choice = GuardianDemoChoice.ContinueCapable;
        private Vector2 cursor = new Vector2(-1, -1), glideFrom;
        private Pointer gliding;

        private void InitializePitch()
        {
            cues = new[]
            {
                new Cue { At = 0, Run = Opening },
                new Cue { At = 4.5f, Run = GiveWork },
                new Cue { At = 6, Run = NextScene },                       // ① vibration rises
                new Cue { At = 8.5f, Run = () => sim.DemoInterface?.Select(Target) },
                new Cue { At = 9.8f, Run = PickChoice },
                new Cue { At = 11.5f, Run = Choose },
                new Cue { At = 13.5f, Run = () => sim.DemoInterface?.Close() },
                new Cue { At = 15, Run = NextScene },                      // ② forged "normal"
                new Cue { At = 21, Run = NextScene },                      // ③ sensor silent
                new Cue { At = 27, Run = NextScene },                      // ④ back, still waiting for a person
                new Cue { At = 30.5f, Run = () => sim.RepairCrew?.Confirm(Target) },
                new Cue { At = 34.5f, Run = NextScene },                   // summary
            };
            pointers = new[]
            {
                new Pointer { From = 7.6f, Click = 8.5f, To = 9.8f, Where = RobotRect },
                new Pointer { From = 9.8f, Click = 11.5f, To = 12.4f, Where = () => sim.DemoInterface != null ? sim.DemoInterface.ChoiceScreenRect(choice) : (Rect?)null },
                new Pointer { From = 29.6f, Click = 30.5f, To = 31.6f, Where = () => sim.RepairCrew != null && sim.RepairCrew.ConfirmButtonRect(Target, out var r) ? r : (Rect?)null },
            };
        }

        private void RunCues()
        {
            float t = FilmTime;
            while (cue < cues.Length && t >= cues[cue].At)
            {
                try { cues[cue].Run(); }
                catch (Exception e) { Debug.LogException(e); }
                cue++;
            }
        }

        private void Opening()
        {
            sim.PitchMode.Begin();
            GiveWork();
        }

        // B gets a dish of food to carry if it has nothing, so scene ① shows it keep working with what it still may carry.
        private void GiveWork()
        {
            var b = Robot;
            if (b != null && b.CurrentTask == null && !sim.Tasks.Any(t => t.RequestedRobotId == Target && t.Phase != TaskPhase.Completed))
                sim.QueueTask(DeliveryKind.Food, 1, GuardianMenu.CurryRice, Target);
        }

        private void NextScene() => sim.PitchMode.Advance();

        private void PickChoice()
        {
            var b = Robot;
            GuardianHumanActor a = null;
            if (b != null && sim.HumanExperienceActive) sim.HumanExperience.Actors.TryGetValue(Target, out a);
            var task = b?.CurrentTask ?? a?.HeldTask;
            // What a person would pick, and the card accepts: soup may not stay on a robot that shakes, so hand it
            // over; other work keeps going; with nothing in hand, collect dishes if there are any, else stay stopped.
            if (task != null) choice = GuardianCapabilityPolicy.Allows(b.Condition, task.Kind) ? GuardianDemoChoice.ContinueCapable : GuardianDemoChoice.Transfer;
            else choice = sim.Inventory != null && Enumerable.Range(0, 3).Any(t => sim.Inventory.AvailableDirty(t).Any()) ? GuardianDemoChoice.ContinueCapable : GuardianDemoChoice.KeepStopped;
        }

        private void Choose()
        {
            var ui = sim.DemoInterface;
            if (ui == null || ui.SelectedId != Target) return;
            ui.RefreshState();
            ui.Choose(Target, ui.SelectionVersion, ui.StateVersion, choice);
        }

        private Rect? RobotRect()
        {
            var b = Robot; var cam = Camera.main;
            if (b == null || cam == null) return null;
            Vector3 p = cam.WorldToScreenPoint(b.transform.position + Vector3.up * .5f);
            if (p.z <= 0) return null;
            return new Rect(p.x - 20, Screen.height - p.y - 20, 40, 40);
        }

        private void DrawPointer()
        {
            float t = FilmTime;
            Pointer active = null;
            foreach (var p in pointers) if (t >= p.From && t <= p.To) { active = p; break; }
            if (active == null) return;
            Rect? where = active.Where();
            if (where == null) return;
            Vector2 target = where.Value.center;
            if (gliding != active) { gliding = active; glideFrom = cursor.x < 0 ? target + new Vector2(160, 120) : cursor; }
            if (t < active.Click)
            {
                // Glide to the button, arriving a moment before the click.
                float k = Mathf.Clamp01((t - active.From) / Mathf.Max(.1f, active.Click - .25f - active.From));
                cursor = Vector2.Lerp(glideFrom, target, k * k * (3 - 2 * k));
            }
            else
            {
                cursor = target;
                float since = t - active.Click, size = Mathf.Lerp(24, 70, Mathf.Clamp01(since / .45f)) * Mathf.Max(1, Screen.height / 720f);
                GUI.color = new Color(1f, .85f, .3f, Mathf.Clamp01(1 - since / .6f));
                GUI.DrawTexture(new Rect(cursor.x - size / 2, cursor.y - size / 2, size, size), ring);
            }
            float scale = Mathf.Max(1, Screen.height / 720f);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cursor.x, cursor.y, arrow.width * scale, arrow.height * scale), arrow);
        }
    }
}
