using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Local branch/dock claims; FIFO lease for the narrow central aisle only.</summary>
    public sealed class GuardianTrafficController : MonoBehaviour
    {
        private sealed class RequestEntry { public GuardianPhysicsDriver Driver; public float Ready; public long Order; }
        private readonly List<RequestEntry> requests=new List<RequestEntry>(),crossings=new List<RequestEntry>();
        private readonly Dictionary<string,GuardianPhysicsDriver> claims=new Dictionary<string,GuardianPhysicsDriver>();
        private readonly Dictionary<string,DeliveryTask> jobs=new Dictionary<string,DeliveryTask>();
        private readonly HashSet<GuardianPhysicsDriver> admitted=new HashSet<GuardianPhysicsDriver>();
        private readonly Dictionary<string,string> reservations=new Dictionary<string,string>();
        private long sequence;
        private System.Random departures;
        private GuardianSimulator sim;
        public GuardianPhysicsDriver Owner { get; private set; }
        public int Grants { get; private set; }
        public int QueueLength=>requests.Count+crossings.Count;
        public int JobClaimCount=>jobs.Count;
        public IReadOnlyDictionary<string,string> Reservations=>reservations;
        private static string Point(Vector3 p)=>p.x.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"/"+p.z.ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
        public static Vector3 Hold(Vector3 p)=>new Vector3(p.x< -1?-3:1,p.y,p.z);
        private static string Dock(Vector3 p)=>"dock/"+Point(p);
        private static string Branch(Vector3 p)=>"branch/"+Point(Hold(p));
        public void ResetTraffic(GuardianSimulator simulator,int seed)
        {
            sim=simulator; departures=new System.Random(seed^0x5142); requests.Clear(); crossings.Clear(); claims.Clear(); jobs.Clear(); admitted.Clear(); reservations.Clear(); Owner=null; sequence=0; Grants=0;
        }
        public bool IsAdmitted(GuardianPhysicsDriver d)=>admitted.Contains(d);
        public bool CanAssign(DeliveryTask task)=>new[]{task.Pickup,task.Destination}.All(p=>!jobs.TryGetValue(Dock(p),out var owner)||owner==task);
        public void ClaimJob(DeliveryTask task) { jobs[Dock(task.Pickup)]=task; jobs[Dock(task.Destination)]=task; }
        public void PickupCompleted(DeliveryTask task)
        { string key=Dock(task.Pickup); if (jobs.TryGetValue(key,out var owner) && owner==task) jobs.Remove(key); }
        public void ReleaseJob(DeliveryTask task) { foreach (string k in jobs.Where(p=>p.Value==task).Select(p=>p.Key).ToArray()) jobs.Remove(k); }
        public void ContinueServiceAtDestination(DeliveryTask delivery,DeliveryTask collection)
        {
            string key=Dock(collection.Pickup);
            if(key!=Dock(delivery.Destination) || (jobs.TryGetValue(key,out var owner) && owner!=delivery))
                throw new InvalidOperationException("Cannot transfer another job's occupied table dock");
            ReleaseJob(delivery); jobs[key]=collection;
        }
        public void Request(GuardianPhysicsDriver d)
        {
            if (Owner==d) throw new InvalidOperationException("Central owner must clear its protected exit before readmission");
            // Claims reset only outside the occupied crossing.
            // Retain the physically occupied source while acquiring the next leg.
            // Releasing it first could let another vehicle reserve an exit into it.
            foreach (string k in claims.Where(p=>p.Value==d && !TouchesSource(p.Key,d.Body.position)).Select(p=>p.Key).ToArray()) claims.Remove(k);
            admitted.Remove(d); CancelQueued(d);
            requests.Add(new RequestEntry{Driver=d,Ready=sim.DemoClock+(float)departures.NextDouble()*sim.Settings.MaximumDepartureDelay,Order=sequence++});
            d.WaitReason="departure / local branch or service point";
        }
        public void RequestCrossing(GuardianPhysicsDriver d)
        { if (Owner!=d && !crossings.Any(r=>r.Driver==d)) crossings.Add(new RequestEntry{Driver=d,Ready=sim.DemoClock,Order=sequence++}); }
        public void CancelQueued(GuardianPhysicsDriver d) { requests.RemoveAll(r=>r.Driver==d); crossings.RemoveAll(r=>r.Driver==d); }
        private void ReleaseLocal(GuardianPhysicsDriver d) { foreach (string k in claims.Where(p=>p.Value==d).Select(p=>p.Key).ToArray()) claims.Remove(k); }
        public void Release(GuardianPhysicsDriver d) { CancelQueued(d); ReleaseLocal(d); admitted.Remove(d); if (Owner==d) Owner=null; Refresh(); }
        public void ClearSource(GuardianPhysicsDriver d,Vector3 source)
        {
            // Source docking acceptance permits 75mm error; its rounded key may
            // differ from the original goal key. Clear spatially matching own
            // source claims, while preserving this leg's protected destination.
            foreach(string key in claims.Where(p=>p.Value==d && TouchesSource(p.Key,source)).Select(p=>p.Key).ToArray())
            {
                string future=key.StartsWith("branch/")?Branch(d.Destination):Dock(d.Destination);
                if(!OverlapsResource(key,future)) claims.Remove(key);
            }
        }
        public void ClearCentral(GuardianPhysicsDriver d) { if (Owner==d) Owner=null; Refresh(); }
        private static Vector2 ResourcePoint(string key)
        { string[] p=key.Split('/'); return new Vector2(float.Parse(p[1],System.Globalization.CultureInfo.InvariantCulture),float.Parse(p[2],System.Globalization.CultureInfo.InvariantCulture)); }
        private static bool OverlapsResource(string a,string b)
        {
            if (a.Split('/')[0]!=b.Split('/')[0]) return false;
            Vector2 pa=ResourcePoint(a),pb=ResourcePoint(b);
            return a.StartsWith("branch/")?Mathf.Abs(pa.x-pb.x)<.1f && Mathf.Abs(pa.y-pb.y)<1.35f:Vector2.Distance(pa,pb)<1.35f;
        }
        private static bool TouchesSource(string key,Vector3 position)
        { return key.StartsWith("branch/")?OverlapsResource(key,Branch(position)):Vector2.Distance(ResourcePoint(key),new Vector2(position.x,position.z))<.4f; }
        private GuardianPhysicsDriver Blocker(string key,GuardianPhysicsDriver d)=>claims.Where(p=>p.Value!=d && OverlapsResource(key,p.Key)).Select(p=>p.Value).FirstOrDefault();
        private bool Available(string key,GuardianPhysicsDriver d)=>Blocker(key,d)==null;
        private bool DestinationClear(GuardianPhysicsDriver d)
        {
            Vector3 goal=d.Destination;
            return sim.Robots.All(r=>r==d.Robot || Vector2.Distance(new Vector2(r.PhysicsDriver.Body.position.x,r.PhysicsDriver.Body.position.z),new Vector2(goal.x,goal.z))>1.35f);
        }
        public bool HasStoppedBlocker(GuardianPhysicsDriver d)
        {
            if (d.Robot.EmergencyStopped) return true;
            if (crossings.Any(r=>r.Driver==d) && Owner!=null && Owner.Robot.EmergencyStopped) return true;
            if (requests.Any(r=>r.Driver==d))
            {
                foreach (string k in new[]{Branch(d.Body.position),Branch(d.Destination),Dock(d.Destination)})
                    if (Blocker(k,d) is GuardianPhysicsDriver other && other.Robot.EmergencyStopped) return true;
                return sim.Robots.Any(r=>r!=d.Robot && r.EmergencyStopped && Vector2.Distance(new Vector2(r.PhysicsDriver.Body.position.x,r.PhysicsDriver.Body.position.z),new Vector2(d.Destination.x,d.Destination.z))<=1.35f);
            }
            return false;
        }
        public string PendingJobReason(DeliveryTask task)
        {
            if (jobs.TryGetValue(Dock(task.Pickup),out var pickup) && pickup!=task) return task.Kind==DeliveryKind.EmptyDishes?"table_pickup_job":"kitchen_job";
            if (jobs.TryGetValue(Dock(task.Destination),out var goal) && goal!=task) return task.Kind==DeliveryKind.EmptyDishes?"return_counter_job":"table_destination_job";
            return null;
        }
        public GuardianTrafficWait WaitDetails(GuardianPhysicsDriver d)
        {
            if (crossings.Any(r=>r.Driver==d) && Owner!=d) return new GuardianTrafficWait{Category="central_reservation",Holder=Owner==null?"none":Owner.Robot.RobotId};
            var request=requests.FirstOrDefault(r=>r.Driver==d); if(request==null) return default;
            if(request.Ready>sim.DemoClock) return new GuardianTrafficWait{Category="departure_delay"};
            foreach(string key in new[]{Branch(d.Body.position),Branch(d.Destination),Dock(d.Destination)})
            {
                var owner=Blocker(key,d); if(owner==null) continue;
                bool kitchen=key.StartsWith("dock/") && Mathf.Abs(ResourcePoint(key).x+6)<.3f && Mathf.Abs(ResourcePoint(key).y-3.6f)<.3f;
                bool branchHeld=claims.Any(p=>p.Value==owner && p.Key.StartsWith("branch/") && OverlapsResource(p.Key,Branch(new Vector3(-6,0,3.6f))));
                bool stale=kitchen && Vector2.Distance(new Vector2(owner.Body.position.x,owner.Body.position.z),ResourcePoint(key))>1.35f && Vector2.Distance(new Vector2(owner.Destination.x,owner.Destination.z),ResourcePoint(key))>1.35f && !branchHeld;
                return new GuardianTrafficWait{Category=stale?"stale_kitchen_dock":kitchen?"kitchen_dock":key.StartsWith("branch/")?"branch_reservation":"other_dock",Resource=key,Holder=owner.Robot.RobotId,Stale=stale};
            }
            return !DestinationClear(d)?new GuardianTrafficWait{Category="physical_destination_occupied"}:default;
        }
        public string CentralWaitReason=>"waiting for narrow aisle / "+(Owner==null?"grant pending":Owner.Robot.RobotId+(Owner.Robot.EmergencyStopped?" STOP: operator recovery needed":" moving"));
        private void Refresh()
        { reservations.Clear(); foreach (var p in claims) reservations[p.Key]=p.Value.Robot.RobotId; if (Owner!=null) reservations["narrow central aisle"]=Owner.Robot.RobotId; }
        public void Tick()
        {
            foreach (var request in requests.OrderBy(r=>r.Order).ToArray())
            {
                var d=request.Driver; if (request.Ready>sim.DemoClock||d.Robot.EmergencyStopped) continue;
                string[] keys={Branch(d.Body.position),Branch(d.Destination),Dock(d.Destination)};
                string blocked=keys.FirstOrDefault(k=>!Available(k,d));
                if (blocked!=null) { var holder=Blocker(blocked,d); d.WaitReason="waiting "+blocked+" held by "+holder.Robot.RobotId+(holder.Robot.EmergencyStopped?" STOP: operator recovery needed":""); continue; }
                if (!DestinationClear(d)) { d.WaitReason="waiting for physical destination clearance"; continue; }
                foreach (string k in keys) claims[k]=d;
                requests.Remove(request); admitted.Add(d); d.Granted();
            }
            if (Owner==null)
            {
                var crossing=crossings.Where(r=>!r.Driver.Robot.EmergencyStopped).OrderBy(r=>r.Order).FirstOrDefault();
                if (crossing!=null) { crossings.Remove(crossing); Owner=crossing.Driver; Grants++; }
            }
            Refresh();
        }
    }

    /// <summary>Dynamic rigidbody actuator: bounded forces/torques in fixed physics steps.</summary>
    public sealed class GuardianPhysicsDriver : MonoBehaviour
    {
        private readonly List<Vector3> route=new List<Vector3>();
        private readonly List<Collider> disabledVisualColliders=new List<Collider>();
        private GuardianTrafficController traffic;
        private GuardianSimulationSettings settings;
        private Vector3 destination,home;
        private int node,centralEntry,centralExit,sourceClear,deferredExit;
        private Vector3 legSource;
        private bool returningHome,requested,deferred,deferredHome;
        private Vector3 deferredTarget,progressAnchor;
        private float progressAt;
        private bool terminalPositionControl;
        private float lastDistance=float.PositiveInfinity,stalled;
        private float terminalElapsed;
        private string lastContact="none";
        private PhysicsMaterial rollingMaterial;
        public GuardianRobot Robot { get; private set; }
        public Rigidbody Body { get; private set; }
        public BoxCollider Shape { get; private set; }
        public bool Active { get; private set; }
        public bool Busy=>Active && (requested || route.Count>0 || returningHome);
        public bool OwnsRoute=>traffic!=null && traffic.Owner==this;
        private bool Admitted=>traffic!=null && traffic.IsAdmitted(this);
        public string WaitReason { get; internal set; }
        public float MaximumStall { get; private set; }
        public float ProgressWait { get; private set; }
        public float MaximumProgressWait { get; private set; }
        public bool DeferredReplan=>deferred;
        public bool ApproachingHome=>returningHome && route.Count>0 && node==route.Count-1;
        public Vector3 Home=>home;
        public Vector3 Destination=>destination;
        public float LastParkDistance { get; private set; }
        public float LastParkSpeed { get; private set; }
        public Vector3 SafetyIntent { get; private set; }
        public bool PedestrianSafetyHold { get; private set; }
        public string MotionState=>"position="+Body.position+" target="+(node<route.Count?route[node]:home)+
            " velocity="+Body.linearVelocity+" yaw="+Body.rotation.eulerAngles.y+" angular="+Body.angularVelocity+
            " COM="+Body.centerOfMass+" inertia="+Body.inertiaTensor+" inertiaFrame="+Body.inertiaTensorRotation+
            " contact="+lastContact+" "+WaitReason;
        public float PlaneY=>.0155f-(Shape.center.y-Shape.size.y*.5f);
        public bool AtServicePoint(Vector3 point)
        {
            Vector3 delta=Body.position-point; delta.y=0;
            return Active && route.Count==0 && !OwnsRoute && delta.sqrMagnitude<.075f*.075f && Body.linearVelocity.sqrMagnitude<.025f;
        }
        public void Configure(GuardianRobot robot,GuardianTrafficController coordinator,GuardianSimulationSettings config,bool active)
        {
            Robot=robot; traffic=coordinator; settings=config;
            if (Body==null)
            {
                foreach (Collider c in GetComponentsInChildren<Collider>(true)) if (c.enabled) { disabledVisualColliders.Add(c); c.enabled=false; }
                Body=gameObject.AddComponent<Rigidbody>(); Shape=gameObject.AddComponent<BoxCollider>();
                Bounds b=GuardianPhysicsWorld.LocalBounds(transform.Find(NekobotVisualFactory.VisualRootName));
                // The visual child has a floor offset; convert its box back to actor space.
                Transform visual=transform.Find(NekobotVisualFactory.VisualRootName);
                Bounds actor=new Bounds(visual.localPosition+Vector3.Scale(b.center,visual.localScale),Vector3.Scale(b.size,visual.localScale));
                actor.Encapsulate(new Vector3(-.25f,1.30f,.10f)); actor.Encapsulate(new Vector3(.25f,1.75f,.60f));
                Shape.center=actor.center+Vector3.up*.01f; Shape.size=actor.size+new Vector3(.04f,.02f,.04f); Shape.contactOffset=.015f;
                rollingMaterial=new PhysicsMaterial("Guardian rolling chassis") { staticFriction=0,dynamicFriction=0,bounciness=0,
                    frictionCombine=PhysicsMaterialCombine.Minimum,bounceCombine=PhysicsMaterialCombine.Minimum };
                Shape.sharedMaterial=rollingMaterial;
                Body.mass=65; Body.useGravity=false; Body.constraints=RigidbodyConstraints.FreezePositionY|RigidbodyConstraints.FreezeRotationX|RigidbodyConstraints.FreezeRotationZ;
                // The cargo-inclusive envelope is asymmetric. Auto COM would be
                // forward of the chassis origin: yaw then orbits that origin and
                // fights the final position/heading controller. Model the heavy
                // mobile base at the actor's XZ center instead.
                Body.centerOfMass=new Vector3(0,.3f,0);
                Body.interpolation=RigidbodyInterpolation.Interpolate; Body.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
                Body.linearDamping=.2f; Body.angularDamping=2; Body.maxAngularVelocity=4;
                Body.solverIterations=12; Body.solverVelocityIterations=6; Body.maxDepenetrationVelocity=1;
            }
            Active=active; Shape.enabled=active;
            Body.interpolation=active?RigidbodyInterpolation.Interpolate:RigidbodyInterpolation.None;
            if (!active) Body.collisionDetectionMode=CollisionDetectionMode.Discrete;
            Body.isKinematic=!active; Body.detectCollisions=active;
            if (active) Body.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
            foreach (Collider c in disabledVisualColliders) if (c!=null) c.enabled=!active;
            route.Clear(); deferred=false; requested=false; returningHome=false; node=0; SafetyIntent=Vector3.zero; PedestrianSafetyHold=false;
            WaitReason=active?"parked":"legacy transform reference mode";
        }
        public void ResetTo(Vector3 position)
        {
            home=position; route.Clear(); node=0; requested=false; returningHome=false; traffic.Release(this);
            // Reset is the only initialization teleport. A new interpolated body
            // can otherwise re-import its creation Transform on the first step,
            // retaining the old frozen-Y pose instead of this profile's floor.
            var interpolation=Body.interpolation; var constraints=Body.constraints;
            Body.interpolation=RigidbodyInterpolation.None; Body.constraints=RigidbodyConstraints.None;
            transform.SetPositionAndRotation(position,Quaternion.identity);
            Body.position=position; Body.rotation=Quaternion.identity;
            if (!Body.isKinematic) { Body.linearVelocity=Vector3.zero; Body.angularVelocity=Vector3.zero; }
            Physics.SyncTransforms();
            Body.constraints=constraints; Body.interpolation=interpolation;
            stalled=0; MaximumStall=0; deferred=false; progressAnchor=position; progressAt=0; ProgressWait=MaximumProgressWait=0;
            lastDistance=float.PositiveInfinity; SafetyIntent=Vector3.zero; PedestrianSafetyHold=false; WaitReason="parked";
        }
        internal void SetPedestrianSafetyHold(bool active) { PedestrianSafetyHold=active; }
        public void DriveTo(Vector3 target)
        {
            if (Admitted && route.Count>0) { deferred=true; deferredExit=node<=centralExit?centralExit:route.Count-1; deferredHome=false; deferredTarget=target; WaitReason="reroute deferred: clearing protected central exit"; return; }
            if (!Busy) BeginProgress();
            destination=target; destination.y=Body.position.y; returningHome=false; requested=true;
            traffic.Request(this);
        }
        public void ReturnHome()
        {
            if (Admitted && route.Count>0) { deferred=true; deferredExit=node<=centralExit?centralExit:route.Count-1; deferredHome=true; deferredTarget=home; WaitReason="home return deferred: clearing protected central exit"; return; }
            if (!Busy) BeginProgress();
            returningHome=true; destination=home; requested=true;
            if (!OwnsRoute && Vector3.Distance(Body.position,home)<.075f && Body.linearVelocity.sqrMagnitude<.025f
                && Mathf.Abs(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,0))<3 && Body.angularVelocity.magnitude<.12f)
            { ClearDeferred(); traffic.Release(this); requested=false; returningHome=false; route.Clear(); WaitReason="parked"; return; }
            traffic.Request(this);
        }
        private void BeginProgress() { progressAnchor=Body.position; progressAt=GetComponentInParent<GuardianSimulator>().DemoClock; ProgressWait=0; }
        public void Granted() { BuildRoute(); }
        private void ClearDeferred() { deferred=false; deferredHome=false; deferredTarget=default; deferredExit=-1; }
        private void BuildRoute()
        {
            ClearDeferred();
            route.Clear(); node=0; lastDistance=float.PositiveInfinity; stalled=0;
            terminalPositionControl=false;
            terminalElapsed=0;
            Vector3 p=Body.position; legSource=p;
            centralEntry=centralExit=sourceClear=-1;
            bool inCentral=OwnsRoute && Mathf.Abs(p.x+1)<1.2f;
            if (!inCentral)
            {
                route.Add(GuardianTrafficController.Hold(p)); centralEntry=route.Count;
                route.Add(new Vector3(-1,p.y,p.z)); sourceClear=route.Count-1;
            }
            else centralEntry=0;
            route.Add(new Vector3(-1,p.y,destination.z));
            route.Add(GuardianTrafficController.Hold(destination)); centralExit=route.Count-1;
            route.Add(destination); WaitReason="moving / local branch";

        }
        public void Tick(float dt)
        {
            if (!Active) { SafetyIntent=Vector3.zero; return; }
            // A stopped chassis remains a dynamic collision obstacle, but its
            // parking brake must resist another body's contact impulse too.
            // This changes constraints only; it never teleports a stopped actor.
            var constraints=Robot.EmergencyStopped?RigidbodyConstraints.FreezeAll:
                RigidbodyConstraints.FreezePositionY|RigidbodyConstraints.FreezeRotationX|RigidbodyConstraints.FreezeRotationZ;
            if (Body.constraints!=constraints) Body.constraints=constraints;
            float clock=GetComponentInParent<GuardianSimulator>().DemoClock;
            if ((Body.position-progressAnchor).sqrMagnitude>.0064f) { progressAnchor=Body.position; progressAt=clock; }
            ProgressWait=Busy?Mathf.Max(0,clock-progressAt):0; MaximumProgressWait=Mathf.Max(MaximumProgressWait,ProgressWait);
            Vector3 desired=Vector3.zero; float desiredYaw=Body.rotation.eulerAngles.y;
            if (Robot.EmergencyStopped) WaitReason=OwnsRoute?"EMERGENCY STOP; route retained until recovery":"EMERGENCY STOP at waiting bay";
            else if (PedestrianSafetyHold) WaitReason="pedestrian safety hold";
            else if(Robot.CurrentTask!=null && Robot.CurrentTask.AwaitingServicePickup && route.Count==0)
            {
                Vector3 delta=Robot.CurrentTask.Pickup-Body.position; delta.y=0;
                desired=Vector3.ClampMagnitude(delta*2.5f,.35f);
                WaitReason="holding table service point for return-counter access";
            }
            else if (Admitted && node<route.Count)
            {
                bool crossingAllowed=node<centralEntry || node>centralExit || OwnsRoute;
                if (!crossingAllowed) { traffic.RequestCrossing(this); WaitReason=traffic.CentralWaitReason; }
                Vector3 delta=route[node]-Body.position; delta.y=0; float distance=delta.magnitude;
                if (!crossingAllowed) distance=float.PositiveInfinity;
                bool final=node==route.Count-1;
                if (returningHome && final && distance<.3f) terminalPositionControl=true;
                if (terminalPositionControl)
                {
                    terminalElapsed+=dt;
                    MaximumStall=Mathf.Max(MaximumStall,terminalElapsed);
                    // Hold the bay position while aligning the chassis. Do not
                    // alternate between "face the tiny position error" and
                    // "face north" as a rotating body settles at its home.
                    desiredYaw=0;
                    desired=Vector3.ClampMagnitude(delta*2.5f,.5f);
                    if (distance<.075f && Body.linearVelocity.sqrMagnitude<.025f
                        && Mathf.Abs(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,0))<3 && Body.angularVelocity.magnitude<.12f)
                    {
                        ClearDeferred(); LastParkDistance=distance; LastParkSpeed=Body.linearVelocity.magnitude;
                        route.Clear(); requested=false; returningHome=false; terminalPositionControl=false;
                        traffic.Release(this); WaitReason="parked"; desired=Vector3.zero;
                    }
                }
                else if (distance<.075f && Body.linearVelocity.sqrMagnitude<.025f)
                {
                    if (returningHome && final)
                    {
                        desiredYaw=0;
                        if (Mathf.Abs(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,0))<3 && Body.angularVelocity.magnitude<.12f)
                        {
                            ClearDeferred(); LastParkDistance=distance; LastParkSpeed=Body.linearVelocity.magnitude;
                            route.Clear(); requested=false; returningHome=false; traffic.Release(this); WaitReason="parked";
                        }
                    }
                    else
                    {
                        if (node==sourceClear) traffic.ClearSource(this,legSource);
                        if (node==centralExit) traffic.ClearCentral(this);
                        if (deferred && node==deferredExit)
                        {
                            // Retain admission/claims through the original protected exit.
                            // No arrival callback at the abandoned destination is emitted.
                            bool goHome=deferredHome; Vector3 goal=deferredTarget; deferred=false;
                            route.Clear(); node=0;
                            if (goHome) ReturnHome(); else DriveTo(goal);
                        }
                        else
                        {
                            node++;
                            if (node>=route.Count)
                            {
                                route.Clear(); requested=false; Robot.NotifyArrived();
                                if (Robot.CurrentTask==null && route.Count==0) ReturnHome();
                            }
                        }
                        lastDistance=float.PositiveInfinity; stalled=0;
                    }
                }
                else if (crossingAllowed)
                {
                    desiredYaw=Mathf.Atan2(delta.x,delta.z)*Mathf.Rad2Deg;
                    float yawError=Mathf.Abs(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,desiredYaw));
                    // A proportional arrival profile approaches the target without the
                    // bang-bang overshoot that caused repeated 180-degree turns at home.
                    // Keep the same 75mm / 0.158m/s arrival acceptance above.
                    float speed=Mathf.Min(settings.MaximumSpeed,distance*2.5f);
                    speed=Mathf.Min(speed,Mathf.Sqrt(2*settings.MotorAcceleration*Mathf.Max(0,distance-.025f)));
                    speed*=Mathf.Clamp01(1-yawError/65f);
                    desired=delta.normalized*speed;
                    WaitReason="moving / reserved route";
                    if (distance<lastDistance-.002f) { stalled=0; lastDistance=distance; }
                    else stalled+=dt;
                    MaximumStall=Mathf.Max(MaximumStall,stalled);
                    if (stalled>15) WaitReason="blocked route: remove obstruction or recover stopped actor";
                }
            }
            // Apply the same obstacle braking to the terminal position hold.
            float stoppingDistance=Body.linearVelocity.sqrMagnitude/(2*settings.MotorAcceleration)+.12f;
            if (desired.sqrMagnitude>.001f && Blocked(desired.normalized,stoppingDistance))
            { desired=Vector3.zero; WaitReason="obstacle / braking"; }
            SafetyIntent=desired;
            Vector3 velocity=Body.linearVelocity; velocity.y=0;
            Body.AddForce(Vector3.ClampMagnitude((desired-velocity)/dt,settings.MotorAcceleration),ForceMode.Acceleration);
            float omega=Mathf.Clamp(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,desiredYaw)*Mathf.Deg2Rad*5,-3,3);
            // Convert a bounded desired angular acceleration through the actual
            // principal inertia tensor. A raw acceleration-mode torque servo
            // oscillated with this tall, offset cargo envelope near zero yaw.
            Vector3 acceleration=Vector3.up*Mathf.Clamp((omega-Body.angularVelocity.y)/dt,-8,8);
            Quaternion inertiaFrame=Body.rotation*Body.inertiaTensorRotation;
            Vector3 principalAcceleration=Quaternion.Inverse(inertiaFrame)*acceleration;
            Vector3 torque=inertiaFrame*Vector3.Scale(Body.inertiaTensor,principalAcceleration);
            Body.AddTorque(torque,ForceMode.Force);
        }
        private bool Blocked(Vector3 direction,float distance)
        {
            Vector3 center=Body.position+Body.rotation*Shape.center;
            Vector3 half=Vector3.Max(Vector3.one*.01f,Shape.size*.5f-Vector3.one*.02f);
            foreach (RaycastHit hit in Physics.BoxCastAll(center,half,direction,Body.rotation,distance,~0,QueryTriggerInteraction.Ignore))
            {
                if (hit.rigidbody==Body || hit.collider.GetComponent<GuardianPhysicsObstacle>()?.Floor==true) continue;
                lastContact="braking for "+hit.collider.name+" distance="+hit.distance.ToString("F3"); return true;
            }
            return false;
        }
        private void OnCollisionStay(Collision collision)
        { lastContact=collision.collider.name+" impulse="+collision.impulse+" points="+collision.contactCount; }
        private void OnDestroy() { if (rollingMaterial!=null) Destroy(rollingMaterial); }
    }
}
