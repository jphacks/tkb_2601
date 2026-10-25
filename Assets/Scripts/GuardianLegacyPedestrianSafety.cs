using System;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public struct GuardianLegacyPedestrianRisk
    {
        public float TimeToClosest;
        public float ClosestDistance;
        public float RequiredDistance;
        public float StoppingDistance;
        public bool Hazard;
    }

    /// <summary>One public-demo pedestrian. This is simulated motion, not a real sensor input.</summary>
    public sealed class GuardianLegacyPedestrianMotion : MonoBehaviour
    {
        public const float Radius=.40f;
        public Rigidbody Body { get; private set; }
        public CapsuleCollider Shape { get; private set; }
        public Vector3 Velocity { get; private set; }
        public bool Moving { get; private set; }
        public bool WaitingForClearance { get; private set; }
        public Vector3 StartPoint { get; private set; }
        public Vector3 EndPoint { get; private set; }

        private readonly RaycastHit[] hits=new RaycastHit[32];
        private bool atEnd;
        private float speed;

        public void Initialize(Vector3 start,Vector3 end,float metresPerSecond)
        {
            StartPoint=start; EndPoint=end; speed=metresPerSecond;
            Body=GetComponent<Rigidbody>()??gameObject.AddComponent<Rigidbody>();
            Body.isKinematic=true; Body.useGravity=false; Body.collisionDetectionMode=CollisionDetectionMode.ContinuousSpeculative;
            Shape=GetComponent<CapsuleCollider>()??gameObject.AddComponent<CapsuleCollider>();
            Shape.radius=Radius; Shape.height=1.83f; Shape.center=Vector3.up*.93f; Shape.enabled=true;
            Body.detectCollisions=true; ResetMotion(); Physics.SyncTransforms();
        }

        public void ResetMotion()
        {
            atEnd=false; Moving=false; WaitingForClearance=false; Velocity=Vector3.zero;
            transform.SetPositionAndRotation(StartPoint,Quaternion.Euler(0,90,0));
            Body.position=StartPoint; Body.rotation=transform.rotation;
        }

        public void BeginCrossing() { Moving=true; WaitingForClearance=false; }

        public bool Step(float dt)
        {
            if (!Moving || dt<=0) { Velocity=Vector3.zero; return false; }
            Vector3 target=atEnd?StartPoint:EndPoint;
            Vector3 delta=target-Body.position; delta.y=0;
            if (delta.magnitude<=.02f)
            {
                Body.MovePosition(target); atEnd=!atEnd; Moving=false; WaitingForClearance=false; Velocity=Vector3.zero;
                return true;
            }

            Velocity=delta.normalized*Mathf.Min(speed,delta.magnitude/dt);
            if (Blocked(Velocity,dt)) { Velocity=Vector3.zero; WaitingForClearance=true; return false; }
            WaitingForClearance=false;
            Body.MoveRotation(Quaternion.LookRotation(Velocity,Vector3.up));
            Body.MovePosition(Body.position+Velocity*dt);
            return false;
        }

        public void Disable(Vector3 originalPosition,Quaternion originalRotation)
        {
            Moving=false; WaitingForClearance=false; Velocity=Vector3.zero;
            if (Shape!=null) Shape.enabled=false;
            if (Body!=null) { Body.detectCollisions=true; Body.position=originalPosition; Body.rotation=originalRotation; }
            transform.SetPositionAndRotation(originalPosition,originalRotation);
            Physics.SyncTransforms();
        }

        private bool Blocked(Vector3 velocity,float dt)
        {
            Vector3 center=Body.position+Body.rotation*Shape.center;
            Vector3 axis=Body.rotation*Vector3.up*(Shape.height*.5f-Shape.radius);
            int count=Physics.CapsuleCastNonAlloc(center-axis,center+axis,Shape.radius,velocity.normalized,hits,
                velocity.magnitude*dt+.08f,~0,QueryTriggerInteraction.Ignore);
            if (count==hits.Length) return true;
            for (int index=0;index<count;index++)
            {
                Collider other=hits[index].collider;
                if (other==null || other.attachedRigidbody==Body || other.transform.IsChildOf(transform)) continue;
                if (other.GetComponent<GuardianPhysicsObstacle>()?.Floor==true) continue;
                // The robot owns the conservative yield decision. Avoid mutual wait in the public demo.
                if (other.GetComponentInParent<GuardianRobot>()!=null) continue;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Public-demo crossing coordinator. It uses simulated ground truth and must not be described
    /// as a certified safety controller or a substitute for real sensing and independent stopping.
    /// </summary>
    public sealed class GuardianLegacyPedestrianSystem : MonoBehaviour
    {
        private GuardianSimulator sim;
        private RestaurantPerson person;
        private RestaurantFixture fixture;
        private GuardianLegacyPedestrianMotion motion;
        private Vector3 originalPosition;
        private Quaternion originalRotation;
        private bool originalBlocksRoutes;
        private bool initialized,active;
        private float nextCrossing;
        private float[] lastHazard=Array.Empty<float>();

        public GuardianLegacyPedestrianRisk[] Risks { get; private set; }=Array.Empty<GuardianLegacyPedestrianRisk>();
        public int HoldTransitions { get; private set; }
        public bool Active=>active;
        public string ObservationSource=>"SIMULATED ground truth; NOT a real pedestrian sensor";
        public GuardianLegacyPedestrianMotion Pedestrian=>motion;

        public bool Configure(GuardianSimulator simulator,bool enabled)
        {
            sim=simulator; active=enabled; HoldTransitions=0;
            if (!active)
            {
                bool changed=initialized && fixture.BlocksRoutes!=originalBlocksRoutes;
                if (initialized)
                {
                    fixture.BlocksRoutes=originalBlocksRoutes;
                    motion?.Disable(originalPosition,originalRotation);
                }
                ClearRobotHolds(); return changed;
            }

            Transform environment=sim.transform.Find("Generated 3D World/"+RestaurantVisuals.RootName);
            if (environment==null) throw new InvalidOperationException("Pedestrian demo requires the restaurant environment");
            person=environment.GetComponentsInChildren<RestaurantPerson>(true).SingleOrDefault(p=>p.Standing);
            if (person==null) throw new InvalidOperationException("Pedestrian demo requires one standing staff model");
            fixture=person.GetComponent<RestaurantFixture>();
            if (fixture==null) throw new InvalidOperationException("Pedestrian demo staff is missing its route fixture");
            if (!initialized)
            {
                originalPosition=person.transform.position; originalRotation=person.transform.rotation;
                originalBlocksRoutes=fixture.BlocksRoutes; initialized=true;
            }

            bool topologyChanged=fixture.BlocksRoutes;
            fixture.BlocksRoutes=false;
            motion=person.GetComponent<GuardianLegacyPedestrianMotion>()??person.gameObject.AddComponent<GuardianLegacyPedestrianMotion>();
            motion.Initialize(new Vector3(-3.2f,originalPosition.y,-1.9f),new Vector3(2.2f,originalPosition.y,-1.9f),sim.Settings.PedestrianSpeed);
            nextCrossing=sim.DemoClock+sim.Settings.PedestrianFirstCrossing;
            EnsureRiskArrays(); ClearRobotHolds();
            return topologyChanged;
        }

        public void Tick(float dt)
        {
            if (!active || motion==null) return;
            if (!motion.Moving && sim.DemoClock>=nextCrossing) motion.BeginCrossing();
            if (motion.Step(dt)) nextCrossing=sim.DemoClock+sim.Settings.PedestrianCrossingInterval;
            Evaluate(dt);
        }

        private void Evaluate(float dt)
        {
            EnsureRiskArrays();
            for (int index=0;index<sim.Robots.Count;index++)
            {
                GuardianPhysicsDriver driver=sim.Robots[index].PhysicsDriver;
                float radius=.5f*Mathf.Sqrt(driver.Shape.size.x*driver.Shape.size.x+driver.Shape.size.z*driver.Shape.size.z);
                float required=radius+GuardianLegacyPedestrianMotion.Radius+sim.Settings.PedestrianSafetyMargin;
                GuardianLegacyPedestrianRisk actual=Predict(driver.Body.position,driver.Body.linearVelocity,motion.Body.position,motion.Velocity,
                    required,sim.Settings.MotorAcceleration,dt);
                GuardianLegacyPedestrianRisk intended=Predict(driver.Body.position,driver.SafetyIntent,motion.Body.position,motion.Velocity,
                    required,sim.Settings.MotorAcceleration,dt);
                bool hazard=actual.Hazard||intended.Hazard;
                GuardianLegacyPedestrianRisk selected=intended.ClosestDistance<actual.ClosestDistance?intended:actual;
                selected.Hazard=hazard; Risks[index]=selected;
                if (hazard) lastHazard[index]=sim.DemoClock;
                bool hold=hazard || sim.DemoClock-lastHazard[index]<sim.Settings.PedestrianClearSeconds;
                if (hold && !driver.PedestrianSafetyHold) HoldTransitions++;
                driver.SetPedestrianSafetyHold(hold);
            }
        }

        private void EnsureRiskArrays()
        {
            if (Risks.Length==sim.Robots.Count) return;
            Risks=new GuardianLegacyPedestrianRisk[sim.Robots.Count];
            lastHazard=Enumerable.Repeat(float.NegativeInfinity,sim.Robots.Count).ToArray();
        }

        private void ClearRobotHolds()
        {
            if (sim==null) return;
            foreach (GuardianRobot robot in sim.Robots) robot.PhysicsDriver.SetPedestrianSafetyHold(false);
            Risks=Array.Empty<GuardianLegacyPedestrianRisk>(); lastHazard=Array.Empty<float>();
        }

        public static GuardianLegacyPedestrianRisk Predict(Vector3 robot,Vector3 robotVelocity,Vector3 pedestrian,
            Vector3 pedestrianVelocity,float required,float acceleration,float dt)
        {
            if (!Finite(robot)||!Finite(robotVelocity)||!Finite(pedestrian)||!Finite(pedestrianVelocity)
                || !Finite(required)||!Finite(acceleration)||!Finite(dt)||required<0||acceleration<=0||dt<=0)
                return new GuardianLegacyPedestrianRisk{Hazard=true,ClosestDistance=0,
                    RequiredDistance=Finite(required)?Mathf.Max(0,required):0,StoppingDistance=float.MaxValue};
            Vector2 relativePosition=new Vector2(pedestrian.x-robot.x,pedestrian.z-robot.z);
            Vector2 relativeVelocity=new Vector2(pedestrianVelocity.x-robotVelocity.x,pedestrianVelocity.z-robotVelocity.z);
            float speed=new Vector2(robotVelocity.x,robotVelocity.z).magnitude;
            float stopping=speed*speed/(2*acceleration)+speed*dt+.20f;
            float time=relativeVelocity.sqrMagnitude>.00001f
                ?Mathf.Clamp(-Vector2.Dot(relativePosition,relativeVelocity)/relativeVelocity.sqrMagnitude,0,2.5f):0;
            float closest=(relativePosition+relativeVelocity*time).magnitude;
            bool closing=Vector2.Dot(relativePosition,relativeVelocity)<0;
            bool hazard=relativePosition.magnitude<required ||
                (closing && closest<required && time<=Mathf.Max(1.8f,speed/acceleration+dt));
            return new GuardianLegacyPedestrianRisk{TimeToClosest=time,ClosestDistance=closest,RequiredDistance=required,
                StoppingDistance=stopping,Hazard=hazard};
        }

        private static bool Finite(Vector3 value)=>Finite(value.x)&&Finite(value.y)&&Finite(value.z);
        private static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
    }
}
