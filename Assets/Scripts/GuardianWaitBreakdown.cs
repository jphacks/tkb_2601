using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
namespace Guardian.UnitySim
{
    [Serializable] public sealed class GuardianReplayInput
    { public float Time; public string Type,Robot; public DeliveryKind Kind; public int Table; public GuardianMenu Menu; public RobotCondition Condition; }
    [Serializable] public sealed class GuardianReplayTrace { public List<GuardianReplayInput> Inputs=new List<GuardianReplayInput>(); }
    public struct GuardianTrafficWait { public string Category,Resource,Holder; public bool Stale; }
    public sealed class GuardianWaitBreakdown
    {
        private readonly GuardianSimulator sim;
        private readonly Dictionary<string,double> totals=new Dictionary<string,double>();
        private readonly Dictionary<string,double> initialWait=new Dictionary<string,double>();
        private readonly StringBuilder snapshots=new StringBuilder("clock,waiter,category,resource,holder,holderX,holderZ,holderGoalX,holderGoalZ\n");
        public readonly GuardianReplayTrace Trace=new GuardianReplayTrace();
        private readonly Dictionary<string,RobotCondition> conditions=new Dictionary<string,RobotCondition>();
        private readonly Dictionary<string,Dictionary<string,double>> byTask=new Dictionary<string,Dictionary<string,double>>();
        private int requestsSeen,replayIndex; private float nextSnapshot; private bool recording;
        private readonly GuardianReplayTrace replay;
        public GuardianWaitBreakdown(GuardianSimulator s,GuardianReplayTrace playback=null)
        { sim=s; replay=playback; recording=playback==null; foreach(var r in s.Robots) conditions[r.RobotId]=r.Condition; if(playback!=null) sim.PhysicsStepBeginning+=Playback; }
        private static void Add(Dictionary<string,double> d,string k,double dt) { if(!d.ContainsKey(k)) d[k]=0; d[k]+=dt; }
        public void CaptureInputs()
        {
            if(!recording) return;
            while(requestsSeen<sim.Requests.Count)
            { var r=sim.Requests[requestsSeen++]; Trace.Inputs.Add(new GuardianReplayInput{Time=r.ReceivedAt,Type="request",Kind=r.Kind,Table=r.TableIndex,Menu=r.Task==null?GuardianMenu.CurryRice:r.Task.Menu}); }
            foreach(var r in sim.Robots) if(conditions[r.RobotId]!=r.Condition)
            { Trace.Inputs.Add(new GuardianReplayInput{Time=sim.DemoClock,Type="condition",Robot=r.RobotId,Condition=r.Condition}); conditions[r.RobotId]=r.Condition; }
        }
        private void Playback(float clock)
        {
            while(replayIndex<replay.Inputs.Count && replay.Inputs[replayIndex].Time<=clock+.0001f)
            { var e=replay.Inputs[replayIndex++]; if(e.Type=="request") sim.QueueTask(e.Kind,e.Table,e.Menu); else sim.SetCondition(e.Robot,e.Condition); }
        }
        public void Tick(float dt)
        {
            CaptureInputs();
            var tasks=sim.Tasks.Where(t=>t.Phase!=TaskPhase.Completed).ToArray(); if(tasks.Length==0) return;
            string phase=sim.DemoClock<300?"fault":"recovery";
            Add(totals,phase+"/outstanding_wall_seconds",dt);
            int moving=sim.Robots.Count(r=>r.CurrentTask!=null && new Vector2(r.PhysicsDriver.Body.linearVelocity.x,r.PhysicsDriver.Body.linearVelocity.z).magnitude>.1f);
            bool work=sim.Robots.Any(r=>r.CurrentTask!=null), opportunity=false,seen=false; int union=0;
            foreach(var t in tasks)
            {
                int mask=0; for(int i=0;i<sim.Robots.Count;i++) { var r=sim.Robots[i]; if(r.DispatchEnabled && GuardianCapabilityPolicy.Allows(r.Condition,t.Kind,r.EmergencyStopped)) mask|=1<<i; }
                if(mask==0) continue;
                int combined=union|mask; if(seen && (combined&(combined-1))!=0) opportunity=true; seen=true; union=combined;
            }
            if(work) Add(totals,phase+"/assigned_work_wall_seconds",dt);
            if(moving>=2) Add(totals,phase+"/parallel_wall_seconds",dt);
            if(opportunity && work) Add(totals,phase+"/two_capable_distinct_jobs_work_seconds",dt);
            if(opportunity && moving>=2) Add(totals,phase+"/parallel_within_opportunity_seconds",dt);
            if(!opportunity && work) Add(totals,phase+"/at_most_one_capable_job_work_seconds",dt);
            foreach(var t in tasks.Where(t=>t.Phase==TaskPhase.Pending))
            {
                var capable=sim.Robots.Where(r=>r.DispatchEnabled && GuardianCapabilityPolicy.Allows(r.Condition,t.Kind)).ToArray();
                var eligible=capable.Where(r=>!r.EmergencyStopped).ToArray();
                string reason=capable.Length==0?"no_capability":eligible.Length==0?"explicit_stop":!eligible.Any(r=>r.IsIdle)?"eligible_busy_or_returning":sim.Traffic.PendingJobReason(t)??"source_or_one_step_scheduler_gap";
                Add(totals,phase+"/pending_task_seconds/"+reason,dt);
                if(t.FirstAssignedAt<0)
                { Add(initialWait,reason,dt); if(!byTask.ContainsKey(t.Id)) byTask[t.Id]=new Dictionary<string,double>(); Add(byTask[t.Id],reason,dt); }
                if(eligible.Any(r=>r.IsIdle)) Add(totals,phase+"/eligible_idle_but_pending_task_seconds/"+reason,dt);
            }
            foreach(var r in sim.Robots)
            {
                var d=r.PhysicsDriver; if(!d.Busy) { Add(totals,phase+"/actor_seconds/idle",dt); continue; }
                var w=sim.Traffic.WaitDetails(d);
                string cat=r.EmergencyStopped?"explicit_stop":w.Category??(d.WaitReason.Contains("obstacle")?"physical_braking":r.CurrentTask==null?"home_motion_or_alignment":"job_motion_or_alignment");
                Add(totals,phase+"/actor_seconds/"+cat,dt);
                if(w.Stale) Add(totals,phase+"/stale_kitchen_dock_wall_seconds",dt);
                if(sim.DemoClock>=nextSnapshot && w.Stale)
                {
                    var owner=sim.Robots.First(x=>x.RobotId==w.Holder).PhysicsDriver;
                    snapshots.AppendFormat(CultureInfo.InvariantCulture,"{0:F2},{1},{2},{3},{4},{5:F3},{6:F3},{7:F3},{8:F3}\n",sim.DemoClock,r.RobotId,cat,w.Resource,w.Holder,owner.Body.position.x,owner.Body.position.z,owner.Destination.x,owner.Destination.z);
                }
            }
            if(sim.DemoClock>=nextSnapshot) nextSnapshot=sim.DemoClock+1;
        }
        public static GuardianReplayTrace LoadTrace(string path)
        {
            var result=new GuardianReplayTrace();
            foreach(var line in File.ReadAllLines(path).Skip(1))
            { var a=line.Split(','); result.Inputs.Add(new GuardianReplayInput{Time=float.Parse(a[0],CultureInfo.InvariantCulture),Type=a[1],Robot=a[2],Kind=(DeliveryKind)int.Parse(a[3]),Table=int.Parse(a[4]),Menu=(GuardianMenu)int.Parse(a[5]),Condition=(RobotCondition)int.Parse(a[6])}); }
            return result;
        }
        public void Save(string folder)
        {
            CaptureInputs(); sim.PhysicsStepBeginning-=Playback;
            Directory.CreateDirectory(folder);
            var lines=new List<string>{"metric,seconds"}; lines.AddRange(totals.OrderBy(p=>p.Key).Select(p=>p.Key+","+p.Value.ToString("F4",CultureInfo.InvariantCulture))); File.WriteAllLines(Path.Combine(folder,"breakdown.csv"),lines);
            File.WriteAllText(Path.Combine(folder,"stale-kitchen-snapshots.csv"),snapshots.ToString());
            lines=new List<string>{"task,reason,initial_wait_task_seconds"}; foreach(var t in byTask) foreach(var v in t.Value) lines.Add(t.Key+","+v.Key+","+v.Value.ToString("F4",CultureInfo.InvariantCulture)); File.WriteAllLines(Path.Combine(folder,"initial-wait.csv"),lines);
            if(recording) File.WriteAllLines(Path.Combine(folder,"inputs.csv"),new[]{"time,type,robot,kind,table,menu,condition"}.Concat(Trace.Inputs.Select(e=>string.Join(",",e.Time.ToString("R",CultureInfo.InvariantCulture),e.Type,e.Robot??"",((int)e.Kind).ToString(),e.Table.ToString(),((int)e.Menu).ToString(),((int)e.Condition).ToString()))));
            File.WriteAllText(Path.Combine(folder,"input-status.txt"),recording?"Recorded actual requests and effective condition changes; original seeded workload/settings unchanged.\n":"Replayed "+replayIndex+" / "+replay.Inputs.Count+" identical recorded inputs at their simulation timestamps.\n");
        }
    }
}
