using System;
using System.Collections.Generic;
using UnityEngine;
namespace Guardian.UnitySim
{
    public struct GuardianSafetyAgent
    {
        public bool Robot;public Vector3 Position,Velocity,Intent;public float Radius,MaximumSpeed,Uncertainty;public GuardianPlanarDynamics Model;
    }
    public struct GuardianSafetyPair {public int Robot,Person;public GuardianSafetyPair(int r,int p){Robot=r;Person=p;}}
    /// <summary>Swept AABB hash. All covered cells are inserted; large envelopes fall
    /// back to exhaustive pairs rather than truncating safety work.</summary>
    public sealed class GuardianSafetyBroadphase
    {
        private struct Region {public float X0,Z0,X1,Z1;}
        private readonly Dictionary<long,List<int>> cells=new Dictionary<long,List<int>>();
        private readonly List<List<int>> pool=new List<List<int>>();private readonly HashSet<ulong> seen=new HashSet<ulong>();
        private readonly List<GuardianSafetyPair> pairs=new List<GuardianSafetyPair>();private Region[] regions=Array.Empty<Region>();
        public IReadOnlyList<GuardianSafetyPair> Pairs=>pairs;public int CellCount=>cells.Count;public int Fallbacks {get;private set;}
        public float CellSize=2.5f;
        private const int MaximumCellCoordinate=1000000;
        public void Build(GuardianSafetyAgent[] agents,int count,float dt,bool spatial=true)
        {
            pairs.Clear();seen.Clear();cells.Clear();if(!spatial){Exhaustive(agents,count);return;}
            if(!float.IsFinite(CellSize)||CellSize<=0||!float.IsFinite(dt)||dt<=0){Fallbacks++;Exhaustive(agents,count);return;}
            for(int i=0;i<count;i++)if(!Valid(agents[i])){Fallbacks++;Exhaustive(agents,count);return;}
            if(regions.Length<count)Array.Resize(ref regions,Mathf.NextPowerOfTwo(count));
            float horizon=2.5f;
            for(int i=0;i<count;i++)if(agents[i].Robot && agents[i].Model!=null)
            {var a=agents[i];float cap=Mathf.Max(a.MaximumSpeed,Mathf.Max(a.Velocity.magnitude,a.Intent.magnitude));horizon=Mathf.Max(horizon,a.Model.ReactionSeconds(dt)+cap/a.Model.ConservativeBrakeAcceleration+.1f);}
            int pooled=0,insertions=0;
            for(int i=0;i<count;i++)
            {
                var a=agents[i];Vector3 lo=a.Position,hi=a.Position;
                if(a.Robot)
                {
                    if(!Include(a,a.Velocity,dt,ref lo,ref hi)||!Include(a,a.Intent,dt,ref lo,ref hi)){Fallbacks++;pairs.Clear();Exhaustive(agents,count);return;}
                }
                else {Vector3 end=a.Position+a.Velocity*horizon;lo=Vector3.Min(lo,end);hi=Vector3.Max(hi,end);}
                float radius=a.Radius+(a.Robot ? .35f+a.Uncertainty : 0);
                var r=new Region{X0=lo.x-radius,Z0=lo.z-radius,X1=hi.x+radius,Z1=hi.z+radius};regions[i]=r;
                double gx0=Math.Floor((double)r.X0/CellSize),gx1=Math.Floor((double)r.X1/CellSize),gz0=Math.Floor((double)r.Z0/CellSize),gz1=Math.Floor((double)r.Z1/CellSize);
                if(!double.IsFinite(gx0)||!double.IsFinite(gx1)||!double.IsFinite(gz0)||!double.IsFinite(gz1)||gx0< -MaximumCellCoordinate||gx1>MaximumCellCoordinate||gz0< -MaximumCellCoordinate||gz1>MaximumCellCoordinate)
                {Fallbacks++;pairs.Clear();Exhaustive(agents,count);return;}
                int x0=(int)gx0,x1=(int)gx1,z0=(int)gz0,z1=(int)gz1;
                // Widen BEFORE subtraction and reject dimensions before product.
                long width=(long)x1-x0+1,height=(long)z1-z0+1;
                if(width<=0||height<=0||width>4096||height>4096){Fallbacks++;pairs.Clear();Exhaustive(agents,count);return;}
                long coverage=width*height;
                if(coverage>4096 || (long)insertions+coverage>65536){Fallbacks++;pairs.Clear();Exhaustive(agents,count);return;}insertions+=(int)coverage;
                for(int x=x0;x<=x1;x++)for(int z=z0;z<=z1;z++)
                {
                    long key=((long)x<<32)^(uint)z;
                    if(!cells.TryGetValue(key,out var list)){if(pooled==pool.Count)pool.Add(new List<int>());list=pool[pooled++];list.Clear();cells.Add(key,list);}list.Add(i);
                }
            }
            foreach(var list in cells.Values)for(int j=0;j<list.Count;j++)for(int k=j+1;k<list.Count;k++)
            {
                int a=list[j],b=list[k];if(agents[a].Robot==agents[b].Robot)continue;var x=regions[a];var y=regions[b];
                if(x.X0>y.X1 || y.X0>x.X1 || x.Z0>y.Z1 || y.Z0>x.Z1)continue;
                int robot=agents[a].Robot?a:b,person=agents[a].Robot?b:a;ulong key=((ulong)(uint)robot<<32)|(uint)person;if(seen.Add(key))pairs.Add(new GuardianSafetyPair(robot,person));
            }
        }
        private static bool Include(GuardianSafetyAgent a,Vector3 velocity,float dt,ref Vector3 lo,ref Vector3 hi)
        {
            float d=a.Model!=null?a.Model.StoppingBound(velocity.magnitude,a.MaximumSpeed,dt):velocity.magnitude*2.5f;
            if(!float.IsFinite(d))return false;
            Vector3 end=a.Position+(velocity.sqrMagnitude>.0000001f?velocity.normalized*d:Vector3.zero);lo=Vector3.Min(lo,end);hi=Vector3.Max(hi,end);
            return Finite(lo)&&Finite(hi);
        }
        private static bool Finite(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);
        private static bool Valid(GuardianSafetyAgent a)=>Finite(a.Position)&&Finite(a.Velocity)&&Finite(a.Intent)&&float.IsFinite(a.Radius)&&a.Radius>=0&&float.IsFinite(a.MaximumSpeed)&&a.MaximumSpeed>=0&&float.IsFinite(a.Uncertainty)&&a.Uncertainty>=0;
        private void Exhaustive(GuardianSafetyAgent[] agents,int count)
        {for(int i=0;i<count;i++)if(agents[i].Robot)for(int j=0;j<count;j++)if(!agents[j].Robot)pairs.Add(new GuardianSafetyPair(i,j));}
        public static bool Hazard(GuardianSafetyAgent robot,GuardianSafetyAgent person,float dt)
        {
            if(!Valid(robot)||!Valid(person)||!float.IsFinite(dt)||dt<=0)return true; // Unsupported data never becomes a clear pair.
            float required=robot.Radius+person.Radius+.20f+robot.Uncertainty;
            if(Vector3.ProjectOnPlane(robot.Position-person.Position,Vector3.up).sqrMagnitude<(required+.15f)*(required+.15f))return true;
            if(robot.Model!=null)return GuardianPedestrianSafety.PredictPhysical(robot.Position,robot.Velocity,person.Position,person.Velocity,required,robot.Model,robot.MaximumSpeed,dt).Hazard || GuardianPedestrianSafety.PredictPhysical(robot.Position,robot.Intent,person.Position,person.Velocity,required,robot.Model,robot.MaximumSpeed,dt).Hazard;
            return GuardianPedestrianSafety.Predict(robot.Position,robot.Velocity,person.Position,person.Velocity,required,6,dt).Hazard || GuardianPedestrianSafety.Predict(robot.Position,robot.Intent,person.Position,person.Velocity,required,6,dt).Hazard;
        }
    }
}
