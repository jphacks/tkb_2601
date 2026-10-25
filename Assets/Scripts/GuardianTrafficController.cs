using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Direct concurrent traffic by default (see GuardianDirectTraffic); the original local
    /// branch/dock claims with a FIFO lease for the narrow central aisle in SingleTrackAisle style.</summary>
    public sealed class GuardianTrafficController : MonoBehaviour
    {
        private GuardianDirectTraffic direct;
        private readonly List<GuardianPhysicsDriver> directRequests=new List<GuardianPhysicsDriver>();
        private readonly Dictionary<GuardianPhysicsDriver,float> directRetryAt=new Dictionary<GuardianPhysicsDriver,float>();
        private readonly Dictionary<GuardianPhysicsDriver,int> directRetryVersion=new Dictionary<GuardianPhysicsDriver,int>();
        private readonly Dictionary<GuardianPhysicsDriver,float> directEarliestRetry=new Dictionary<GuardianPhysicsDriver,float>();
        public int DirectPlanAttempts {get;private set;}
        public bool DirectActive=>direct!=null;
        public GuardianDirectTraffic Direct=>direct;
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
        public int QueueLength=>direct!=null?directRequests.Count:requests.Count+crossings.Count;
        public int JobClaimCount=>jobs.Count;
        public const float SurfaceMargin=.05f;
        public float ResourceSpacing { get; private set; }=1.35f;
        public IReadOnlyDictionary<string,string> Reservations=>reservations;
        public string DiagnosticClaims=>string.Join(";",claims.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value.Robot.RobotId));
        public string DiagnosticQueue=>string.Join(";",requests.OrderBy(r=>r.Order).Select(r=>r.Driver.Robot.RobotId+"@"+r.Order));
        private static string Point(Vector3 p)=>p.x.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"/"+p.z.ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
        public static Vector3 Hold(Vector3 p)=>new Vector3(p.x< -1?-3:1,p.y,p.z);
        private static string Dock(Vector3 p)=>"dock/"+Point(p);
        private static string Branch(Vector3 p)=>"branch/"+Point(Hold(p));
        public void ResetTraffic(GuardianSimulator simulator,int seed)
        {
            sim=simulator; departures=new System.Random(seed^0x5142); requests.Clear(); crossings.Clear(); claims.Clear(); jobs.Clear(); admitted.Clear(); reservations.Clear(); Owner=null; sequence=0; Grants=0;
            direct=null; directRequests.Clear(); directRetryAt.Clear(); directRetryVersion.Clear();directEarliestRetry.Clear();DirectPlanAttempts=0;
        }
        public bool IsAdmitted(GuardianPhysicsDriver d)=>direct!=null?direct.HasLeg(d.Robot.RobotId):admitted.Contains(d);
        public void ConfigureClearance()
        {
            // Swept horizontal corner radius includes the cargo-box center
            // offset. Two accepted endpoints may each be 75mm off their goal.
            float radius=sim.Robots.Max(r=>r.PhysicsDriver.TurningRadius);
            ResourceSpacing=Mathf.Max(1.35f,2*radius+2*.075f+SurfaceMargin);
            direct=sim.Mode==GuardianSimulationMode.Physics && sim.Settings.Traffic==GuardianTrafficStyle.DirectConcurrent?BuildDirect():null;
        }

        // ---- Direct concurrent traffic ----
        private GuardianDirectTraffic BuildDirect()
        {
            Bounds floor=new Bounds(Vector3.zero,new Vector3(20,1,12.5f));
            foreach(Collider c in sim.PhysicsWorld.Obstacles) if(c!=null && c.GetComponent<GuardianPhysicsObstacle>()?.Floor==true) floor=c.bounds;
            var grid=new GuardianFloorGrid(floor.min.x,floor.min.z,floor.max.x,floor.max.z,.1f);
            foreach(Collider c in sim.PhysicsWorld.Obstacles)
                if(c is BoxCollider box && box.enabled && box.GetComponent<GuardianPhysicsObstacle>()?.Floor!=true) grid.Obstacles.Add(FloorFootprintOf(box));
            grid.Build();
            var traffic=new GuardianDirectTraffic(grid);
            foreach(Vector3 p in GuardianSimulator.ServicePoints) traffic.Docks.Add(new GuardianDock(new FloorPoint(p.x,p.z),GuardianPhysicsDriver.ServiceYaw(p)));
            foreach(var r in sim.Robots) traffic.Park(r.RobotId,r.PhysicsDriver.FootprintBox());
            return traffic;
        }
        /// <summary>Plan footprint of a static box collider: its two horizontal axes, or its world AABB if tilted.</summary>
        private static FloorBox FloorFootprintOf(BoxCollider box)
        {
            Transform t=box.transform;Vector3 center=t.TransformPoint(box.center);
            Vector3[] half={t.TransformVector(new Vector3(box.size.x*.5f,0,0)),t.TransformVector(new Vector3(0,box.size.y*.5f,0)),t.TransformVector(new Vector3(0,0,box.size.z*.5f))};
            int up=0;for(int i=1;i<3;i++) if(Mathf.Abs(half[i].normalized.y)>Mathf.Abs(half[up].normalized.y)) up=i;
            Vector3 u=half[(up+1)%3],w=half[(up+2)%3];u.y=w.y=0;
            if(Mathf.Abs(half[up].normalized.y)<.999f || Mathf.Abs(Vector3.Dot(u.normalized,w.normalized))>.01f)
                return new FloorBox(new FloorPoint(box.bounds.center.x,box.bounds.center.z),box.bounds.extents.x,box.bounds.extents.z,0);
            float yaw=Mathf.Atan2(-u.z,u.x)*Mathf.Rad2Deg;
            return new FloorBox(new FloorPoint(center.x,center.z),u.magnitude,w.magnitude,yaw);
        }
        private static FloorPoint Planar(Vector3 v)=>new FloorPoint(v.x,v.z);
        private void DirectRequest(GuardianPhysicsDriver d)
        {
            if(!directRequests.Contains(d)) directRequests.Add(d);
            directRetryAt.Remove(d);directEarliestRetry.Remove(d);d.WaitReason="planning direct route";
        }
        private void DirectTick()
        {
            // Bound planning work per physics step; a blocked request retries when any
            // reservation changes or after a short pause.
            int attempts=0;
            foreach(var d in directRequests.ToArray())
            {
                if(d==null || !d.Active || !sim.Robots.Contains(d.Robot)) {directRequests.Remove(d);continue;}
                if(d.Robot.EmergencyStopped || d.SafetyHeld) continue;
                // Cell releases can change Version every fixed step. A blocked request
                // still observes a 100 ms lower bound between expensive path searches.
                if(directEarliestRetry.TryGetValue(d,out float earliest) && earliest>sim.DemoClock)continue;
                if(directRetryAt.TryGetValue(d,out float at) && at>sim.DemoClock && directRetryVersion[d]==direct.Version) continue;
                if(attempts++>=2) break;
                DirectPlanAttempts++;
                var result=direct.Request(d.Robot.RobotId,Planar(d.Body.position),d.Body.rotation.eulerAngles.y,d.Footprint,Planar(d.Destination),d.ServiceLeg,d.HomeLeg,0,
                    out var plan,Planar(d.PlanarVelocity),d.EffectiveAcceleration);
                if(result==GuardianDirectRequestResult.Committed) {directRequests.Remove(d);directRetryAt.Remove(d);directRetryVersion.Remove(d);directEarliestRetry.Remove(d);Grants++;d.GrantedDirect(plan);}
                else
                {
                    directRetryAt[d]=sim.DemoClock+.3f;directRetryVersion[d]=direct.Version;
                    directEarliestRetry[d]=sim.DemoClock+.1f;
                    d.WaitReason=(result==GuardianDirectRequestResult.DestinationOccupied?"waiting: ":"waiting for a clear route: ")+direct.LastFailure;
                }
            }
        }
        internal float DirectFrontier(GuardianPhysicsDriver d)=>direct==null?float.PositiveInfinity:direct.Frontier(d.Robot.RobotId);
        internal void DirectProgress(GuardianPhysicsDriver d,float progress)=>direct?.UpdateProgress(d.Robot.RobotId,progress);
        internal string DirectBlocker(GuardianPhysicsDriver d)=>direct?.FrontierBlocker(d.Robot.RobotId);
        internal void CompleteDirect(GuardianPhysicsDriver d)
        {
            if(direct==null) return;
            if(direct.HasLeg(d.Robot.RobotId)) direct.Complete(d.Robot.RobotId);
            else if(!direct.Holds(d.Robot.RobotId)) direct.Park(d.Robot.RobotId,d.FootprintBox());
        }
        /// <summary>Stay parked where the robot is (cancels any pending direct request).</summary>
        internal void ParkDirect(GuardianPhysicsDriver d)
        {
            directRequests.Remove(d);directRetryAt.Remove(d);directRetryVersion.Remove(d);directEarliestRetry.Remove(d);
            if(direct!=null && !direct.HasLeg(d.Robot.RobotId) && !direct.Holds(d.Robot.RobotId)) direct.Park(d.Robot.RobotId,d.FootprintBox());
        }
        public bool CanAssign(DeliveryTask task)=>new[]{task.Pickup,task.Destination}.All(p=>!jobs.TryGetValue(Dock(p),out var owner)||owner==task);
        public void ClaimJob(DeliveryTask task) { jobs[Dock(task.Pickup)]=task; jobs[Dock(task.Destination)]=task; }
        /// <summary>No job holds the service point at this place (the return counter, for example).</summary>
        public bool ServicePointFree(Vector3 point)=>!jobs.ContainsKey(Dock(point));
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
            if (direct!=null) { DirectRequest(d); return; }
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
        public void Release(GuardianPhysicsDriver d)
        {
            CancelQueued(d); ReleaseLocal(d); admitted.Remove(d); if (Owner==d) Owner=null;
            directRequests.Remove(d); directRetryAt.Remove(d);directRetryVersion.Remove(d);directEarliestRetry.Remove(d); direct?.Remove(d.Robot.RobotId);
            Refresh();
        }
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
        public void ClearApproachBranch(GuardianPhysicsDriver d)
        {
            // Adjacent branch resources can overlap (e.g. spare z=1.7 and
            // kitchen z=3.6). ClearSource retains an overlapping destination
            // during the crossing. After the complete shape has physically
            // cleared the approach, release every now-clear own branch, not
            // only the destination's exact key. Retaining the departed spare
            // key while waiting at the kitchen creates a table/home wait cycle.
            Vector2 position=new Vector2(d.Body.position.x,d.Body.position.z);
            foreach(string key in claims.Where(p=>p.Value==d && p.Key.StartsWith("branch/")
                && Vector2.Distance(ResourcePoint(p.Key),position)>ResourceSpacing).Select(p=>p.Key).ToArray())
                claims.Remove(key);
        }
        private static Vector2 ResourcePoint(string key)
        { string[] p=key.Split('/'); return new Vector2(float.Parse(p[1],System.Globalization.CultureInfo.InvariantCulture),float.Parse(p[2],System.Globalization.CultureInfo.InvariantCulture)); }
        private bool OverlapsResource(string a,string b)
        {
            if (a.Split('/')[0]!=b.Split('/')[0]) return false;
            Vector2 pa=ResourcePoint(a),pb=ResourcePoint(b);
            return a.StartsWith("branch/")?Mathf.Abs(pa.x-pb.x)<.1f && Mathf.Abs(pa.y-pb.y)<ResourceSpacing:Vector2.Distance(pa,pb)<ResourceSpacing;
        }
        private bool TouchesSource(string key,Vector3 position)
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
            if (direct!=null)
            {
                string blocker=direct.FrontierBlocker(d.Robot.RobotId);
                var other=blocker==null?null:sim.Robots.FirstOrDefault(r=>r.RobotId==blocker);
                return other!=null && (other.EmergencyStopped || other.PhysicsDriver.SafetyHeld);
            }
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
            if (direct!=null)
            {
                if (directRequests.Contains(d)) return new GuardianTrafficWait{Category="direct_route_request",Resource=direct.LastFailure};
                string blocker=direct.FrontierBlocker(d.Robot.RobotId);
                return blocker==null?default:new GuardianTrafficWait{Category="direct_yield",Holder=blocker};
            }
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
        {
            reservations.Clear(); foreach (var p in claims) reservations[p.Key]=p.Value.Robot.RobotId; if (Owner!=null) reservations["narrow central aisle"]=Owner.Robot.RobotId;
            // Direct style lists moving legs only; parked footprints are not route reservations.
            if (direct!=null) foreach (string id in direct.Moving) reservations["route/"+id+"/cells="+direct.HeldCells(id)]=id;
        }
        public void Tick()
        {
            if (direct!=null) { DirectTick(); Refresh(); return; }
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
        private string lastObstacle="none";
        private PhysicsMaterial rollingMaterial;
        private Vector3 chassisInertia;
        private GuardianMotionInterlock safetyInterlock;
        private readonly GuardianRotationEnvelope rotationEnvelope=new GuardianRotationEnvelope();
        public bool RotationGuardHeld {get;private set;}public bool RotationGuardIntervened {get;private set;}public bool RotationInitialOverlap {get;private set;}
        public GuardianPlanarDynamics Dynamics {get;private set;}
        public Vector3 PlanarVelocity=>Dynamics!=null?Dynamics.RootVelocity:Vector3.ProjectOnPlane(Body.linearVelocity,Vector3.up);
        public void RefreshLoad() {if(Dynamics!=null)Dynamics.RefreshInventory(GetComponentInParent<GuardianSimulator>().Inventory,Robot);}
        internal void AttachSafetyInterlock(GuardianMotionInterlock value) {safetyInterlock=value;}
        public Vector3 IntentVelocity
        {get {if(!Active || node>=route.Count)return Vector3.zero;Vector3 d=route[node]-Body.position;d.y=0;return d.sqrMagnitude>.001f?d.normalized*EffectiveMaximumSpeed:Vector3.zero;}}
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
        public bool DeferredReplan=>deferred || directPending;
        // Direct concurrent traffic state.
        private GuardianLegPlan directPlan;private float directProgress;private bool directPending;
        public bool Direct=>traffic!=null && traffic.DirectActive;
        public bool DirectHomeward=>Direct && returningHome && Robot.CurrentTask==null;
        private bool legacyPedestrianHold;
        public bool PedestrianSafetyHold=>legacyPedestrianHold || SafetyHeld;
        public Vector3 SafetyIntent=>IntentVelocity;
        public void SetPedestrianSafetyHold(bool held) { legacyPedestrianHold=held; }
        public bool SafetyHeld=>safetyInterlock!=null && safetyInterlock.Held;
        public GuardianLegPlan DirectPlan=>directPlan;
        public FloorFootprint Footprint=>new FloorFootprint(Shape.center.x,Shape.center.z,Shape.size.x*.5f,Shape.size.z*.5f);
        public FloorBox FootprintBox()=>Footprint.At(new FloorPoint(Body.position.x,Body.position.z),Body.rotation.eulerAngles.y);
        internal bool ServiceLeg=>!returningHome && Robot.CurrentTask!=null;
        internal bool HomeLeg=>returningHome;
        public bool ApproachingHome=>returningHome && route.Count>0 && node==route.Count-1;
        public Vector3 Home=>home;
        /// <summary>Moves the place the robot goes back to when it has no job (the live demo's patrol).</summary>
        internal void SetHome(Vector3 position) { position.y=home.y; home=position; }
        public static float ServiceYaw(Vector3 point)=>point.x> -1?270:90;
        public bool ServiceHeadingAligned(Vector3 point)=>Mathf.Abs(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,ServiceYaw(point)))<3 && Body.angularVelocity.magnitude<.12f;
        public float TurningRadius
        {
            get
            {
                Vector3 half=Shape.size*.5f;
                return new Vector2(Mathf.Abs(Shape.center.x)+half.x,Mathf.Abs(Shape.center.z)+half.z).magnitude;
            }
        }
        public Vector3 Destination=>destination;
        public string DiagnosticRoute=>"node="+node+"/"+route.Count+" sourceClear="+sourceClear+" central="+centralEntry+".."+centralExit+" source="+legSource+" final="+destination+" home="+returningHome+" admitted="+Admitted+" requested="+requested+" deferred="+deferred
            +(Direct?" direct progress="+directProgress.ToString("F2")+"/"+(directPlan!=null?directPlan.Length.ToString("F2"):"-")+" pending="+directPending+" frontier="+traffic.DirectFrontier(this).ToString("F2"):"");
        public float LastParkDistance { get; private set; }
        public float LastParkSpeed { get; private set; }
        public float PeakPlanarSpeed { get; private set; }
        public float LastStoppingDistance { get; private set; }
        public float LastBrakingSpeed { get; private set; }
        public float LastBrakingAcceleration { get; private set; }
        public float EffectiveAcceleration=>Dynamics!=null?Dynamics.ConservativeBrakeAcceleration:settings.MotorAcceleration*settings.MotionSpeedMultiplier;
        /// <summary>Speed limit from the supervisory AI (GuardianHubLink), 0.3-1. Lowering it slows the robot down.</summary>
        public float SpeedScale=1f;
        public float EffectiveMaximumSpeed=>settings.MaximumSpeed*settings.MotionSpeedMultiplier*Mathf.Clamp(SpeedScale,.3f,1f);
        public string MotionState=>"position="+Body.position+" target="+(node<route.Count?route[node]:home)+
            " velocity="+Body.linearVelocity+" yaw="+Body.rotation.eulerAngles.y+" angular="+Body.angularVelocity+
            " COM="+Body.centerOfMass+" inertia="+Body.inertiaTensor+" inertiaFrame="+Body.inertiaTensorRotation+
            " contact="+lastContact+" blocker="+lastObstacle+" "+WaitReason;
        public float PlaneY=>.0155f-(Shape.center.y-Shape.size.y*.5f);
        public bool AtServicePoint(Vector3 point)
        {
            Vector3 delta=Body.position-point; delta.y=0;
            return Active && route.Count==0 && !OwnsRoute && delta.sqrMagnitude<.075f*.075f && Body.linearVelocity.sqrMagnitude<.025f && ServiceHeadingAligned(point);
        }
        public void Configure(GuardianRobot robot,GuardianTrafficController coordinator,GuardianSimulationSettings config,bool active)
        {
            Robot=robot; traffic=coordinator; settings=config;
            if(active) (robot.GetComponent<GuardianCargoTray>()??robot.gameObject.AddComponent<GuardianCargoTray>()).Configure();
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
                // Rotation constraints use inertia space. The visual/cargo box
                // has tilted automatic principal axes; model the wheeled base
                // with upright axes so its free yaw axis is actually vertical.
                Vector3 size=Shape.size;
                chassisInertia=Body.mass/12f*new Vector3(size.y*size.y+size.z*size.z,size.x*size.x+size.z*size.z,size.x*size.x+size.y*size.y);
                Body.inertiaTensor=chassisInertia;
                Body.inertiaTensorRotation=Quaternion.identity;
                Body.interpolation=RigidbodyInterpolation.Interpolate; Body.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
                Body.linearDamping=.2f; Body.angularDamping=2; Body.maxAngularVelocity=4;
                Body.solverIterations=12; Body.solverVelocityIterations=6; Body.maxDepenetrationVelocity=1;
            }
            Dynamics=active && settings.Physical.Enabled?new GuardianPlanarDynamics(Body,Shape.size,settings.Physical):null;
            if(Dynamics==null){Body.mass=65;Body.centerOfMass=new Vector3(0,.3f,0);Body.linearDamping=.2f;Body.angularDamping=2;Body.sleepThreshold=.005f;}
            Active=active; Shape.enabled=active;
            Body.interpolation=active?RigidbodyInterpolation.Interpolate:RigidbodyInterpolation.None;
            if (!active) Body.collisionDetectionMode=CollisionDetectionMode.Discrete;
            Body.isKinematic=!active; Body.detectCollisions=active;
            if (active) Body.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
            if(active) ApplyConstraints(RigidbodyConstraints.FreezePositionY|RigidbodyConstraints.FreezeRotationX|RigidbodyConstraints.FreezeRotationZ);
            foreach (Collider c in disabledVisualColliders) if (c!=null) c.enabled=!active;
            route.Clear(); deferred=false; requested=false; returningHome=false; node=0; WaitReason=active?"parked":"legacy transform reference mode";
            directPlan=null; directPending=false; directProgress=0;
        }
        public void ResetTo(Vector3 position)
        {
            RotationGuardHeld=RotationGuardIntervened=RotationInitialOverlap=false;
            home=position; route.Clear(); node=0; requested=false; returningHome=false; traffic.Release(this);
            directPlan=null; directPending=false; directProgress=0;
            // Reset is the only initialization teleport. A new interpolated body
            // can otherwise re-import its creation Transform on the first step,
            // retaining the old frozen-Y pose instead of this profile's floor.
            var interpolation=Body.interpolation; var constraints=Body.constraints;
            Body.interpolation=RigidbodyInterpolation.None; Body.constraints=RigidbodyConstraints.None;
            transform.SetPositionAndRotation(position,Quaternion.identity);
            Body.position=position; Body.rotation=Quaternion.identity;
            if (!Body.isKinematic) { Body.linearVelocity=Vector3.zero; Body.angularVelocity=Vector3.zero; }
            Physics.SyncTransforms();
            ApplyConstraints(constraints); Body.interpolation=interpolation;
            Dynamics?.ResetObservationForScenario();
            stalled=0; MaximumStall=0; deferred=false; progressAnchor=position; progressAt=0; ProgressWait=MaximumProgressWait=0; lastDistance=float.PositiveInfinity; WaitReason="parked";
            PeakPlanarSpeed=0; LastStoppingDistance=0;
        }
        public void DriveTo(Vector3 target)
        {
            if (Direct)
            {
                // Re-plan from wherever the robot is; the current leg continues until the new one is granted.
                if (!Busy) BeginProgress();
                destination=target; destination.y=Body.position.y; returningHome=false; requested=true; directPending=true;
                traffic.Request(this); return;
            }
            if (Admitted && route.Count>0) { deferred=true; deferredExit=node<=centralExit?centralExit:route.Count-1; deferredHome=false; deferredTarget=target; WaitReason="reroute deferred: clearing protected central exit"; return; }
            if (!Busy) BeginProgress();
            destination=target; destination.y=Body.position.y; returningHome=false; requested=true;
            traffic.Request(this);
        }
        public void ReturnHome()
        {
            if (Direct)
            {
                if (!Busy) BeginProgress();
                returningHome=true; destination=home; requested=true;
                if (directPlan==null && Vector3.Distance(Body.position,home)<.075f && Body.linearVelocity.sqrMagnitude<.025f
                    && Mathf.Abs(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,0))<3 && Body.angularVelocity.magnitude<.12f)
                { directPending=false; traffic.ParkDirect(this); requested=false; returningHome=false; route.Clear(); WaitReason="parked"; return; }
                directPending=true; traffic.Request(this); return;
            }
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
        internal void GrantedDirect(GuardianLegPlan plan)
        {
            ClearDeferred(); directPending=false; directPlan=plan; directProgress=0;
            route.Clear(); foreach (var p in plan.Points) route.Add(new Vector3(p.X,Body.position.y,p.Z));
            node=Mathf.Min(1,route.Count-1); lastDistance=float.PositiveInfinity; stalled=0;
            terminalPositionControl=false; terminalElapsed=0; legSource=Body.position; centralEntry=centralExit=sourceClear=-1;
            WaitReason="moving / direct route";
        }
        private void DirectStep(float dt,ref Vector3 desired,ref float desiredYaw)
        {
            Vector3 v=PlanarVelocity;
            var o=GuardianDirectControl.Step(directPlan,new GuardianDirectControl.Input{
                Position=new FloorPoint(Body.position.x,Body.position.z),Velocity=new FloorPoint(v.x,v.z),Yaw=Body.rotation.eulerAngles.y,YawRate=Body.angularVelocity.y,
                MaximumSpeed=EffectiveMaximumSpeed,Acceleration=EffectiveAcceleration,Frontier=traffic.DirectFrontier(this),Progress=directProgress,Node=node});
            directProgress=o.Progress; traffic.DirectProgress(this,o.Progress);
            desired=new Vector3(o.Velocity.X,0,o.Velocity.Z); desiredYaw=o.HoldYaw?Body.rotation.eulerAngles.y:o.Yaw;
            if (o.AdvanceNode && node<route.Count-1) { node++; lastDistance=float.PositiveInfinity; stalled=0; }
            WaitReason=o.Waiting?"yielding to "+(traffic.DirectBlocker(this)??"robot")+" / "+o.Reason:o.Reason;
            float remaining=directPlan.Length-directProgress;
            if (remaining<lastDistance-.002f || o.Waiting) { stalled=0; lastDistance=remaining; } else stalled+=dt;
            MaximumStall=Mathf.Max(MaximumStall,stalled);
            if (stalled>15) WaitReason="blocked route: remove obstruction or recover stopped actor";
            if (o.Arrived) ArriveDirect();
        }
        private void ArriveDirect()
        {
            bool home=returningHome;
            LastParkDistance=Vector3.Distance(Body.position,route[route.Count-1]); LastParkSpeed=Body.linearVelocity.magnitude;
            traffic.CompleteDirect(this); directPlan=null; route.Clear(); node=0;
            // A newer request superseded this leg: no arrival callback at an abandoned goal.
            if (directPending) { WaitReason="re-planning"; return; }
            requested=false;
            if (home) { returningHome=false; WaitReason="parked"; return; }
            Robot.NotifyArrived();
            if (Robot.CurrentTask==null && route.Count==0 && !requested) ReturnHome();
        }
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
            if (!Active) return;
            // A stopped chassis remains a dynamic collision obstacle, but its
            // parking brake must resist another body's contact impulse too.
            // This changes constraints only; it never teleports a stopped actor.
            RefreshLoad();bool localHold=PedestrianSafetyHold;
            var constraints=Dynamics==null && (Robot.EmergencyStopped || (localHold && Body.linearVelocity.sqrMagnitude<.0025f && Body.angularVelocity.magnitude<.12f))?RigidbodyConstraints.FreezeAll:
                RigidbodyConstraints.FreezePositionY|RigidbodyConstraints.FreezeRotationX|RigidbodyConstraints.FreezeRotationZ;
            if (Body.constraints!=constraints) ApplyConstraints(constraints);
            float clock=GetComponentInParent<GuardianSimulator>().DemoClock;
            Vector3 planarVelocity=PlanarVelocity;
            PeakPlanarSpeed=Mathf.Max(PeakPlanarSpeed,planarVelocity.magnitude);
            if ((Body.position-progressAnchor).sqrMagnitude>.0064f) { progressAnchor=Body.position; progressAt=clock; }
            ProgressWait=Busy?Mathf.Max(0,clock-progressAt):0; MaximumProgressWait=Mathf.Max(MaximumProgressWait,ProgressWait);
            Vector3 desired=Vector3.zero; float desiredYaw=Body.rotation.eulerAngles.y;
            if (Robot.EmergencyStopped) WaitReason=OwnsRoute?"EMERGENCY STOP; route retained until recovery":"EMERGENCY STOP at waiting bay";
            else if(localHold) WaitReason="Local bounded braking / "+safetyInterlock.Reason+"; task and route retained";
            else if(Robot.CurrentTask!=null && Robot.CurrentTask.AwaitingServicePickup && route.Count==0)
            {
                Vector3 delta=Robot.CurrentTask.Pickup-Body.position; delta.y=0;
                desired=Vector3.ClampMagnitude(delta*2.5f,.35f);
                WaitReason="holding table service point for return-counter access";
            }
            else if (Direct && directPlan!=null) DirectStep(dt,ref desired,ref desiredYaw);
            else if (Direct) { if (directPending && WaitReason=="parked") WaitReason="planning direct route"; }
            else if (Admitted && node<route.Count)
            {
                bool crossingAllowed=node<centralEntry || node>centralExit || OwnsRoute;
                if (!crossingAllowed) { traffic.RequestCrossing(this); WaitReason=traffic.CentralWaitReason; }
                Vector3 delta=route[node]-Body.position; delta.y=0; float distance=delta.magnitude;
                if (!crossingAllowed) distance=float.PositiveInfinity;
                bool final=node==route.Count-1;
                bool service=!returningHome && Robot.CurrentTask!=null;
                // Once the whole corner envelope has left the exit hold and
                // yaw is settled on a straight service approach, that turn
                // resource is clear. Keep the service dock/job until handoff.
                if(final && !returningHome && !OwnsRoute && !deferred
                    && Vector3.Distance(Body.position,GuardianTrafficController.Hold(destination))>TurningRadius+.075f+GuardianTrafficController.SurfaceMargin
                    && Mathf.Abs(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,service?ServiceYaw(destination):Mathf.Atan2(delta.x,delta.z)*Mathf.Rad2Deg))<5
                    && Body.angularVelocity.magnitude<.2f) traffic.ClearApproachBranch(this);
                if (returningHome && final && distance<.3f) terminalPositionControl=true;
                if(service && !deferred && node==centralExit && distance<.075f && Body.linearVelocity.sqrMagnitude<.025f && !ServiceHeadingAligned(destination))
                {
                    // Turn at the reserved clear approach, never beside the
                    // table/counter. Retain the central exit until yaw settles.
                    desiredYaw=ServiceYaw(destination); desired=Vector3.ClampMagnitude(delta*2.5f,.35f);
                    WaitReason="aligning rear tray at reserved approach";
                }
                else if (terminalPositionControl)
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
                else if (distance<.075f && Body.linearVelocity.sqrMagnitude<.025f && !(service && final && !ServiceHeadingAligned(destination)))
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
                    desiredYaw=service && final?ServiceYaw(destination):Mathf.Atan2(delta.x,delta.z)*Mathf.Rad2Deg;
                    float yawError=Mathf.Abs(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,desiredYaw));
                    // A proportional arrival profile approaches the target without the
                    // bang-bang overshoot that caused repeated 180-degree turns at home.
                    // Keep the same 75mm / 0.158m/s arrival acceptance above.
                    float speed=Mathf.Min(EffectiveMaximumSpeed,distance*2.5f*settings.MotionSpeedMultiplier);
                    if(service && final) speed=Mathf.Min(speed,.8f); // Slow reverse with the rear shelf facing the service point.
                    speed=Mathf.Min(speed,Mathf.Sqrt(2*EffectiveAcceleration*Mathf.Max(0,distance-.025f)));
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
            // Use actual velocity after a 2x -> 1x change too. Include one fixed
            // step of travel; no velocity/position reset is used for switching.
            float stoppingDistance=Dynamics!=null?Dynamics.StoppingBound(planarVelocity.magnitude,desired.magnitude,dt):planarVelocity.sqrMagnitude/(2*EffectiveAcceleration)+planarVelocity.magnitude*dt+.12f;
            LastStoppingDistance=stoppingDistance;
            LastBrakingSpeed=planarVelocity.magnitude; LastBrakingAcceleration=EffectiveAcceleration;
            float clearance=desired.sqrMagnitude>.001f?ObstacleClearance(desired.normalized,stoppingDistance):float.PositiveInfinity;
            bool obstacle=float.IsFinite(clearance);
            if(obstacle && Dynamics!=null)
            {
                float cap=Dynamics.ClearanceSpeed(planarVelocity.magnitude,desired.magnitude,clearance,dt);
                if(cap>.005f){desired=Vector3.ClampMagnitude(desired,cap);obstacle=false;WaitReason="clearance-limited force motion";}
            }
            if (obstacle)
            { desired=Vector3.zero; WaitReason="obstacle / braking"; }
            if(Dynamics!=null)
            {
                float targetOmega=Mathf.Clamp(Mathf.DeltaAngle(Body.rotation.eulerAngles.y,desiredYaw)*Mathf.Deg2Rad*5,-settings.Physical.MaximumYawRate,settings.Physical.MaximumYawRate);
                RotationGuardHeld=RotationGuardIntervened=RotationInitialOverlap=false;
                if(!rotationEnvelope.Clear(this,desired,targetOmega,dt))
                {
                    RotationGuardIntervened=true;RotationInitialOverlap=rotationEnvelope.InitialOverlap;
                    if(!RotationInitialOverlap && rotationEnvelope.Clear(this,Vector3.zero,targetOmega,dt))
                    {desired=Vector3.zero;WaitReason="rotation envelope: translation limited";}
                    else {RotationGuardHeld=true;desired=Vector3.zero;targetOmega=0;WaitReason="rotation swept envelope / bounded braking";}
                }
                Dynamics.Apply(desired,targetOmega,dt,localHold || Robot.EmergencyStopped || obstacle || RotationGuardHeld);return;
            }
            Vector3 velocity=Body.linearVelocity; velocity.y=0;
            Body.AddForce(Vector3.ClampMagnitude((desired-velocity)/dt,EffectiveAcceleration),ForceMode.Acceleration);
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
        private float ObstacleClearance(Vector3 direction,float distance)
        {
            Vector3 center=Body.position+Body.rotation*Shape.center;
            Vector3 half=Shape.size*.5f+new Vector3(GuardianTrafficController.SurfaceMargin*.5f,0,GuardianTrafficController.SurfaceMargin*.5f);
            float nearest=float.PositiveInfinity;
            foreach (RaycastHit hit in Physics.BoxCastAll(center,half,direction,Body.rotation,distance,~0,QueryTriggerInteraction.Ignore))
            {
                if (hit.rigidbody==Body || hit.collider.GetComponent<GuardianPhysicsObstacle>()?.Floor==true) continue;
                if(hit.distance<nearest){nearest=hit.distance;lastObstacle=hit.collider.name+" distance="+hit.distance.ToString("F3")+" shape="+Shape.center+"/"+Shape.size;}
            }
            return nearest;
        }
        private void ApplyConstraints(RigidbodyConstraints constraints)
        {
            Body.constraints=constraints;
            // PhysX masks locked inertia axes. Restore the authored diagonal
            // after FreezeAll, otherwise recovery can leave yaw inertia zero.
            Body.inertiaTensor=Dynamics!=null?Dynamics.Inertia:chassisInertia; Body.inertiaTensorRotation=Quaternion.identity;
        }
        private void OnCollisionStay(Collision collision)
        { lastContact=collision.collider.name+" impulse="+collision.impulse+" points="+collision.contactCount; }
        private void OnDestroy() { if (rollingMaterial!=null) Destroy(rollingMaterial); }
    }
}
