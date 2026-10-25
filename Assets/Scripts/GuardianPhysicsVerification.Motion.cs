using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private IEnumerator MotionDemoRegression()
        {
            int configured=sim.MotionSpeedMultiplier; float automationScale=Time.timeScale;
            var comparisons=new List<string>{"fleet,motion,input,seed,meal_completed_seconds,all_parked_seconds,peak_planar_mps,clock_scale,fixed_delta"};
            Check(!sim.TrySetMotionSpeed(3) && !sim.TrySetMotionSpeed(0),"physical speed selector rejects unsupported values");
            foreach(bool five in new[]{false,true}) foreach(int speed in new[]{1,2})
            {
                Reset(2026,false); clearanceCase="speed-comparison-"+speed;
                sim.SetReserveFleet(five); sim.TrySetMotionSpeed(speed);
                sim.AutomaticTasks=false; sim.Settings.RandomFaults=false; sim.Settings.MealSeconds=600;
                foreach(var robot in sim.Robots) sim.SetCondition(robot.RobotId,RobotCondition.Normal);
                if(five) { sim.Fleet.Deploy("D"); sim.Fleet.Deploy("E"); }
                var meal=sim.QueueTask(DeliveryKind.Soup,0);
                sim.QueueTask(DeliveryKind.Food,1,GuardianMenu.CurryRice); sim.QueueTask(DeliveryKind.EmptyDishes,2);
                yield return Wait(Drained,600,"identical three-job input drains at motion="+speed+" fleet="+sim.Robots.Count);
                if(failed) yield break;
                float peak=sim.Robots.Max(r=>r.PhysicsDriver.PeakPlanarSpeed);
                Check(peak>sim.Settings.MaximumSpeed*speed*.8f && peak<sim.Settings.MaximumSpeed*speed+.15f,
                    "measured cruise envelope at motion="+speed+" peak="+peak.ToString("F3"));
                comparisons.Add(string.Join(",",new[]{sim.Robots.Count.ToString(),speed.ToString(),"SoupT1+FoodT2+ReturnT3", "2026",
                    meal.CompletedAt.ToString("F4",CultureInfo.InvariantCulture),sim.DemoClock.ToString("F4",CultureInfo.InvariantCulture),
                    peak.ToString("F4",CultureInfo.InvariantCulture),Time.timeScale.ToString(CultureInfo.InvariantCulture),Time.fixedDeltaTime.ToString(CultureInfo.InvariantCulture)}));
            }
            File.WriteAllLines(Path.Combine(output,"motion-comparison.csv"),comparisons);

            Reset(2026,false); sim.TrySetMotionSpeed(2); sim.Settings.MealSeconds=3; OnlyActorA();
            clearanceCase="speed-meal-clock";
            var timed=sim.QueueTask(DeliveryKind.Food,0);
            yield return Wait(()=>timed.Phase==TaskPhase.Completed,180,"real delivery starts the configured meal timer");
            if(failed) yield break;
            var plate=sim.Inventory.Items[timed.DishIds.Single()];
            Check(Mathf.Abs(plate.DirtyAt-timed.CompletedAt-3)<.001f,"meal duration is three clock seconds at 2x motion");
            Time.timeScale=1; float wallStart=Time.realtimeSinceStartup,clockStart=sim.DemoClock,dt=Time.fixedDeltaTime;
            sim.TrySetMotionSpeed(1); yield return new WaitForSeconds(1); sim.TrySetMotionSpeed(2);
            yield return Wait(()=>plate.Dirty,5,"meal becomes dirty on the ordinary world clock across speed switches");
            float elapsed=Time.realtimeSinceStartup-wallStart;
            Check(Time.timeScale==1 && Time.fixedDeltaTime==dt && elapsed>2.7f && elapsed<3.6f
                && sim.DemoClock-clockStart>2.7f && sim.DemoClock-clockStart<3.2f,
                "1x/2x motion changes neither fixed step nor meal/world clock; wall="+elapsed.ToString("F3"));
            Time.timeScale=automationScale;
            yield return Wait(Drained,240,"meal-clock test physically parks after timer check");
            if(failed) yield break;

            foreach(bool five in new[]{false,true})
            {
                Reset(2026,true);
                sim.Settings.MinimumTaskInterval=sim.Settings.MaximumTaskInterval=1000;
                sim.Settings.MinimumFaultInterval=sim.Settings.MaximumFaultInterval=1000;
                sim.SetReserveFleet(five); sim.TrySetMotionSpeed(2);
                clearanceCase="live-decision-switch-"+(five?5:3);
                foreach(var robot in sim.Robots) sim.SetCondition(robot.RobotId,RobotCondition.Normal);
                var b=sim.Robots.Single(r=>r.RobotId=="B");
                var task=sim.QueueTask(DeliveryKind.Soup,0,GuardianMenu.Soup,"B");
                yield return Wait(()=>b.Carrying && b.PhysicsDriver.Body.linearVelocity.magnitude>2.6f,240,
                    "2x actor really carries soup at fast translation before live intervention");
                if(failed) yield break;
                var body=b.PhysicsDriver.Body; Vector3 velocity=body.linearVelocity,position=body.position;
                float beforeClock=sim.DemoClock,mealSeconds=sim.Settings.MealSeconds; dt=Time.fixedDeltaTime;
                sim.TrySetMotionSpeed(1);
                Check(body.linearVelocity==velocity && body.position==position && Time.timeScale==automationScale
                    && sim.DemoClock==beforeClock && Time.fixedDeltaTime==dt && sim.Settings.MealSeconds==mealSeconds,
                    "2x -> 1x switch changes no velocity/pose/clock/meal timer instantaneously");
                yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
                var driver=b.PhysicsDriver;
                Check(driver.LastBrakingAcceleration==sim.Settings.MotorAcceleration
                    && driver.LastStoppingDistance>=driver.LastBrakingSpeed*driver.LastBrakingSpeed/(2*driver.LastBrakingAcceleration)+.119f
                    && body.collisionDetectionMode==CollisionDetectionMode.ContinuousDynamic,
                    "downshift braking uses actual residual speed, lower acceleration, fixed-step allowance and CCD");
                sim.TrySetMotionSpeed(2);
                string id=task.DishIds.Single(); int instance=sim.Inventory.Items[id].GetInstanceID();
                int tasks=sim.Tasks.Count; float preservedClock=sim.DemoClock; position=body.position;
                bool oldAuto=sim.AutomaticTasks,oldFaults=sim.Settings.RandomFaults;
                sim.AutomaticTasks=true; sim.Settings.RandomFaults=true;
                oldAuto=true; oldFaults=true;
                sim.OperatorSupervisionEnabled=true; sim.SubmitVibration("B",.5f);
                var exp=sim.HumanExperience; var state=exp.Actors["B"];
                Check(exp.Live && exp.Active && exp.SelectedActor=="B" && sim.Tasks.Count==tasks && sim.Tasks.Contains(task)
                    && sim.DemoClock==preservedClock && body.position==position && sim.Settings.MealSeconds==mealSeconds
                    && sim.AutomaticTasks==oldAuto && sim.Settings.RandomFaults==oldFaults
                    && sim.Inventory.Items[id].GetInstanceID()==instance && sim.Inventory.Items[id].OwnerKey=="B",
                    "lamp change opens live decision without resetting positions, tasks, dishes, timers or automatic settings");
                Check(state.PreviousCondition==RobotCondition.Normal && state.AlertCondition==RobotCondition.Warning
                    && state.AlertVibration==.5f && state.AlertCause.Contains("0.50") && exp.Choices("B").Contains("液体")
                    && exp.RobotResponse("B").Contains("停止") && b.Condition==state.AlertCondition,
                    "lamp, textual transition/cause, capability choices and physical response describe the same alert");
                Time.timeScale=1; wallStart=Time.realtimeSinceStartup; beforeClock=sim.DemoClock;
                position=body.position; sim.TrySetMotionSpeed(1); yield return new WaitForSeconds(1);
                sim.TrySetMotionSpeed(2); yield return new WaitForSeconds(2);
                Check(Time.timeScale==1 && Time.fixedDeltaTime==dt && Time.realtimeSinceStartup-wallStart>2.8f
                    && sim.DemoClock-beforeClock>2.8f && sim.DemoClock-beforeClock<3.2f
                    && (new Vector2(body.position.x-position.x,body.position.z-position.z)).magnitude<.005f
                    && b.EmergencyStopped && state.AwaitingDecision && state.HeldTask==task,
                    "unanswered human decision keeps cargo stopped for ordinary real/clock seconds across 1x/2x switches");
                Check(sim.AutomaticTasks && sim.Settings.RandomFaults,"live supervision preserves enabled automatic settings while awaiting a reply");
                sim.AutomaticTasks=false; sim.Settings.RandomFaults=false;
                Time.timeScale=automationScale;
                Check(!exp.Submit("B",GuardianHumanCommand.ContinueDelivery).Accepted && state.DecisionFeedback.Contains("拒否"),
                    "live yellow liquid instruction rejects with linked textual feedback");
                Check(exp.Submit("B",GuardianHumanCommand.Transfer).Accepted && state.DecisionFeedback.Contains("受付"),
                    "live human transfer accepts the held task");
                yield return Wait(Drained,900,"live transfer physically returns the same cargo before healthy delivery and clearing");
                if(failed) yield break;
                Check(task.AssignedTo!=b && sim.Inventory.Items[id].GetInstanceID()==instance
                    && sim.Inventory.Events.Any(e=>e.Id==id && e.From==GuardianDishOwner.Robot && e.FromKey=="B" && e.To==GuardianDishOwner.Kitchen),
                    "live handoff preserves the real source and exact meal instance");
                sim.SetEmergencyStop("B",true); sim.SetCondition("B",RobotCondition.Normal); sim.TrySetMotionSpeed(1); sim.TrySetMotionSpeed(2);
                Check(exp.Actors["B"].MobilityUnsafe && b.EmergencyStopped && exp.Choices("B").Contains("危険")
                    && !exp.Submit("B",GuardianHumanCommand.ClearDishes).Accepted,
                    "green lamp and motion changes cannot clear an unsafe-motion latch");
                SaveHumanTrace(exp,"live-switch-"+(five?5:3)); SaveServeTrace("live-switch-"+(five?5:3));
                exp.Exit(); sim.OperatorSupervisionEnabled=false;
            }
            sim.TrySetMotionSpeed(configured); Time.timeScale=automationScale;
            report.Add("MOTION_DEMO_OK: physical speed only; identical input comparisons, ordinary clocks, bounded switches and live lamp decisions; no GUI/pixel acceptance");
        }
    }
}
