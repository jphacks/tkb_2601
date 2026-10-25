using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private void SaveHumanTrace(GuardianInterventionExperience experience,string name)
        {
            System.Func<string,string> csv=s=>"\""+(s??"").Replace("\"","\"\"")+"\"";
            File.WriteAllLines(Path.Combine(output,name+"-human-events.csv"),new[]{"sequence,sim_seconds,kind,actor,message"}.Concat(experience.Events.Select(e=>
                string.Join(",",new[]{e.Sequence.ToString(),e.Clock.ToString("F4",CultureInfo.InvariantCulture),e.Kind,e.Actor,e.Message}.Select(csv)))));
            Check(experience.Events.Zip(experience.Events.Skip(1),(a,b)=>b.Clock>=a.Clock).All(v=>v),"human event timestamps remain chronological");
        }
        private IEnumerator LinkedStateRegression()
        {
            Reset(); clearanceCase="linked-state";
            bool previousSupervision=sim.OperatorSupervisionEnabled;
            sim.OperatorSupervisionEnabled=true;
            var b=sim.Robots.Single(r=>r.RobotId=="B");

            Check(!sim.HumanExperienceActive,"linked-state regression starts without the manual decision flow");
            Check(sim.SetLinkedCondition("B",RobotCondition.Warning),"linked Warning state is accepted");
            yield return new WaitForFixedUpdate();
            Check(b.Condition==RobotCondition.Warning && !b.EmergencyStopped && b.DispatchEnabled && !sim.HumanExperienceActive,
                "linked Warning keeps motion available without opening an operator alert");
            Check(!sim.AllowsWork(b,DeliveryKind.Soup) && sim.AllowsWork(b,DeliveryKind.Food)
                && sim.AllowsWork(b,DeliveryKind.EmptyDishes),"linked Warning applies the existing capability matrix");

            Check(sim.SetLinkedCondition("B",RobotCondition.Limited),"linked Limited state is accepted");
            yield return new WaitForFixedUpdate();
            Check(b.Condition==RobotCondition.Limited && !b.EmergencyStopped && b.DispatchEnabled && !sim.HumanExperienceActive,
                "linked Limited keeps motion available without opening an operator alert");
            Check(!sim.AllowsWork(b,DeliveryKind.Soup) && !sim.AllowsWork(b,DeliveryKind.Food)
                && sim.AllowsWork(b,DeliveryKind.EmptyDishes),"linked Limited permits only empty-dish work");

            sim.SetLinkedEmergencyStop("B",true);
            yield return new WaitForFixedUpdate();
            Check(b.EmergencyStopped && !sim.HumanExperienceActive,
                "linked Hold stops the robot without creating a manual decision latch");
            sim.SetLinkedEmergencyStop("B",false);
            Check(sim.SetLinkedCondition("B",RobotCondition.Normal),"linked Normal recovery is accepted");
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            Check(b.Condition==RobotCondition.Normal && !b.EmergencyStopped && b.DispatchEnabled && !sim.HumanExperienceActive,
                "linked Hold to Normal recovery resumes without a stale operator latch");

            sim.SetCondition("B",RobotCondition.Warning);
            yield return new WaitForFixedUpdate();
            Check(sim.HumanExperienceActive && sim.HumanExperience.Actors["B"].AwaitingDecision && b.EmergencyStopped,
                "manual/demo condition input still opens the existing operator decision flow");

            sim.HumanExperience.EndWithoutReset();
            sim.SetLinkedEmergencyStop("B",false);
            Check(sim.SetLinkedCondition("B",RobotCondition.Normal),"linked-state regression restores B to Normal");
            sim.SetDispatchEnabled(b,true);
            sim.OperatorSupervisionEnabled=previousSupervision;
            Reset();
        }
        private IEnumerator HumanInterventionRegression()
        {
            foreach(bool five in new[]{false,true})
            {
                Reset(2026,true); clearanceCase="human-guided";
                var exp=sim.EnterHumanExperience(true,five);
                Check(sim.Mode==GuardianSimulationMode.Physics && !sim.AutomaticTasks && !sim.Settings.RandomFaults
                    && sim.Robots.Count==(five?5:3),"independent guided mode uses the physical engine and chosen fleet");
                yield return Wait(()=>exp.Stage==GuardianHumanStage.YellowDecision,180,"C receives yellow alert before soup pickup");
                if(failed) yield break;
                var c=sim.Robots.Single(r=>r.RobotId=="C"); var primary=sim.Tasks.Single(t=>t.Id==exp.PrimaryTaskId);
                string mealId=primary.DishIds.Single(); int instance=sim.Inventory.Items[mealId].GetInstanceID();
                Vector3 stopped=c.PhysicsDriver.Body.position; yield return new WaitForSeconds(3);
                Check(c.Condition==RobotCondition.Warning && c.EmergencyStopped && Vector3.Distance(stopped,c.PhysicsDriver.Body.position)<.005f
                    && primary.Phase==TaskPhase.Pending && primary.AssignedTo==null && !string.IsNullOrEmpty(primary.OperatorHoldReason),
                    "no-answer yellow alert stops motion and keeps assignment for human judgment");
                Check(sim.Inventory.Items[mealId].Owner==GuardianDishOwner.Kitchen && sim.Inventory.Items[mealId].gameObject.activeInHierarchy,"unpicked meal remains visible at its source");
                var rejection=exp.Submit("C",GuardianHumanCommand.ContinueDelivery,exp.Actors["C"].Revision);
                Check(!rejection.Accepted && rejection.Reason.Contains("液体") && c.EmergencyStopped,"yellow liquid continuation rejects with a reason and preserves stop");
                int stale=exp.Actors["C"].Revision; exp.InjectEvidence("C",RobotCondition.Warning,false);
                Check(!exp.Submit("C",GuardianHumanCommand.Transfer,stale).Accepted && primary.AssignedTo==null,"stale human decision cannot release a newer alert");
                Check(exp.Submit("C",GuardianHumanCommand.Transfer,exp.Actors["C"].Revision).Accepted,"current transfer decision accepted");
                yield return Wait(()=>exp.Stage==GuardianHumanStage.RedDecision,500,"same meal is physically delivered before the red episode");
                if(failed) yield break;
                Check(primary.AssignmentVersion>=2 && primary.AssignedTo!=c && sim.Inventory.Items[mealId].GetInstanceID()==instance
                    && sim.Inventory.Items[mealId].Owner==GuardianDishOwner.Table,"human-authorized reassignment preserves the same meal instance");
                var a=sim.Robots.Single(r=>r.RobotId=="A");
                Check(a.Condition==RobotCondition.Limited && !exp.Actors["A"].MobilityUnsafe,"red episode explicitly distinguishes reduced capability from unsafe mobility");
                Check(!exp.Submit("A",GuardianHumanCommand.ContinueDelivery).Accepted,"red meal delivery rejected");
                var clearing=exp.Submit("A",GuardianHumanCommand.ClearDishes);
                Check(clearing.Accepted,"mobile red actor accepts only an eligible empty-dish instruction");
                var clear=sim.Tasks.Single(t=>t.Id==clearing.TaskId);
                yield return Wait(()=>clear.Phase==TaskPhase.ToDropoff && a.Carrying,240,"red actor actually collects the requested table's plate");
                if(failed) yield break;
                Check(clear.AssignedTo==a && clear.DishIds.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Robot
                    && sim.Inventory.Items[id].transform.parent==a.GetComponent<GuardianCargoTray>().Anchors[sim.Inventory.Items[id].TraySlot]),"selected red actor carries exactly its collected identities on its rear shelves");
                yield return Wait(()=>exp.Stage==GuardianHumanStage.UnsafeDecision,400,"unsafe-mobility episode starts after A is parked");
                if(failed) yield break;
                yield return new WaitForSeconds(2); stopped=a.PhysicsDriver.Body.position;
                Check(a.EmergencyStopped && a.PhysicsDriver.Body.linearVelocity.sqrMagnitude<.0001f && exp.Actors["A"].AwaitingDecision,
                    "unsafe actor is physically stopped before a human reply");
                Check(!exp.Submit("A",GuardianHumanCommand.ClearDishes).Accepted && !exp.Submit("A",GuardianHumanCommand.ContinueDelivery).Accepted,
                    "unsafe mobility overrides both clearing capability and meal commands");
                sim.SetEmergencyStop("A",false); yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
                Check(a.EmergencyStopped && Vector3.Distance(stopped,a.PhysicsDriver.Body.position)<.005f,"safety enforcement restores stop before actuation even after a bypass attempt");
                if(five)
                {
                    Check(exp.Submit("A",GuardianHumanCommand.DeploySpare).Accepted,"human can request healthy spare deployment");
                    yield return new WaitForSeconds(1);
                    Check(sim.Robots.Single(r=>r.RobotId=="D").FleetRole==GuardianFleetRole.Active
                        && sim.Fleet.Commands.Last().State!=GuardianCommandState.Completed,"deployment acceptance is not falsely reported as physical departure");
                }
                else Check(!exp.Submit("A",GuardianHumanCommand.DeploySpare).Accepted,"three-actor mode explains lack of a spare");
                Check(exp.Submit("A",GuardianHumanCommand.KeepStopped).Accepted,"human chooses continued stop and independent healthy work");
                yield return Wait(()=>exp.Stage==GuardianHumanStage.Complete && Drained(),900,"guided work fully drains while unsafe actor remains stopped");
                if(failed) yield break;
                Check(a.EmergencyStopped && exp.Actors["A"].MobilityUnsafe && !a.DispatchEnabled,"acknowledgment never clears unresolved physical risk");
                var final=sim.Tasks.Single(t=>t.Id==exp.FinalTaskId);
                Check(final.AssignedTo!=a && (!five || final.AssignedTo.RobotId=="D"),"healthy actor/spare performs the final real delivery");
                Check(exp.Events.Any(e=>e.Kind=="Alert") && exp.Events.Any(e=>e.Kind=="Rejected") && exp.Events.Any(e=>e.Kind=="Robot")
                    && exp.Events.Any(e=>e.Kind=="Dish"),"timeline contains alerts, refusal reasons and actual robot/dish responses");
                SaveHumanTrace(exp,"guided-"+(five?5:3)); SaveServeTrace("guided-"+(five?5:3));
                Check(exp.Submit("A",GuardianHumanCommand.Inspect).Accepted,"explicit simulated inspection permits recovery");
                yield return Wait(()=>a.IsIdle && (!five || a.FleetRole==GuardianFleetRole.Standby),120,"inspection recovery reaches physical standby");
                exp.Exit();
                Check(!sim.HumanExperienceActive && sim.Mode==GuardianSimulationMode.Physics && sim.Settings.MealSeconds==8 && sim.Robots.Count==3,
                    "exit restores ordinary settings/fleet without experience holds");
            }

            Reset(2026,true); clearanceCase="human-free"; var free=sim.EnterHumanExperience(false,false);
            var delivery=sim.QueueTask(DeliveryKind.Soup,0,GuardianMenu.Soup,"B"); var b=sim.Robots.Single(r=>r.RobotId=="B");
            yield return Wait(()=>b.Carrying && Vector3.Distance(b.PhysicsDriver.Body.position,delivery.Pickup)>2,180,"free-mode actor physically carries soup before fault");
            if(failed) yield break;
            string sameId=delivery.DishIds.Single();
            free.InjectEvidence("B",RobotCondition.Warning,false); yield return new WaitForSeconds(2);
            Check(b.EmergencyStopped && sim.Inventory.Items[sameId].Owner==GuardianDishOwner.Robot && sim.Inventory.Items[sameId].OwnerKey=="B",
                "carried fault retains physical ownership while waiting for the human");
            Check(!free.Submit("B",GuardianHumanCommand.ContinueDelivery).Accepted && free.Submit("B",GuardianHumanCommand.Transfer).Accepted,
                "carried liquid continuation rejects but safe source-return transfer accepts");
            yield return Wait(Drained,900,"human transfer returns the same cargo to source before another actor delivers it");
            if(failed) yield break;
            Check(delivery.AssignedTo!=b && delivery.AssignmentVersion>=2 && sim.Inventory.Events.Any(e=>e.Id==sameId
                && e.From==GuardianDishOwner.Robot && e.FromKey=="B" && e.To==GuardianDishOwner.Kitchen),"free-mode handoff uses the genuine kitchen source before reassignment");
            free.InjectEvidence("B",RobotCondition.Limited,true); yield return new WaitForSeconds(2);
            free.InjectEvidence("B",RobotCondition.Normal,false);
            Check(free.Actors["B"].MobilityUnsafe && b.EmergencyStopped && !free.Submit("B",GuardianHumanCommand.ClearDishes).Accepted,
                "green evidence alone cannot clear an unsafe-motion latch");
            SaveHumanTrace(free,"free"); SaveServeTrace("free");
            sim.ResetScenario();
            Check(sim.HumanExperienceActive && free.Stage==GuardianHumanStage.Free && sim.Tasks.Count==0
                && sim.Robots.All(r=>!r.EmergencyStopped && r.Condition==RobotCondition.Normal) && free.Actors.Values.All(s=>!s.MobilityUnsafe),
                "reset clears alerts, holds and risk in a fresh independent free session");
            sim.SetMode(GuardianSimulationMode.LegacyStory);
            Check(!sim.HumanExperienceActive && sim.Mode==GuardianSimulationMode.LegacyStory && sim.Settings.MealSeconds==8,
                "legacy selection exits/restores experience settings");
            if(failed) yield break;
            yield return HumanFreeHandoffRegression();
            if(failed) yield break;
            yield return HumanThreeActorRecoveryRegression();
            if(failed) yield break;
            yield return HumanHeldCollectionRegression();
            if(failed) yield break;
            report.Add("GUARDIAN_HUMAN_TEST_OK: API-driven operating physics; no GUI-click or pixel acceptance claimed");
        }
    }
}
