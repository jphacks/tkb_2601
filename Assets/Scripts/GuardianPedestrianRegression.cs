using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Actual fixed-step negative cases. Robot relocation is fixture initialization only.</summary>
    public sealed class GuardianPedestrianRegression : MonoBehaviour
    {
        [Serializable] public sealed class Case
        {
            public float yaw,fixedDeltaTime,simulatedSeconds,maxPenetration,minCircleGap=float.MaxValue,minSurfaceGap=float.MaxValue,finalX;
            public bool operatorHeldAtStart,remainedBeforeRobot,robotStationary,waitedForClearance,resumedAfterClearance;
        }
        [Serializable] public sealed class Report
        {
            public string status="FAIL",scope="Unity fixed-step physical negative cases; no GUI, graphics or hardware",error="";
            public List<Case> occupied=new List<Case>();
            public float oldObservationClock,resetClock;
            public bool observedBeforeOcclusion,occludedBeforeReset,unobservedAfterReset,untrackedAfterReset,unknownHoldAfterReset,ackRefusedAfterReset;
            public int capsuleCastHits,capsuleOverlapHits,querySaturations;
            public double realSeconds;
        }
        public bool Done {get;private set;} public bool Passed=>report.status=="PASS";
        public object ExportReport=>report;
        private readonly Report report=new Report();
        private GuardianInteractiveExperience experience;private GuardianSimulator sim;private double began;
        private IEnumerator Start()
        {
            began=Time.realtimeSinceStartupAsDouble;experience=GetComponent<GuardianInteractiveExperience>();
            while(!experience.Ready)yield return null;sim=experience.Sim;Time.timeScale=8;float originalDt=Time.fixedDeltaTime;
            foreach(float dt in new[]{.02f,.01f})
            foreach(float yaw in new[]{0f,90f})
            {
                Time.fixedDeltaTime=dt;experience.BeginDemo();var driver=sim.Robots[0].PhysicsDriver;Vector3 originalHome=driver.Home;
                foreach(var r in sim.Robots)r.GetComponent<GuardianMotionInterlock>().KeepStopped();
                driver.ResetTo(new Vector3(-1,driver.PlaneY,-1.9f));
                // Test setup only, before walking begins. Production motion has one body owner.
                driver.Body.rotation=Quaternion.Euler(0,yaw,0);Physics.SyncTransforms();
                var item=new Case{yaw=yaw,fixedDeltaTime=dt,operatorHeldAtStart=sim.Robots[0].GetComponent<GuardianMotionInterlock>().Held};report.occupied.Add(item);
                Vector3 parked=driver.Body.position;experience.Person.Walk();
                for(int frame=0;frame<Mathf.CeilToInt(10/Time.fixedDeltaTime);frame++)
                {
                    yield return new WaitForFixedUpdate();
                    var person=experience.Person;
                    item.waitedForClearance|=person.WaitingForClearance;
                    if(Physics.ComputePenetration(driver.Shape,driver.Body.position,driver.Body.rotation,person.Shape,person.Body.position,person.Body.rotation,out var direction,out float depth))item.maxPenetration=Mathf.Max(item.maxPenetration,depth);
                    float gap=Vector2.Distance(new Vector2(driver.Body.position.x,driver.Body.position.z),new Vector2(person.Body.position.x,person.Body.position.z))-driver.TurningRadius-GuardianPedestrianMotion.Radius;
                    item.minCircleGap=Mathf.Min(item.minCircleGap,gap);
                    Vector3 point=person.Body.position+person.Shape.center;Vector3 surface=driver.Shape.ClosestPoint(point)-point;surface.y=0;
                    item.minSurfaceGap=Mathf.Min(item.minSurfaceGap,surface.magnitude-person.Shape.radius);
                }
                item.simulatedSeconds=sim.DemoClock;item.finalX=experience.Person.Body.position.x;
                item.remainedBeforeRobot=item.finalX<parked.x-GuardianPedestrianMotion.Radius;
                item.robotStationary=Vector3.Distance(parked,driver.Body.position)<.001f;
                // Removing the fixture must allow the outstanding crossing request
                // to proceed without another Walk call or teleporting the person.
                driver.ResetTo(originalHome);
                for(int frame=0;frame<Mathf.CeilToInt(8/Time.fixedDeltaTime);frame++)yield return new WaitForFixedUpdate();
                item.resumedAfterClearance=experience.Person.Body.position.x>=experience.Person.EndPoint.x-.03f && !experience.Person.WaitingForClearance;
            }
            // Observe at t>10, then obscure the old and reset positions with an overhead slab.
            while(sim.DemoClock<=11)yield return new WaitForFixedUpdate();
            experience.Person.ResetPedestrian();Physics.SyncTransforms();experience.Safety.Evaluate(true);
            report.oldObservationClock=sim.DemoClock;report.observedBeforeOcclusion=experience.Safety.Observed;
            var slab=GameObject.CreatePrimitive(PrimitiveType.Cube);slab.name="Temporary regression LOS occluder";
            slab.transform.position=new Vector3(0,3.5f,-1.9f);slab.transform.localScale=new Vector3(10,1,7);Physics.SyncTransforms();
            experience.Safety.Evaluate(true);report.occludedBeforeReset=!experience.Safety.Observed;
            experience.BeginDemo();experience.Safety.Evaluate();report.resetClock=sim.DemoClock;
            report.unobservedAfterReset=!experience.Safety.Observed;report.untrackedAfterReset=!experience.Safety.Tracked;
            report.unknownHoldAfterReset=(sim.Robots[0].GetComponent<GuardianMotionInterlock>().Current&GuardianMotionHold.Unobserved)!=0;
            report.ackRefusedAfterReset=!experience.ResumeSafety(sim.Robots[0]);
            Destroy(slab);
            // Both reusable capsule query paths must grow instead of silently
            // dropping one of 64 tightly packed colliders beyond their 32 slots.
            var fixtures=new List<GameObject>();
            for(int i=0;i<64;i++)
            {
                var box=GameObject.CreatePrimitive(PrimitiveType.Cube);box.name="Query saturation fixture";
                box.transform.position=new Vector3(100+(i%4-1.5f)*.06f,.8f+((i/4)%4)*.12f,100+(i/16-1.5f)*.06f);box.transform.localScale=Vector3.one*.03f;fixtures.Add(box);
            }
            Physics.SyncTransforms();var query=new GuardianQueryBuffer();
            report.capsuleCastHits=query.Capsule(new Vector3(100,.4f,98),new Vector3(100,1.6f,98),.4f,Vector3.forward,4);
            report.capsuleOverlapHits=query.OverlapCapsule(new Vector3(100,.4f,100),new Vector3(100,1.6f,100),.5f);report.querySaturations=query.Saturations;
            foreach(var box in fixtures)Destroy(box);
            bool ok=report.observedBeforeOcclusion && report.occludedBeforeReset && report.unobservedAfterReset && report.untrackedAfterReset && report.unknownHoldAfterReset && report.ackRefusedAfterReset;
            ok&=report.capsuleCastHits==64 && report.capsuleOverlapHits==64 && report.querySaturations>0;
            foreach(var item in report.occupied)ok&=item.operatorHeldAtStart && item.robotStationary && item.remainedBeforeRobot && item.waitedForClearance && item.resumedAfterClearance && item.maxPenetration<.001f && item.minSurfaceGap>=.14f;
            report.status=ok?"PASS":"FAIL";report.error=ok?"":"Occupied crossing and/or stale observation reset regression failed; see individual measurements.";
            report.realSeconds=Time.realtimeSinceStartupAsDouble-began;Time.timeScale=1;Time.fixedDeltaTime=originalDt;Done=true;Debug.Log("GUARDIAN_PEDESTRIAN_REGRESSION "+report.status);
        }
    }
}
