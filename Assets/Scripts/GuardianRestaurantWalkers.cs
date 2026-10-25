using System;
using System.Collections.Generic;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Animated guests use the same floor grid, planner, follower and cell queues as robots.</summary>
    public sealed class GuardianRestaurantWalkers : MonoBehaviour
    {
        public sealed class Walker
        {
            public string Id;
            public GuardianPedestrianMotion Motion;
            public GuardianLegPlan Plan;
            public Vector3[] Nodes;
            public int Target=1,Node=1,CompletedLegs;
            public float Progress,RetryAt,Distance,BlockedSeconds,MaximumBlocked;
            internal Vector3 Previous;
        }
        private GuardianSimulator sim;
        private GuardianDirectTraffic traffic;
        private readonly List<Walker> walkers=new List<Walker>();
        private readonly FloorFootprint footprint=new FloorFootprint(0,0,.55f,.55f);
        public IReadOnlyList<Walker> People=>walkers;
        public int PlanningAttempts {get;private set;}
        public bool Running {get;private set;}
        private int nextPlanner;
        private static Vector3 P(float x,float z)=>new Vector3(x,0,z);
        private static FloorPoint Flat(Vector3 p)=>new FloorPoint(p.x,p.z);
        public void ResetPeople(GuardianSimulator simulator,bool active)
        {
            if(sim!=null)sim.PhysicsStepBeginning-=Step;
            foreach(var w in walkers){traffic?.Remove(w.Id);if(w.Motion!=null){w.Motion.gameObject.SetActive(false);Destroy(w.Motion.gameObject);}}
            walkers.Clear();sim=simulator;traffic=sim.Traffic.Direct;Running=false;PlanningAttempts=0;nextPlanner=0;
            if(!active || traffic==null)return;
            var catalog=Resources.Load<RestaurantCatalog>("RestaurantCatalog");
            if(catalog==null || catalog.WalkingGuest==null)throw new InvalidOperationException("Original walking guest reference missing from RestaurantCatalog");
            Add("Guest-1",catalog.WalkingGuest,new[]{P(-1.8f,-4.8f),P(1.5f,-4.8f),P(1.5f,-1.7f),P(-1.8f,-1.7f)});
            Add("Guest-2",catalog.WalkingGuest,new[]{P(1.5f,4.2f),P(-1.8f,4.2f),P(-1.8f,1.7f),P(1.5f,1.7f)});
            sim.PhysicsStepBeginning+=Step;Running=true;
        }
        private void Add(string id,GameObject prefab,Vector3[] nodes)
        {
            var root=Instantiate(prefab,transform);root.name="Walking "+id;
            var motion=root.AddComponent<GuardianPedestrianMotion>();motion.StartPoint=nodes[0];motion.EndPoint=nodes[1];motion.Initialize();
            var w=new Walker{Id=id,Motion=motion,Nodes=nodes,Previous=nodes[0],RetryAt=sim.DemoClock+.6f*walkers.Count};walkers.Add(w);
            traffic.Park(id,footprint.At(Flat(motion.Body.position),motion.Body.rotation.eulerAngles.y));
        }
        private void Step(float clock)
        {
            if(!Running)return;
            // At most one pedestrian request per fixed step; blocked routes retry at 2 Hz.
            for(int j=0;j<walkers.Count;j++)
            {
                int i=(nextPlanner+j)%walkers.Count;var w=walkers[i];
                if(w.Plan!=null || clock<w.RetryAt)continue;
                if(!traffic.TakePedestrianTurn(clock))break;
                PlanningAttempts++;w.RetryAt=clock+.5f;nextPlanner=(i+1)%walkers.Count;
                if(traffic.Request(w.Id,Flat(w.Motion.Body.position),w.Motion.Body.rotation.eulerAngles.y,footprint,Flat(w.Nodes[w.Target]),false,false,0,out var plan)==GuardianDirectRequestResult.Committed)
                {w.Plan=plan;w.Node=1;w.Progress=0;w.Motion.Walk();}
                break;
            }
            foreach(var w in walkers)
            {
                Vector3 p=w.Motion.Body.position;float moved=Vector3.Distance(w.Previous,p);w.Distance+=moved;w.Previous=p;
                if(w.Plan==null){if(w.Motion.Crossing)w.Motion.Wait();continue;}
                w.Progress=Mathf.Max(w.Progress,GuardianDirectControl.Project(w.Plan,w.Node,Flat(p)));traffic.UpdateProgress(w.Id,w.Progress);
                var control=GuardianDirectControl.Step(w.Plan,new GuardianDirectControl.Input{Position=Flat(p),Velocity=Flat(w.Motion.Velocity),Yaw=w.Motion.Body.rotation.eulerAngles.y,
                    YawRate=0,MaximumSpeed=.8f,Acceleration=2,Frontier=traffic.Frontier(w.Id),Progress=w.Progress,Node=w.Node});
                if(control.AdvanceNode)w.Node=Mathf.Min(w.Node+1,w.Plan.Points.Count-1);
                if(control.Arrived)
                {traffic.Complete(w.Id);w.Plan=null;w.Target=(w.Target+1)%w.Nodes.Length;w.CompletedLegs++;w.RetryAt=clock+.8f;w.Motion.Wait();continue;}
                w.Motion.MoveAlongRoute(new Vector3(control.Velocity.X,0,control.Velocity.Z),control.Yaw,Time.fixedDeltaTime);
                w.BlockedSeconds=moved<.0001f?w.BlockedSeconds+Time.fixedDeltaTime:0;w.MaximumBlocked=Mathf.Max(w.MaximumBlocked,w.BlockedSeconds);
            }
        }
        private void OnDestroy(){if(sim!=null)sim.PhysicsStepBeginning-=Step;foreach(var w in walkers)traffic?.Remove(w.Id);}
    }
}
