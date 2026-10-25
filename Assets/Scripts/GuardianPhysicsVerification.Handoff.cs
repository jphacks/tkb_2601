using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private IEnumerator HumanHandoffProbe()
        {
            var outcomes=new List<KeyValuePair<string,bool>>();
            foreach(var command in new[]{GuardianHumanCommand.Transfer,GuardianHumanCommand.KeepStopped})
            {
                Reset(2026,true); clearanceCase="handoff-probe-"+command;
                var exp=sim.EnterHumanExperience(false,false); exp.SelectedTable=0;
                var a=sim.Robots.Single(r=>r.RobotId=="A");
                var queued=exp.Submit("A",GuardianHumanCommand.ClearDishes);
                Check(queued.Accepted && a.CurrentTask!=null && !a.Carrying
                    && exp.Actors["A"].HeldTask==null && !exp.Actors["A"].AwaitingDecision,
                    "probe starts a targeted, unpicked clearing job without any alert");
                if(failed) yield break;
                var task=a.CurrentTask;
                bool accepted=exp.Submit("A",command).Accepted;
                yield return new WaitForSeconds(30);
                report.Add("HANDOFF_PROBE command="+command+" accepted="+accepted+" phase="+task.Phase
                    +" requested="+(task.RequestedRobotId??"none")+" assigned="+(task.AssignedTo?.RobotId??"none")
                    +" held="+(exp.Actors["A"].HeldTask?.Id??"none")+" A.dispatch="+a.DispatchEnabled
                    +" B.dispatch="+sim.Robots.Single(r=>r.RobotId=="B").DispatchEnabled
                    +" C.dispatch="+sim.Robots.Single(r=>r.RobotId=="C").DispatchEnabled
                    +" pendingReason="+task.PendingReason);
                outcomes.Add(new KeyValuePair<string,bool>(command.ToString(),accepted && task.RequestedRobotId==null
                    && task.AssignedTo!=null && task.AssignedTo!=a && task.Phase!=TaskPhase.Pending));
                SaveHumanTrace(exp,"probe-"+command); exp.Exit();
            }
            foreach(var outcome in outcomes)
                Check(outcome.Value,"unalerted unpicked "+outcome.Key+" releases the actual target pin and makes healthy progress");
        }
    }
}
