using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private readonly List<string> trafficTrace=new List<string>{"clock,seed,fleet,actor,task,phase,carrying,condition,wait,progress,position,velocity,yaw,route,claims,queue,central,contact"};
        private float trafficTraceAt=-1,trafficTraceClock=-1;
        private void CaptureTrafficTrace()
        {
            if(suite!="reservation7") return;
            if(sim.DemoClock<trafficTraceClock) trafficTraceAt=-1;
            trafficTraceClock=sim.DemoClock;
            if(sim.DemoClock<trafficTraceAt) return;
            trafficTraceAt=sim.DemoClock+.5f;
            foreach(var r in sim.Robots)
            {
                var d=r.PhysicsDriver;
                var cells=new[]{sim.DemoClock.ToString("F4",CultureInfo.InvariantCulture),sim.Settings.Seed.ToString(),sim.Robots.Count.ToString(),r.RobotId,
                    r.CurrentTask?.Id,r.CurrentTask?.Phase.ToString(),r.Carrying.ToString(),r.Condition.ToString(),d.WaitReason,d.ProgressWait.ToString("F4",CultureInfo.InvariantCulture),
                    d.Body.position.ToString("F5"),d.Body.linearVelocity.ToString("F5"),d.Body.rotation.eulerAngles.y.ToString("F4",CultureInfo.InvariantCulture),
                    d.DiagnosticRoute,sim.Traffic.DiagnosticClaims,sim.Traffic.DiagnosticQueue,sim.Traffic.Owner?.Robot.RobotId,d.MotionState};
                trafficTrace.Add(string.Join(",",cells.Select(s=>"\""+(s??"").Replace("\"","\"\"")+"\"")));
            }
        }
        private void SaveTrafficTrace()
        { if(trafficTrace.Count>1) File.WriteAllLines(Path.Combine(output,"traffic-trace.csv"),trafficTrace); }
    }
}
