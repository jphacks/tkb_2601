using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private void SaveHeldState(string label,DeliveryTask task,string[] ids)
        {
            System.Func<string,string> csv=s=>"\""+(s??"").Replace("\"","\"\"")+"\"";
            var rows=ids.Select(id=>
            {
                var item=sim.Inventory.Items[id];
                return string.Join(",",new[]{sim.DemoClock.ToString("F4",CultureInfo.InvariantCulture),task.Id,task.Phase.ToString(),
                    task.AssignedTo?.RobotId,task.OperatorHoldReason,task.RequestedRobotId,task.FixedDishBatch.ToString(),
                    id,item.GetInstanceID().ToString(),item.Owner.ToString(),item.OwnerKey,item.ReservedBy,
                    item.transform.parent.name,item.gameObject.activeInHierarchy.ToString()}.Select(csv));
            });
            File.WriteAllLines(Path.Combine(output,label+"-held-state.csv"),new[]{
                "sim_seconds,task,phase,actor,operator_hold,requested_actor,fixed_batch,dish,instance,owner,owner_key,reserved_by,parent,active"}.Concat(rows));
        }

        private IEnumerator HumanHeldCollectionRegression()
        {
            foreach(bool five in new[]{false,true})
            {
                foreach(string kind in new[]{"ordinary","ordinary-overflow","fixed"})
                {
                    string label="held-"+kind+"-"+(five?5:3);
                    Reset(2026,true); clearanceCase=label;
                    var exp=sim.EnterHumanExperience(false,five); exp.SelectedTable=0; sim.Settings.MealSeconds=600;
                    var a=sim.Robots.Single(r=>r.RobotId=="A");
                    int extras=kind=="ordinary-overflow"?5:kind=="fixed"?2:0;
                    for(int i=0;i<extras;i++) TableFixture(0,true);
                    DeliveryTask held;
                    if(kind=="fixed")
                    {
                        sim.SetDispatchEnabled(a,false);
                        held=sim.QueueTask(DeliveryKind.EmptyDishes,0,GuardianMenu.CurryRice,"A");
                        // Use the same public reservation API as same-visit
                        // creation, before ordinary physical assignment.
                        Check(sim.Inventory.ReserveFixedBatch(held,sim.Inventory.AvailableDirty(0).Select(i=>i.Id).ToArray()),
                            label+": materialize a fixed batch with real table identities");
                        sim.SetDispatchEnabled(a,true);
                    }
                    else
                    {
                        Check(exp.Submit("A",GuardianHumanCommand.ClearDishes).Accepted,label+": ordinary clearing accepted");
                        held=a.CurrentTask;
                    }
                    string[] ids=held.DishIds.ToArray();
                    var instances=ids.ToDictionary(id=>id,id=>sim.Inventory.Items[id].GetInstanceID());
                    Check(!a.Carrying && held.AssignedTo==a && held.FixedDishBatch==(kind=="fixed")
                        && ids.Length==(kind=="ordinary-overflow"?4:extras+1),label+": unpicked ordinary/fixed paths remain distinct");
                    exp.InjectEvidence("A",RobotCondition.Warning,false);
                    Check(held.Phase==TaskPhase.Pending && held.AssignedTo==null && held.DishIds.SequenceEqual(ids)
                        && ids.All(id=>sim.Inventory.Items[id].ReservedBy==held.Id),label+": human hold preserves exact source reservations");
                    if(failed) yield break;
                    var meal=sim.QueueTask(DeliveryKind.Food,0,GuardianMenu.CurryRice,"B");
                    DeliveryTask separate=null;
                    if(kind=="ordinary-overflow")
                    {
                        separate=sim.QueueTask(DeliveryKind.EmptyDishes,0,GuardianMenu.CurryRice,"C");
                        Check(separate!=null && separate!=held,label+": independent request can collect only the two unheld plates");
                    }
                    yield return Wait(()=>meal.Phase==TaskPhase.Completed && sim.Robots.Where(r=>r!=a).All(r=>r.IsIdle),360,
                        label+": healthy delivery/independent work completes with no human answer to A");
                    if(failed) yield break;
                    Check(a.EmergencyStopped && exp.Actors["A"].AwaitingDecision && held.Phase==TaskPhase.Pending
                        && held.AssignedTo==null && held.RequestedRobotId=="A" && !string.IsNullOrEmpty(held.OperatorHoldReason)
                        && ids.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Table
                            && sim.Inventory.Items[id].ReservedBy==held.Id && sim.Inventory.Items[id].GetInstanceID()==instances[id])
                        && !sim.Inventory.Events.Any(e=>ids.Contains(e.Id) && e.From==GuardianDishOwner.Table && e.To==GuardianDishOwner.Robot),
                        label+": same visit and new task cannot move any held identity or change its assignee");
                    Check(meal.SameVisitReturnId==null && (separate==null || (separate.Phase==TaskPhase.Completed
                        && !separate.DishIds.Intersect(ids).Any())),label+": target pins are respected without blocking unrelated plates");
                    SaveHeldState(label+"-unanswered",held,ids);
                    int taskCount=sim.Tasks.Count;
                    var resumed=exp.Submit("A",GuardianHumanCommand.ClearDishes);
                    Check(resumed.Accepted && resumed.TaskId==held.Id && sim.Tasks.Count==taskCount
                        && string.IsNullOrEmpty(held.OperatorHoldReason) && held.RequestedRobotId=="A",
                        label+": human approval resumes the same ordinary/fixed task without a duplicate");
                    yield return Wait(Drained,900,label+": approved collector completes and releases every claim");
                    if(failed) yield break;
                    Check(held.AssignedTo==a && held.FixedDishBatch==(kind=="fixed") && held.DishIds.SequenceEqual(ids)
                        && ids.All(id=>sim.Inventory.Items[id].GetInstanceID()==instances[id]
                            && sim.Inventory.Items[id].Owner==GuardianDishOwner.ReturnCounter && !sim.Inventory.Items[id].gameObject.activeInHierarchy)
                        && sim.Inventory.Items[meal.DishIds.Single()].Owner==GuardianDishOwner.Table,
                        label+": exact held batch returns once after approval while the new meal stays served");
                    SaveHumanTrace(exp,label); SaveServeTrace(label); exp.Exit();
                }

                string pinnedLabel="pinned-unassigned-"+(five?5:3);
                Reset(2026,true); clearanceCase=pinnedLabel;
                var free=sim.EnterHumanExperience(false,five); sim.Settings.MealSeconds=600;
                var selected=sim.Robots.Single(r=>r.RobotId=="A"); sim.SetDispatchEnabled(selected,false);
                string plate=sim.Inventory.AvailableDirty(0).Single().Id;
                var target=sim.QueueTask(DeliveryKind.EmptyDishes,0,GuardianMenu.CurryRice,"A");
                var served=sim.QueueTask(DeliveryKind.Food,0,GuardianMenu.CurryRice,"B");
                var duplicate=sim.QueueTask(DeliveryKind.EmptyDishes,0,GuardianMenu.CurryRice,"C");
                Check(target.Phase==TaskPhase.Pending && target.DishIds.Count==0 && string.IsNullOrEmpty(target.OperatorHoldReason)
                    && duplicate==target,pinnedLabel+": pending target-only request is coalesced without overriding its pin");
                yield return Wait(()=>served.Phase==TaskPhase.Completed && sim.Robots.Where(r=>r!=selected).All(r=>r.IsIdle),240,
                    pinnedLabel+": healthy meal finishes while the requested collector remains unavailable");
                if(failed) yield break;
                Check(target.Phase==TaskPhase.Pending && target.AssignedTo==null && !target.FixedDishBatch
                    && target.RequestedRobotId=="A" && served.SameVisitReturnId==null
                    && sim.Inventory.Items[plate].Owner==GuardianDishOwner.Table,
                    pinnedLabel+": same visit cannot adopt a different requested actor or mint a bypass batch");
                SaveHeldState(pinnedLabel+"-waiting",target,new[]{plate});
                Check(free.Submit("A",GuardianHumanCommand.ResumeWork).Accepted,pinnedLabel+": explicitly enable the requested normal actor");
                yield return Wait(Drained,600,pinnedLabel+": original targeted request physically completes after resumption");
                Check(target.AssignedTo==selected && sim.Inventory.Items[plate].Owner==GuardianDishOwner.ReturnCounter,
                    pinnedLabel+": requested A performs the genuine pickup and return");
                SaveHumanTrace(free,pinnedLabel); SaveServeTrace(pinnedLabel); free.Exit();
            }
            report.Add("HUMAN_HELD_COLLECTION_OK: ordinary/fixed holds, independent batches and requested-actor promises respected through approval and full drain");
        }

        private IEnumerator HumanSameVisitHoldProbe()
        {
            Reset(2026,true); clearanceCase="human-samevisit-hold-probe";
            var exp=sim.EnterHumanExperience(false,false); exp.SelectedTable=0;
            var a=sim.Robots.Single(r=>r.RobotId=="A");
            Check(exp.Submit("A",GuardianHumanCommand.ClearDishes).Accepted,"hold probe accepts ordinary A clearing");
            var held=a.CurrentTask; var ids=held.DishIds.ToArray();
            Check(!held.FixedDishBatch && ids.Length==1 && !a.Carrying,"hold probe starts an ordinary unpicked single-plate task");
            exp.InjectEvidence("A",RobotCondition.Warning,false);
            Check(held.Phase==TaskPhase.Pending && !string.IsNullOrEmpty(held.OperatorHoldReason)
                && held.RequestedRobotId=="A" && a.EmergencyStopped,"hold probe awaits a human decision without cargo");
            SaveHeldState("probe-before-delivery",held,ids);
            var meal=sim.QueueTask(DeliveryKind.Food,0,GuardianMenu.CurryRice,"B");
            yield return Wait(()=>meal.Phase==TaskPhase.Completed && sim.Robots.Single(r=>r.RobotId=="B").IsIdle,240,
                "healthy B physically serves the same table and returns while A receives no answer");
            if(failed) yield break;
            SaveHeldState("probe-after-delivery",held,ids); SaveHumanTrace(exp,"samevisit-hold-probe");
            report.Add("SAMEVISIT_HOLD_PROBE task="+held.Id+" phase="+held.Phase+" assigned="+(held.AssignedTo?.RobotId??"none")
                +" hold="+held.OperatorHoldReason+" requested="+held.RequestedRobotId+" mealReturn="+(meal.SameVisitReturnId??"none")
                +" owner="+sim.Inventory.Items[ids[0]].Owner+" A.stopped="+a.EmergencyStopped);
            Check(held.Phase==TaskPhase.Pending && held.AssignedTo==null && meal.SameVisitReturnId==null
                && ids.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Table)
                && !sim.Inventory.Events.Any(e=>ids.Contains(e.Id) && e.From==GuardianDishOwner.Table && e.To==GuardianDishOwner.Robot),
                "same-visit collection cannot adopt or move a human-held ordinary plate without approval");
            exp.Exit();
        }
    }
}
