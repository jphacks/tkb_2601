using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed class GuardianRotationContactProbe : MonoBehaviour
    {
        public Collider Target;public int Count;
        private void OnCollisionEnter(Collision c){if(c.collider==Target)Count++;}
        private void OnCollisionStay(Collision c){if(c.collider==Target)Count++;}
    }
    public sealed class GuardianRotationVerification : MonoBehaviour
    {
        [Serializable]public sealed class Case
        {
            public string kind,classification;public float dt;public int speed,contacts;public bool initialPhysicalOverlap,guardSeen,invalidStartDetected,passed;
            public float maxPenetration,peakAngularSpeed,peakDisplacement,peakYawChange;
        }
        [Serializable]public sealed class Report
        {
            public string status="FAIL",scope="Actual restaurant GuardianPhysicsDriver, 12mm pillar; invalid initial overlap is detection evidence, not safe operation",error="";
            public List<Case> cases=new List<Case>();public double realSeconds;
        }
        private readonly Report report=new Report();public bool Done {get;private set;}public bool Passed=>report.status=="PASS";public object ExportReport=>report;
        private GuardianInteractiveExperience experience;private GuardianSimulator sim;private Case active;private GuardianPhysicsDriver driver;private BoxCollider post;private GuardianRotationContactProbe probe;private Vector3 origin;private float originalYaw;private double began;
        private IEnumerator Start()
        {
            began=Time.realtimeSinceStartupAsDouble;experience=GetComponent<GuardianInteractiveExperience>();while(!experience.Ready)yield return null;sim=experience.Sim;experience.enabled=false;
            float oldDt=Time.fixedDeltaTime;Time.timeScale=8;sim.PhysicsStepBeginning+=Measure;
            foreach(float dt in new[]{.02f,.01f})foreach(int speed in new[]{1,2})foreach(string kind in new[]{"thin-inplace","thin-combined"})
            {BeginCase(dt,speed,kind);yield return new WaitForSeconds(3);EndCase();}
            foreach(float dt in new[]{.02f,.01f})foreach(string kind in new[]{"initial-overlap","outside-circle","initial-overlap-child","outside-circle-child"})
            {BeginCase(dt,2,kind);yield return new WaitForSeconds(3);EndCase();}
            ExtremeYawRange();
            Time.fixedDeltaTime=oldDt;Time.timeScale=1;sim.PhysicsStepBeginning-=Measure;
            bool ok=report.cases.TrueForAll(c=>c.passed);report.status=ok?"PASS":"FAIL";report.error=ok?"":"See per-case rotation contact/invalid-start classification";report.realSeconds=Time.realtimeSinceStartupAsDouble-began;Done=true;Debug.Log("GUARDIAN_ROTATION_TEST "+report.status);
        }
        private void BeginCase(float dt,int speed,string kind)
        {
            Time.fixedDeltaTime=dt;sim.HumanExperience?.EndWithoutReset();sim.OperatorSupervisionEnabled=false;sim.AutomaticTasks=false;sim.Settings.RandomFaults=false;sim.Settings.Physical.Enabled=true;sim.Settings.MotionSpeedMultiplier=speed;sim.ResetScenario();
            foreach(var r in sim.Robots){sim.SetCondition(r.RobotId,RobotCondition.Normal);sim.SetDispatchEnabled(r,false);r.GetComponent<GuardianMotionInterlock>().ResetForScenario(0);}
            experience.Inputs.Initialize(sim);experience.Person.ResetPedestrian();experience.Safety.ResetObservations();experience.Safety.Evaluate(true);
            driver=sim.Robots.Single(r=>r.RobotId=="A").PhysicsDriver;driver.ResetTo(new Vector3(-1,driver.PlaneY,0));origin=driver.Body.position;originalYaw=kind=="thin-combined"?60:0;driver.Body.rotation=Quaternion.Euler(0,originalYaw,0);
            Vector3 corner=new Vector3(driver.Shape.center.x+driver.Shape.size.x*.5f,0,driver.Shape.center.z+driver.Shape.size.z*.5f),p;
            if(kind=="thin-inplace")p=Quaternion.Euler(0,20,0)*corner;
            else if(kind=="thin-combined")p=Quaternion.Euler(0,90,0)*corner+Vector3.right*.18f;
            else if(kind.StartsWith("outside-circle"))p=new Vector3(driver.TurningRadius+.25f,0,driver.TurningRadius+.25f);
            else p=Vector3.zero;
            var go=new GameObject("Original 12mm rotation pillar");go.transform.position=origin+p+Vector3.up*1.1f;post=go.AddComponent<BoxCollider>();post.size=new Vector3(.012f,2.4f,.012f);
            if(kind.EndsWith("-child"))
            {var root=new GameObject("Offset compound pillar root");root.transform.position=kind.StartsWith("initial")?origin+new Vector3(12,0,7):origin+Vector3.up*1.1f;var rb=root.AddComponent<Rigidbody>();rb.isKinematic=true;rb.useGravity=false;go.transform.SetParent(root.transform,true);}
            probe=driver.GetComponent<GuardianRotationContactProbe>()??driver.gameObject.AddComponent<GuardianRotationContactProbe>();probe.Target=post;probe.Count=0;
            Physics.SyncTransforms();active=new Case{dt=dt,speed=speed,kind=kind,classification=kind.StartsWith("initial-overlap")?"invalid-start detection":kind.StartsWith("outside-circle")?"clear-path motion":"collision-prevention"};
            active.initialPhysicalOverlap=Physics.ComputePenetration(driver.Shape,driver.Body.position,driver.Body.rotation,post,post.transform.position,post.transform.rotation,out var axis,out float depth);report.cases.Add(active);
            driver.DriveTo(new Vector3(3.6f,driver.PlaneY,0));
        }
        private void Measure(float clock)
        {
            if(active==null)return;
            var prop=driver.GetType().GetProperty("RotationGuardIntervened");active.guardSeen|=prop!=null && (bool)prop.GetValue(driver);
            var invalid=driver.GetType().GetProperty("RotationInitialOverlap");active.invalidStartDetected|=invalid!=null && (bool)invalid.GetValue(driver);
            if(Physics.ComputePenetration(driver.Shape,driver.Body.position,driver.Body.rotation,post,post.transform.position,post.transform.rotation,out var axis,out float depth))active.maxPenetration=Mathf.Max(active.maxPenetration,depth);
            active.peakAngularSpeed=Mathf.Max(active.peakAngularSpeed,Mathf.Abs(driver.Body.angularVelocity.y));active.peakDisplacement=Mathf.Max(active.peakDisplacement,Vector3.Distance(driver.Body.position,origin));active.peakYawChange=Mathf.Max(active.peakYawChange,Mathf.Abs(Mathf.DeltaAngle(originalYaw,driver.Body.rotation.eulerAngles.y)));
        }
        private void EndCase()
        {
            Measure(sim.DemoClock);active.contacts=probe.Count;
            if(active.kind.StartsWith("initial-overlap"))active.passed=active.initialPhysicalOverlap && active.invalidStartDetected && active.guardSeen;
            else if(active.kind.StartsWith("outside-circle"))active.passed=!active.initialPhysicalOverlap && !active.invalidStartDetected && active.contacts==0 && active.maxPenetration==0 && active.peakYawChange>60 && active.peakDisplacement>.2f;
            else active.passed=!active.initialPhysicalOverlap && active.guardSeen && active.contacts==0 && active.maxPenetration==0;
            active=null;var root=post.attachedRigidbody!=null?post.attachedRigidbody.gameObject:post.gameObject;root.SetActive(false);Destroy(root);
        }
        private void ExtremeYawRange()
        {
            // Positive finite parameters can still exceed int's interval count.
            // Exercise the production envelope without advancing this invalid state.
            driver.ResetTo(origin);var settings=driver.Dynamics.Assumptions;float torque=settings.YawTorque,acceleration=settings.MaximumYawAcceleration;
            try
            {
                settings.YawTorque=settings.MaximumYawAcceleration=1e24f;driver.Body.angularVelocity=Vector3.up*1e10f;
                float actual=driver.Body.angularVelocity.y;var watch=System.Diagnostics.Stopwatch.StartNew();var envelope=new GuardianRotationEnvelope();bool clear=envelope.Clear(driver,Vector3.zero,0,.02f);watch.Stop();
                report.cases.Add(new Case{kind="extreme-yaw-range",classification="unsupported forecast fail-closed; NOT physical operation",dt=.02f,peakAngularSpeed=actual,guardSeen=!clear,passed=actual>1e9f&&!clear&&!envelope.InitialOverlap&&watch.Elapsed.TotalSeconds<1});
            }
            finally {settings.YawTorque=torque;settings.MaximumYawAcceleration=acceleration;driver.ResetTo(origin);}
        }
        private void OnDestroy(){if(sim!=null)sim.PhysicsStepBeginning-=Measure;}
    }
}
