using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private sealed class PairSample
        {
            public string Scenario,A,B,PoseA,PoseB,ShapeA,ShapeB,MotionA,MotionB,Owner;
            public int Seed,Fleet,Steps;
            public float Maximum,Closest=float.PositiveInfinity,Clock,Tilt;
        }
        private readonly Dictionary<string,PairSample> robotPairs=new Dictionary<string,PairSample>();
        private string clearanceCase="startup";
        private void RecordRobotPair(GuardianPhysicsDriver a,GuardianPhysicsDriver b,float depth)
        {
            string key=clearanceCase+"/"+sim.Settings.Seed+"/"+sim.Robots.Count+"/"+a.Robot.RobotId+"/"+b.Robot.RobotId;
            if(!robotPairs.TryGetValue(key,out var row))
            {
                row=new PairSample{Scenario=clearanceCase,Seed=sim.Settings.Seed,Fleet=sim.Robots.Count,A=a.Robot.RobotId,B=b.Robot.RobotId};
                robotPairs[key]=row;
            }
            row.Steps++;
            float distance=Vector3.Distance(a.Body.worldCenterOfMass,b.Body.worldCenterOfMass);
            row.Tilt=Mathf.Max(row.Tilt,Vector3.Angle(a.Body.rotation*Vector3.up,Vector3.up),Vector3.Angle(b.Body.rotation*Vector3.up,Vector3.up));
            if(depth>row.Maximum || (row.Maximum==0 && distance<row.Closest))
            {
                row.Maximum=Mathf.Max(row.Maximum,depth); row.Closest=distance; row.Clock=sim.DemoClock;
                row.PoseA=a.Body.position.ToString("F5")+" / "+a.Body.rotation.eulerAngles.ToString("F5");
                row.PoseB=b.Body.position.ToString("F5")+" / "+b.Body.rotation.eulerAngles.ToString("F5");
                row.ShapeA=a.Shape.size.ToString("F5")+" center="+a.Shape.center.ToString("F5");
                row.ShapeB=b.Shape.size.ToString("F5")+" center="+b.Shape.center.ToString("F5");
                row.MotionA=a.MotionState; row.MotionB=b.MotionState; row.Owner=sim.Traffic.Owner?.Robot.RobotId??"none";
            }
        }
        private void SaveRobotPairs()
        {
            report.Add("DISH_VISIBILITY pickups="+verifiedPickups+" returns="+verifiedReturns+"; same instances, table siblings preserved, returned objects inactive");
            report.Add("CPU_BODY_CARGO_ENVELOPES corners="+envelopeCorners+" maximumOutside="+maximumEnvelopeExcess.ToString("F7",CultureInfo.InvariantCulture)+"m numericTolerance=0.0001m; status labels excluded, not pixels");
            System.Func<string,string> csv=s=>"\""+(s??"").Replace("\"","\"\"")+"\"";
            var rows=robotPairs.Values.OrderBy(p=>p.Scenario).ThenBy(p=>p.Fleet).ThenBy(p=>p.Seed).ThenBy(p=>p.A).ThenBy(p=>p.B)
                .Select(p=>string.Join(",",new[]{p.Scenario,p.Seed.ToString(),p.Fleet.ToString(),p.A,p.B,p.Steps.ToString(),
                    p.Maximum.ToString("F9",CultureInfo.InvariantCulture),p.Clock.ToString("F4",CultureInfo.InvariantCulture),
                    p.Closest.ToString("F6",CultureInfo.InvariantCulture),p.Tilt.ToString("F5",CultureInfo.InvariantCulture),
                    p.PoseA,p.PoseB,p.ShapeA,p.ShapeB,p.Owner,p.MotionA,p.MotionB}.Select(csv)));
            File.WriteAllLines(Path.Combine(output,"robot-pair-maxima.csv"),new[]{"scenario,seed,fleet,a,b,samples,max_penetration_m,at_sim_seconds,center_distance_m,max_tilt_degrees,pose_a,pose_b,shape_a,shape_b,central_owner,motion_a,motion_b"}.Concat(rows));
            report.Add("ROBOT_PAIR_CLEARANCE pairs="+robotPairs.Count+" maximum="+(robotPairs.Count==0?0:robotPairs.Values.Max(p=>p.Maximum)).ToString("F9",CultureInfo.InvariantCulture)
                +"m required="+(suite=="contact42-baseline"?"baseline diagnostic only":"0m")+"; collider measurement, not pixels");
        }
    }
}
