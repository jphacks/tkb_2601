using System.Collections;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private IEnumerator HumanFreeHandoffRegression()
        {
            foreach(bool five in new[]{false,true}) foreach(bool loaded in new[]{false,true})
            foreach(var command in new[]{GuardianHumanCommand.Transfer,GuardianHumanCommand.KeepStopped})
            {
                string label="unalerted-"+command+"-"+(loaded?"loaded":"unpicked")+"-"+(five?5:3);
                Reset(2026,true); clearanceCase=label;
                var exp=sim.EnterHumanExperience(false,five); exp.SelectedTable=0;
                var a=sim.Robots.Single(r=>r.RobotId=="A");
                var queued=exp.Submit("A",GuardianHumanCommand.ClearDishes);
                Check(queued.Accepted && a.CurrentTask!=null && a.CurrentTask.RequestedRobotId=="A",
                    label+": explicit clearing instruction targets A");
                if(failed) yield break;
                var task=a.CurrentTask; string dish=task.DishIds.Single();
                int instance=sim.Inventory.Items[dish].GetInstanceID();
                if(loaded)
                {
                    yield return Wait(()=>a.Carrying && Vector3.Distance(a.PhysicsDriver.Body.position,task.Pickup)>2,240,
                        label+": cargo physically leaves its table");
                    if(failed) yield break;
                }
                Check(exp.Actors["A"].HeldTask==null && !exp.Actors["A"].AwaitingDecision
                    && a.Carrying==loaded,label+": current-task fallback is exercised without an alert");
                Check(exp.Submit("A",command).Accepted,label+": first instruction accepted");
                int version=task.AssignmentVersion;
                for(int repeat=0;repeat<3;repeat++) exp.Submit("A",command);
                Check(task.RequestedRobotId==null && string.IsNullOrEmpty(task.OperatorHoldReason)
                    && sim.Tasks.Count==1 && task.AssignmentVersion==version,
                    label+": repeated commands neither retain actor pin nor duplicate/restart the job");
                if(failed) yield break;
                if(command==GuardianHumanCommand.KeepStopped)
                {
                    yield return new WaitForSeconds(2);
                    Check(a.EmergencyStopped && !a.DispatchEnabled && !exp.Actors["A"].MobilityUnsafe,
                        label+": deliberate safe stop remains enforced");
                    if(loaded)
                    {
                        Check(a.Carrying && sim.Inventory.Items[dish].Owner==GuardianDishOwner.Robot
                            && sim.Inventory.Items[dish].OwnerKey=="A" && task.Phase!=TaskPhase.Completed,
                            label+": stop preserves real cargo and forbids early completion");
                        Check(exp.Submit("A",GuardianHumanCommand.Inspect).Accepted,
                            label+": explicit simulated inspection allows source return");
                    }
                }
                yield return Wait(Drained,900,label+": healthy replacement finishes and all reservations drain");
                if(failed) yield break;
                var item=sim.Inventory.Items[dish];
                var moves=sim.Inventory.Events.Where(e=>e.Action=="move" && e.Id==dish).ToArray();
                Check(task.AssignedTo!=a && task.AssignmentVersion>=2 && item.GetInstanceID()==instance
                    && item.Owner==GuardianDishOwner.ReturnCounter && !item.gameObject.activeInHierarchy,
                    label+": same instance completes under another actor and disappears at the counter");
                Check(moves.Count(e=>e.From==GuardianDishOwner.Table && e.To==GuardianDishOwner.Robot)==(loaded?2:1)
                    && moves.Count(e=>e.From==GuardianDishOwner.Robot && e.To==GuardianDishOwner.Table)==(loaded?1:0)
                    && moves.Count(e=>e.From==GuardianDishOwner.Robot && e.To==GuardianDishOwner.ReturnCounter)==1,
                    label+": physical source return precedes replacement pickup exactly when required");
                SaveHumanTrace(exp,label); SaveServeTrace(label); exp.Exit();
            }
        }

        private IEnumerator HumanThreeActorRecoveryRegression()
        {
            Reset(2026,true); clearanceCase="human-three-recovery";
            var exp=sim.EnterHumanExperience(false,false);
            for(int table=0;table<3;table++)
            {
                exp.SelectedTable=table;
                Check(exp.Submit(sim.Robots[table].RobotId,GuardianHumanCommand.ClearDishes).Accepted,
                    "recovery fixture orders all three initial plates through real clearing jobs");
            }
            yield return Wait(Drained,900,"all initial plates physically reach the counter before recovery exercise");
            if(failed) yield break;
            Check(!Enumerable.Range(0,3).Any(t=>sim.Inventory.AvailableDirty(t).Any()),
                "no initial empty plate is available as an accidental redispatch path");
            foreach(var robot in sim.Robots) exp.InjectEvidence(robot.RobotId,RobotCondition.Limited,true);
            yield return new WaitForSeconds(2);
            foreach(var robot in sim.Robots)
            {
                exp.InjectEvidence(robot.RobotId,RobotCondition.Normal,false);
                Check(!exp.Submit(robot.RobotId,GuardianHumanCommand.ResumeWork).Accepted
                    && robot.EmergencyStopped && exp.Actors[robot.RobotId].MobilityUnsafe,
                    "green evidence cannot authorize work while unsafe-motion latch remains "+robot.RobotId);
            }
            exp.SelectedTable=0; var requested=exp.OrderMeal(false);
            Check(requested.Accepted,"new food is accepted as pending while all three actors are unavailable");
            var meal=sim.Tasks.Single(t=>t.Id==requested.TaskId); string dish=meal.DishIds.Single();
            int instance=sim.Inventory.Items[dish].GetInstanceID();
            foreach(var robot in sim.Robots)
                Check(exp.Submit(robot.RobotId,GuardianHumanCommand.Inspect).Accepted,
                    "explicit stopped simulated inspection recovers "+robot.RobotId);
            yield return Wait(()=>sim.Robots.All(r=>r.IsIdle),180,"all inspected actors reach their physical waiting bays");
            yield return new WaitForSeconds(2);
            Check(meal.Phase==TaskPhase.Pending && sim.Robots.All(r=>!r.DispatchEnabled && !r.EmergencyStopped
                && r.Condition==RobotCondition.Normal),"inspection alone does not dispatch a recovered actor");
            Check(!exp.Submit("A",GuardianHumanCommand.ClearDishes).Accepted
                && !exp.Submit("A",GuardianHumanCommand.ContinueDelivery).Accepted
                && !exp.Submit("A",GuardianHumanCommand.DeploySpare).Accepted,
                "empty table, no held meal and no spare cannot substitute for explicit work resumption");
            Check(exp.Submit("A",GuardianHumanCommand.ResumeWork).Accepted,
                "inspected normal parked A explicitly resumes work reception in three-actor mode");
            for(int repeat=0;repeat<3;repeat++) exp.Submit("A",GuardianHumanCommand.ResumeWork);
            Check(sim.Tasks.Count==4 && sim.Robots.Where(r=>r.RobotId!="A").All(r=>!r.DispatchEnabled),
                "repeated resumption adds no task and leaves other inspected actors waiting");
            yield return Wait(Drained,600,"new meal physically completes after explicit recovery and resumption");
            if(failed) yield break;
            Check(meal.AssignedTo.RobotId=="A" && sim.Inventory.Items[dish].GetInstanceID()==instance
                && sim.Inventory.Items[dish].Owner==GuardianDishOwner.Table,
                "recovered A delivers the same newly requested meal instance without scenario reset");
            SaveHumanTrace(exp,"three-recovered-orders"); SaveServeTrace("three-recovered-orders"); exp.Exit();
        }
    }
}
