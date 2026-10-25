using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Guardian.UnitySim
{
    public sealed class GuardianDynamicsContactProbe : MonoBehaviour
    {public int Contacts;private void OnCollisionEnter(Collision c){Contacts++;}private void OnCollisionStay(Collision c){Contacts++;}}

    /// <summary>Production force actuator + real isolated PhysX scene, not equation-only tests.</summary>
    public sealed class GuardianDynamicsVerification : MonoBehaviour
    {
        [Serializable]public sealed class Case
        {
            public string load,motion,input,phase,validationClass;public float dt,warmupSeconds;public int speed,steps,contacts;
            public bool riskExceeded,actualDecelerationObserved;public float observedDecelerationSeconds;
            public string latencyMeaning="SafetyBrakeActive command flag and separately observed Rigidbody deceleration; not independent perception-to-brake timing";
            public float mass,comX,comY,comZ,inertiaYaw,cruiseSpeed,cruiseOmega,requestedSpeed;
            public float reactionSeconds,measuredBrakeLatency,stopBound,actualStopTravel,boundError,minLeadingClearance=float.MaxValue,maxPenetration;
            public float maxForce,maxTorque,maxAcceleration,maxLateralAcceleration,maxPointAcceleration,maxSupportUtilization,maxSlideUtilization,maxItemTipUtilization;
            public bool passed;
        }
        [Serializable]public sealed class Report
        {
            public string status="FAIL",error="",scope="Real Rigidbody forces in an isolated manually stepped Unity PhysX scene; synthetic lumped payloads, not identified hardware or rigid/liquid cargo";
            public string unity;public double realSeconds,simulatedSeconds;public int contactPositiveControl;
            public bool fractionalFixedStepQuantization;
            public GuardianPhysicalSettings assumptions=new GuardianPhysicalSettings{Enabled=true};public List<Case> cases=new List<Case>();
        }
        public bool Done {get;private set;}public bool Passed=>report.status=="PASS";public object ExportReport=>report;
        private readonly Report report=new Report();private PhysicsScene physics;private Scene scene;private double began;
        private IEnumerator Start()
        {
            began=Time.realtimeSinceStartupAsDouble;report.unity=Application.unityVersion;
            scene=SceneManager.CreateScene("Guardian physical matrix",new CreateSceneParameters(LocalPhysicsMode.Physics3D));physics=scene.GetPhysicsScene();
            foreach(float dt in new[]{.02f,.01f})foreach(int speed in new[]{1,2})foreach(string load in new[]{"empty","center","high","offset"})foreach(string motion in new[]{"straight","turn","pivot"})
            {
                try{RunCase(dt,speed,load,motion,false);}catch(Exception ex){Finish(ex);yield break;}yield return null;
            }
            foreach(string load in new[]{"empty","center","high","offset"})
            {try{RunCase(.02f,2,load,"straight",true);}catch(Exception ex){Finish(ex);yield break;}yield return null;}
            foreach(float dt in new[]{.02f,.01f})foreach(string load in new[]{"empty","center","high","offset"})
            {try{RunCase(dt,2,load,"straight",false,.2f);}catch(Exception ex){Finish(ex);yield break;}yield return null;}
            try
            {
                report.fractionalFixedStepQuantization=GuardianPlanarDynamics.QuantizedSteps(.08f,.01999999f)==4 && GuardianPlanarDynamics.QuantizedSteps(.04f,.01999999f)==2;
                Check(report.fractionalFixedStepQuantization,"Fractional fixed-step rounding added a phantom delay step");
                // Deliberately overlapped initialization validates native contact callbacks.
                var fixture=CreateBody(out var box,out var probe);var wall=CreateWall(Vector3.forward*.42f,Vector3.forward);Physics.SyncTransforms();
                for(int n=0;n<5;n++)physics.Simulate(.02f);report.contactPositiveControl=probe.Contacts;
                DestroyImmediate(fixture.gameObject);DestroyImmediate(wall.gameObject);Check(report.contactPositiveControl>0,"Contact callback positive control did not fire");
                foreach(var c in report.cases)Check(c.passed,"Matrix row failed: "+c.load+" "+c.motion+" dt="+c.dt+" speed="+c.speed);
                var central=report.cases.Find(c=>c.dt==.02f&&c.speed==2&&c.load=="center"&&c.motion=="straight"&&c.input=="delayed");
                var high=report.cases.Find(c=>c.dt==.02f&&c.speed==2&&c.load=="high"&&c.motion=="straight"&&c.input=="delayed");
                var offset=report.cases.Find(c=>c.dt==.02f&&c.speed==2&&c.load=="offset"&&c.motion=="pivot");
                var zeroDelay=report.cases.Find(c=>c.load=="empty"&&c.input=="zero-driver-delay");var delayed=report.cases.Find(c=>c.dt==.02f&&c.speed==2&&c.load=="empty"&&c.motion=="straight"&&c.input=="delayed");
                Check(high.maxSupportUtilization>central.maxSupportUtilization+.1f,"High load did not change support risk");
                Check(offset.maxPointAcceleration>.1f && offset.cruiseSpeed<.05f,"In-place offset load rotational acceleration missing");
                Check(delayed.actualStopTravel>zeroDelay.actualStopTravel+.1f,"Delay did not increase physical stopping travel");Finish(null);
            }
            catch(Exception ex){Finish(ex);}
        }
        private void RunCase(float dt,int speed,string load,string motion,bool zeroDelay,float warmup=4)
        {
            var p=new GuardianPhysicalSettings{Enabled=true};if(zeroDelay){p.SensorPeriod=0;p.DriverResponseDelay=0;}
            Rigidbody body=CreateBody(out var box,out var probe);var model=new GuardianPlanarDynamics(body,box.size,p);
            var payload=new List<GuardianPayloadPoint>();
            if(load!="empty")
            {
                Vector3 center=load=="center"?new Vector3(0,.65f,0):load=="high"?new Vector3(0,1.65f,0):new Vector3(.24f,1.2f,-.20f);
                var item=new GuardianPayloadPoint(12,center,p);if(load=="high")item.CenterHeight=.30f;payload.Add(item);
            }
            model.SetPayload(payload);var c=new Case{load=load,motion=motion,input=zeroDelay?"zero-driver-delay":"delayed",phase=warmup<1?"accelerating":"steady",warmupSeconds=warmup,dt=dt,speed=speed,mass=model.Mass,comX=model.Center.x,comY=model.Center.y,comZ=model.Center.z,inertiaYaw=model.Inertia.y};report.cases.Add(c);
            float omega=motion=="turn"?.8f:motion=="pivot"?2:0;c.requestedSpeed=motion=="pivot"?0:1.9f*speed;
            for(int n=0;n<Mathf.RoundToInt(warmup/dt);n++)Step(model,physics,Command(body,motion,c.requestedSpeed),omega,dt,false,c);
            c.cruiseSpeed=model.RootVelocity.magnitude;c.cruiseOmega=body.angularVelocity.y;c.reactionSeconds=model.ReactionSeconds(dt);c.stopBound=model.StoppingBound(c.cruiseSpeed,c.requestedSpeed,dt);
            Vector3 forward=model.RootVelocity.sqrMagnitude>.0001f?model.RootVelocity.normalized:Vector3.forward;
            float extent=motion=="straight"?Support(box,body.rotation,forward):new Vector2(Mathf.Abs(box.center.x)+box.size.x*.5f,Mathf.Abs(box.center.z)+box.size.z*.5f).magnitude;
            Vector3 origin=body.position;BoxCollider wall=CreateWall(origin+forward*(extent+c.stopBound),forward);Physics.SyncTransforms();
            int wait=GuardianPlanarDynamics.QuantizedSteps(p.SensorPeriod,dt)+1,quiet=0;bool brakeSeen=false;float travelled=0;
            for(int n=0;n<Mathf.CeilToInt(8/dt);n++)
            {
                Vector3 prior=body.position;bool stop=n>=wait;
                float before=model.RootVelocity.magnitude;
                Step(model,physics,stop?Vector3.zero:Command(body,motion,c.requestedSpeed),stop?0:omega,dt,stop,c);
                if(model.SafetyBrakeActive && !brakeSeen){c.measuredBrakeLatency=n*dt;brakeSeen=true;}
                if(stop && !c.actualDecelerationObserved && model.RootVelocity.magnitude<before-.001f){c.actualDecelerationObserved=true;c.observedDecelerationSeconds=(n+1)*dt;}
                travelled+=Vector3.Distance(prior,body.position);
                float gap=Vector3.Dot(origin+forward*(extent+c.stopBound)-body.position,forward)-Support(box,body.rotation,forward);c.minLeadingClearance=Mathf.Min(c.minLeadingClearance,gap);
                if(Physics.ComputePenetration(box,body.position,body.rotation,wall,wall.transform.position,wall.transform.rotation,out var axis,out float depth))c.maxPenetration=Mathf.Max(c.maxPenetration,depth);
                if(stop && model.RootVelocity.magnitude<.005f && body.angularVelocity.magnitude<.005f)quiet++;else quiet=0;
                if(quiet>=5)break;
            }
            c.actualStopTravel=travelled;c.boundError=travelled-c.stopBound;c.contacts=probe.Contacts;
            bool cruise=warmup<1?c.cruiseSpeed>.2f && c.cruiseSpeed<c.requestedSpeed*.8f:motion=="straight"?Mathf.Abs(c.cruiseSpeed-c.requestedSpeed)<.02f:motion=="turn"?c.cruiseSpeed>1.5f && c.cruiseOmega>.7f:c.cruiseSpeed<.05f && c.cruiseOmega>1.9f;
            c.passed=quiet>=5 && cruise && c.contacts==0 && c.maxPenetration==0 && c.minLeadingClearance>.01f && c.boundError<=.001f && c.maxForce<=Mathf.Max(p.DriveForce,p.BrakeForce)+.1f && c.maxTorque<=p.YawTorque+.01f && Mathf.Abs(c.measuredBrakeLatency-c.reactionSeconds)<=dt+.001f;
            c.riskExceeded=c.maxSupportUtilization>1 || c.maxSlideUtilization>1 || c.maxItemTipUtilization>1;
            c.validationClass=c.riskExceeded?"mechanics + risk exceedance detected; NOT safe loaded operation":"mechanics within tested indicator thresholds; NOT hardware safety";
            DestroyImmediate(body.gameObject);DestroyImmediate(wall.gameObject);
        }
        private void Step(GuardianPlanarDynamics model,PhysicsScene world,Vector3 desired,float omega,float dt,bool stop,Case c)
        {
            model.Apply(desired,omega,dt,stop);world.Simulate(dt);model.Observe(dt);var r=model.Risk;
            c.steps++;report.simulatedSeconds+=dt;c.maxForce=Mathf.Max(c.maxForce,model.LastForce.magnitude);c.maxTorque=Mathf.Max(c.maxTorque,Mathf.Abs(model.LastYawTorque));
            c.maxAcceleration=Mathf.Max(c.maxAcceleration,r.Acceleration);c.maxLateralAcceleration=Mathf.Max(c.maxLateralAcceleration,r.LateralAcceleration);c.maxPointAcceleration=Mathf.Max(c.maxPointAcceleration,r.PayloadPointAcceleration);
            c.maxSupportUtilization=Mathf.Max(c.maxSupportUtilization,r.SupportUtilization);c.maxSlideUtilization=Mathf.Max(c.maxSlideUtilization,r.SlideUtilization);c.maxItemTipUtilization=Mathf.Max(c.maxItemTipUtilization,r.ItemTipUtilization);
        }
        private static Vector3 Command(Rigidbody body,string motion,float speed)=>(motion=="turn"?body.rotation*Vector3.forward:Vector3.forward)*speed;
        private Rigidbody CreateBody(out BoxCollider shape,out GuardianDynamicsContactProbe probe)
        {
            var go=new GameObject("Original physical test chassis");SceneManager.MoveGameObjectToScene(go,scene);
            var b=go.AddComponent<Rigidbody>();b.useGravity=false;b.constraints=RigidbodyConstraints.FreezePositionY|RigidbodyConstraints.FreezeRotationX|RigidbodyConstraints.FreezeRotationZ;b.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
            shape=go.AddComponent<BoxCollider>();shape.center=Vector3.up*.85f;shape.size=new Vector3(.9f,1.7f,.9f);probe=go.AddComponent<GuardianDynamicsContactProbe>();return b;
        }
        private BoxCollider CreateWall(Vector3 leadingSurface,Vector3 forward)
        {
            var go=new GameObject("Physical stopping boundary");SceneManager.MoveGameObjectToScene(go,scene);go.transform.SetPositionAndRotation(leadingSurface+forward*.025f+Vector3.up,Quaternion.LookRotation(forward));
            var c=go.AddComponent<BoxCollider>();c.size=new Vector3(10,3,.05f);return c;
        }
        private static float Support(BoxCollider shape,Quaternion rotation,Vector3 direction)
        {Vector3 d=Quaternion.Inverse(rotation)*direction;return Vector3.Dot(rotation*shape.center,direction)+Mathf.Abs(d.x)*shape.size.x*.5f+Mathf.Abs(d.z)*shape.size.z*.5f;}
        private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        private void Finish(Exception error){report.status=error==null?"PASS":"FAIL";report.error=error?.ToString()??"";report.realSeconds=Time.realtimeSinceStartupAsDouble-began;Done=true;Debug.Log("GUARDIAN_DYNAMICS_MATRIX "+report.status);if(error!=null)Debug.LogException(error);}
    }
}
