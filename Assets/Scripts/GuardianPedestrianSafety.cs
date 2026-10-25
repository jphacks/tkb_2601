using System;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed class GuardianPedestrianMotion : MonoBehaviour
    {
        public const float Radius=.40f;
        public Rigidbody Body {get;private set;}public CapsuleCollider Shape {get;private set;}
        public Vector3 Velocity {get;private set;}public bool Crossing {get;private set;}
        public bool Retreating {get;private set;}
        public bool WaitingForClearance {get;private set;}public string WaitReason {get;private set;}="";
        public Vector3 StartPoint=new Vector3(-2.8f,0,-1.9f),EndPoint=new Vector3(2.2f,0,-1.9f);
        private RiggedRestaurantGuest guest;
        private readonly GuardianQueryBuffer query=new GuardianQueryBuffer();
        private const float Clearance=.15f;
        public void Initialize()
        {
            guest=GetComponent<RiggedRestaurantGuest>();Body=gameObject.AddComponent<Rigidbody>();Body.isKinematic=true;Body.useGravity=false;Body.collisionDetectionMode=CollisionDetectionMode.ContinuousSpeculative;
            Shape=gameObject.AddComponent<CapsuleCollider>();Shape.radius=Radius;Shape.height=1.83f;Shape.center=Vector3.up*.93f;ResetPedestrian();
        }
        public void ResetPedestrian() {Crossing=false;Retreating=false;WaitingForClearance=false;WaitReason="";Velocity=Vector3.zero;transform.SetPositionAndRotation(StartPoint,Quaternion.Euler(0,90,0));Body.position=StartPoint;Body.rotation=transform.rotation;guest.Play(GuestPose.Idle);}
        public void Walk() {Crossing=true;Retreating=false;WaitingForClearance=false;WaitReason="";guest.Play(GuestPose.Walk);}
        public void Retreat() {Crossing=true;Retreating=true;WaitingForClearance=false;WaitReason="";guest.Play(GuestPose.Walk);}
        public void Wait() {Crossing=false;Retreating=false;WaitingForClearance=false;WaitReason="";Velocity=Vector3.zero;guest.Play(GuestPose.Waiting);}
        public void Step(float dt)
        {
            if(!Crossing || dt<=0){Velocity=Vector3.zero;return;}
            Vector3 delta=(Retreating?StartPoint:EndPoint)-Body.position;delta.y=0;float distance=delta.magnitude;
            Velocity=distance>.01f?delta.normalized*Mathf.Min(.8f,distance/dt):Vector3.zero;
            MoveChecked(Velocity,dt);
            if(distance<.01f)Wait();
        }
        public void MoveAlongRoute(Vector3 velocity,float yaw,float dt)
        {
            if(dt<=0)return;
            if(!Crossing)Walk();
            Body.MoveRotation(Quaternion.RotateTowards(Body.rotation,Quaternion.Euler(0,yaw,0),180*dt));
            MoveChecked(Vector3.ClampMagnitude(velocity,.8f),dt);
            var pose=Velocity.sqrMagnitude>.0001f?GuestPose.Walk:GuestPose.Waiting;
            if(guest.Pose!=pose)guest.Play(pose);
        }
        private void MoveChecked(Vector3 velocity,float dt)
        {
            Velocity=velocity;
            // A kinematic body is not stopped by contacts. Check its full upright
            // capsule, including the next fixed-step displacement and clearance,
            // before issuing MovePosition. Check already occupied space separately
            // rather than relying on a sweep to report initial overlap contacts.
            Vector3 center=Body.position+Body.rotation*Shape.center;
            Vector3 axis=Body.rotation*Vector3.up*(Shape.height*.5f-Shape.radius);
            bool blocked=false;WaitReason="";
            int count=query.OverlapCapsule(center-axis,center+axis,Shape.radius+Clearance);
            for(int i=0;i<count;i++)if(Blocks(query.Colliders[i])){blocked=true;WaitReason=query.Colliders[i].name;break;}
            if(!blocked && Velocity.sqrMagnitude>.00001f)
            {
                count=query.Capsule(center-axis,center+axis,Shape.radius,Velocity.normalized,Velocity.magnitude*dt+Clearance);
                for(int i=0;i<count;i++)if(Blocks(query.Hits[i].collider)){blocked=true;WaitReason=query.Hits[i].collider.name;break;}
            }
            if(blocked){Velocity=Vector3.zero;if(!WaitingForClearance)guest.Play(GuestPose.Waiting);WaitingForClearance=true;return;}
            if(WaitingForClearance)guest.Play(GuestPose.Walk);WaitingForClearance=false;
            if(Velocity.sqrMagnitude>.00001f)Body.MoveRotation(Quaternion.LookRotation(Velocity,Vector3.up));
            Body.MovePosition(Body.position+Velocity*dt);
        }
        private bool Blocks(Collider other)
        {return other!=null && other.attachedRigidbody!=Body && !other.transform.IsChildOf(transform) && other.GetComponent<GuardianPhysicsObstacle>()?.Floor!=true;}
    }
    public struct GuardianPedestrianRisk {public float Ttc,ClosestDistance,RequiredDistance,StoppingDistance;public bool Hazard,Observed,Tracked;}
    public sealed class GuardianPedestrianSafety
    {
        private GuardianSimulator sim;private GuardianPedestrianMotion person;private readonly GuardianQueryBuffer query=new GuardianQueryBuffer();
        public Vector3 Observer=new Vector3(0,7,-5);private Vector3 seenPosition,seenVelocity;private float seenAt=float.NegativeInfinity;
        private int observationSteps;
        private readonly GuardianSafetyBroadphase broadphase=new GuardianSafetyBroadphase();private GuardianSafetyAgent[] agents=Array.Empty<GuardianSafetyAgent>();private bool[] candidates=Array.Empty<bool>();
        public int CandidatePairs {get;private set;}public int PossiblePairs=>sim.Robots.Count;public int BroadphaseFallbacks=>broadphase.Fallbacks;
        public bool Observed {get;private set;}public bool Tracked {get {float age=sim.DemoClock-seenAt;return age>=0 && age<=.75f;}}
        public string ObservationSource=>"SIMULATED shared overhead LOS; NOT a real camera";
        public GuardianPedestrianRisk[] Risks {get;private set;}
        public int HoldTransitions {get;private set;}
        public void Initialize(GuardianSimulator simulator,GuardianPedestrianMotion pedestrian) {sim=simulator;person=pedestrian;Risks=new GuardianPedestrianRisk[sim.Robots.Count];ResetObservations();}
        public void ResetObservations()
        {
            seenAt=float.NegativeInfinity;observationSteps=0;seenPosition=seenVelocity=Vector3.zero;Observed=false;HoldTransitions=0;
            Array.Clear(Risks,0,Risks.Length);
            // BeginDemo, every fixed step and every ACK evaluate LOS before motion
            // or release. Resetting the clock itself supplies no new observation.
        }
        public void Evaluate(bool forceObservation=false)
        {
            if(forceObservation || --observationSteps<=0)
            {
            Vector3 to=person.Body.position+Vector3.up*.95f-Observer;int count=query.Ray(Observer,to.normalized,to.magnitude+.1f);float closest=float.PositiveInfinity;Collider first=null;
            for(int i=0;i<count;i++)if(query.Hits[i].distance<closest){closest=query.Hits[i].distance;first=query.Hits[i].collider;}
            Observed=first!=null && first.GetComponentInParent<GuardianPedestrianMotion>()==person;
            if(Observed){seenPosition=person.Body.position;seenVelocity=person.Velocity;seenAt=sim.DemoClock;}
            observationSteps=sim.Settings.Physical.Enabled?Mathf.Max(1,GuardianPlanarDynamics.QuantizedSteps(sim.Settings.Physical.SensorPeriod,Time.fixedDeltaTime)):1;
            }
            float age=sim.DemoClock-seenAt;
            int robots=sim.Robots.Count;
            if(agents.Length<robots+1){agents=new GuardianSafetyAgent[robots+1];candidates=new bool[robots];}
            if(Risks.Length!=robots)Risks=new GuardianPedestrianRisk[robots];Array.Clear(candidates,0,candidates.Length);CandidatePairs=0;
            if(Tracked)
            {
                for(int i=0;i<robots;i++){var d=sim.Robots[i].PhysicsDriver;agents[i]=new GuardianSafetyAgent{Robot=true,Position=d.Body.position,Velocity=d.PlanarVelocity,Intent=d.IntentVelocity,Radius=d.TurningRadius,MaximumSpeed=d.EffectiveMaximumSpeed,Uncertainty=.4f*age*age,Model=d.Dynamics};}
                agents[robots]=new GuardianSafetyAgent{Position=seenPosition+seenVelocity*age,Velocity=seenVelocity,Radius=GuardianPedestrianMotion.Radius};
                broadphase.Build(agents,robots+1,Time.fixedDeltaTime);CandidatePairs=broadphase.Pairs.Count;for(int j=0;j<broadphase.Pairs.Count;j++)candidates[broadphase.Pairs[j].Robot]=true;
            }
            for(int i=0;i<sim.Robots.Count;i++)
            {
                var r=sim.Robots[i];var driver=r.PhysicsDriver;var hold=r.GetComponent<GuardianMotionInterlock>();bool prior=hold.Held;
                hold.Set(GuardianMotionHold.Unobserved,!Tracked,sim.DemoClock);
                var risk=new GuardianPedestrianRisk{Observed=Observed,Tracked=Tracked,Ttc=float.PositiveInfinity};
                if(Tracked && candidates[i])
                {
                    Vector3 predictedPerson=seenPosition+seenVelocity*age;Vector3 v=driver.PlanarVelocity;
                    float uncertainty=.4f*age*age;
                    float required=driver.TurningRadius+GuardianPedestrianMotion.Radius+.20f+uncertainty;
                    risk=driver.Dynamics!=null?PredictPhysical(driver.Body.position,v,predictedPerson,seenVelocity,required,driver.Dynamics,driver.EffectiveMaximumSpeed,Time.fixedDeltaTime):Predict(driver.Body.position,v,predictedPerson,seenVelocity,required,driver.EffectiveAcceleration,Time.fixedDeltaTime);
                    GuardianPedestrianRisk intended=driver.Dynamics!=null?PredictPhysical(driver.Body.position,driver.IntentVelocity,predictedPerson,seenVelocity,required,driver.Dynamics,driver.EffectiveMaximumSpeed,Time.fixedDeltaTime):Predict(driver.Body.position,driver.IntentVelocity,predictedPerson,seenVelocity,required,driver.EffectiveAcceleration,Time.fixedDeltaTime);
                    bool anyHazard=risk.Hazard || intended.Hazard;
                    if(intended.Hazard || intended.ClosestDistance<risk.ClosestDistance)risk=intended;
                    risk.Hazard=anyHazard;
                    risk.Observed=Observed;risk.Tracked=true;
                    // Release needs more clearance than entry; one clear fixed step is not an acknowledgement.
                    bool near=Vector2.Distance(new Vector2(predictedPerson.x,predictedPerson.z),new Vector2(driver.Body.position.x,driver.Body.position.z))<required+.15f;
                    hold.Set(GuardianMotionHold.Pedestrian,risk.Hazard || near,sim.DemoClock);
                }
                else hold.Set(GuardianMotionHold.Pedestrian,false,sim.DemoClock);
                Risks[i]=risk;if(!prior && hold.Held)HoldTransitions++;
            }
        }
        public static GuardianPedestrianRisk PredictPhysical(Vector3 robot,Vector3 velocity,Vector3 pedestrian,Vector3 pedestrianVelocity,float required,GuardianPlanarDynamics model,float speedCap,float dt)
        {
            if(!float.IsFinite(dt)||dt<=0||!float.IsFinite(required)||!float.IsFinite(speedCap)||!Finite(robot)||!Finite(velocity)||!Finite(pedestrian)||!Finite(pedestrianVelocity))return Unbounded(required);
            velocity.y=0;float speed=velocity.magnitude,cap=Mathf.Max(speed,speedCap),reaction=model.ReactionSeconds(dt);
            Vector3 direction=speed>.00001f?velocity/speed:Vector3.zero;
            if(direction==Vector3.zero)cap=0;
            float horizon=Mathf.Max(1.8f,reaction+cap/model.ConservativeBrakeAcceleration+.1f);
            // A range fallback must not enter an infinite/float-stalled narrow
            // rollout for near-zero braking. Unsupported forecasts fail closed.
            if(!float.IsFinite(horizon)||horizon/dt>4096||!float.IsFinite(speed))return Unbounded(required);
            Vector3 relative=pedestrian-robot;relative.y=0;float closest=relative.magnitude,at=0;
            for(float t=0;t<horizon;t+=dt)
            {
                float h=Mathf.Min(dt,horizon-t),next=t+1e-6f<reaction?Mathf.Min(cap,speed+model.DriveAcceleration*h):Mathf.Max(0,speed-model.ConservativeBrakeAcceleration*h);
                Vector3 step=pedestrianVelocity*h-direction*((speed+next)*.5f*h);step.y=0;
                float u=step.sqrMagnitude>.0000001f?Mathf.Clamp01(-Vector3.Dot(relative,step)/step.sqrMagnitude):0;
                float gap=(relative+step*u).magnitude;if(gap<closest){closest=gap;at=t+u*h;}relative+=step;speed=next;
            }
            return new GuardianPedestrianRisk{Ttc=at,ClosestDistance=closest,RequiredDistance=required,StoppingDistance=model.StoppingBound(velocity.magnitude,speedCap,dt),Hazard=closest<required};
        }
        private static bool Finite(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);
        private static GuardianPedestrianRisk Unbounded(float required)=>new GuardianPedestrianRisk{Hazard=true,Ttc=0,ClosestDistance=0,RequiredDistance=float.IsFinite(required)?required:0,StoppingDistance=float.MaxValue};
        public static GuardianPedestrianRisk Predict(Vector3 robot,Vector3 robotVelocity,Vector3 pedestrian,Vector3 pedestrianVelocity,float required,float acceleration,float dt)
        {
            Vector2 p=new Vector2(pedestrian.x-robot.x,pedestrian.z-robot.z),v=new Vector2(pedestrianVelocity.x-robotVelocity.x,pedestrianVelocity.z-robotVelocity.z);
            float speed=new Vector2(robotVelocity.x,robotVelocity.z).magnitude,stop=speed*speed/(2*Mathf.Max(.1f,acceleration))+speed*dt+.20f;
            float t=v.sqrMagnitude>.00001f?Mathf.Clamp(-Vector2.Dot(p,v)/v.sqrMagnitude,0,2.5f):0;
            float separation=(p+v*t).magnitude;
            bool closing=Vector2.Dot(p,v)<0;
            return new GuardianPedestrianRisk{Ttc=t,ClosestDistance=separation,RequiredDistance=required,StoppingDistance=stop,
                Hazard=p.magnitude<required || (closing && separation<required && t<=Mathf.Max(1.8f,speed/Mathf.Max(.1f,acceleration)+dt))};
        }
    }
}
