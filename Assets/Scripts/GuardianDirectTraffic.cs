using System;
using System.Collections.Generic;

namespace Guardian.UnitySim
{
    // Direct concurrent traffic: obstacle-aware smooth legs plus ordered floor-cell
    // reservations. No UnityEngine dependency, so routing and ordering rules are
    // exercised by an offline multi-robot simulation as well as by the driver.

    /// <summary>Planar floor point (Unity x/z).</summary>
    public readonly struct FloorPoint
    {
        public readonly float X,Z;
        public FloorPoint(float x,float z) {X=x;Z=z;}
        public static FloorPoint operator+(FloorPoint a,FloorPoint b)=>new FloorPoint(a.X+b.X,a.Z+b.Z);
        public static FloorPoint operator-(FloorPoint a,FloorPoint b)=>new FloorPoint(a.X-b.X,a.Z-b.Z);
        public static FloorPoint operator*(FloorPoint a,float k)=>new FloorPoint(a.X*k,a.Z*k);
        public float Length=>MathF.Sqrt(X*X+Z*Z);
        public static float Distance(FloorPoint a,FloorPoint b)=>(a-b).Length;
        public static float Dot(FloorPoint a,FloorPoint b)=>a.X*b.X+a.Z*b.Z;
        public static FloorPoint Lerp(FloorPoint a,FloorPoint b,float t)=>new FloorPoint(a.X+(b.X-a.X)*t,a.Z+(b.Z-a.Z)*t);
        /// <summary>Unity yaw in degrees (0=+Z, 90=+X) to a unit forward vector.</summary>
        public static FloorPoint Forward(float yaw) {float r=yaw*MathF.PI/180;return new FloorPoint(MathF.Sin(r),MathF.Cos(r));}
        public static float YawOf(FloorPoint d)=>Wrap(MathF.Atan2(d.X,d.Z)*180/MathF.PI);
        public static float Wrap(float yaw) {yaw%=360;return yaw<0?yaw+360:yaw;}
        public static float DeltaAngle(float from,float to) {float d=Wrap(to-from);return d>180?d-360:d;}
        public override string ToString()=>"("+X.ToString("F2")+","+Z.ToString("F2")+")";
    }

    /// <summary>Oriented rectangle on the floor: center, half extents on local x/z and Unity yaw.</summary>
    public readonly struct FloorBox
    {
        public readonly FloorPoint Center;public readonly float HalfX,HalfZ,Yaw;
        public FloorBox(FloorPoint center,float halfX,float halfZ,float yaw) {Center=center;HalfX=halfX;HalfZ=halfZ;Yaw=FloorPoint.Wrap(yaw);}
        public FloorPoint Right {get {float r=Yaw*MathF.PI/180;return new FloorPoint(MathF.Cos(r),-MathF.Sin(r));}}
        public FloorPoint Ahead=>FloorPoint.Forward(Yaw);
        public FloorBox Inflate(float margin)=>new FloorBox(Center,HalfX+margin,HalfZ+margin,Yaw);
        public float Circumradius=>MathF.Sqrt(HalfX*HalfX+HalfZ*HalfZ);
        public void Bounds(out float minX,out float minZ,out float maxX,out float maxZ)
        {
            FloorPoint r=Right,f=Ahead;float ex=MathF.Abs(r.X)*HalfX+MathF.Abs(f.X)*HalfZ,ez=MathF.Abs(r.Z)*HalfX+MathF.Abs(f.Z)*HalfZ;
            minX=Center.X-ex;maxX=Center.X+ex;minZ=Center.Z-ez;maxZ=Center.Z+ez;
        }
        public float Distance(FloorPoint p)
        {
            FloorPoint d=p-Center;float lx=MathF.Max(0,MathF.Abs(FloorPoint.Dot(d,Right))-HalfX),lz=MathF.Max(0,MathF.Abs(FloorPoint.Dot(d,Ahead))-HalfZ);
            return MathF.Sqrt(lx*lx+lz*lz);
        }
        /// <summary>Separating-axis overlap test; touching counts as overlap.</summary>
        public bool Overlaps(FloorBox o)
        {
            FloorPoint d=o.Center-Center,r=Right,f=Ahead,or=o.Right,of=o.Ahead;
            return !Separated(d,r,r,f,or,of,o) && !Separated(d,f,r,f,or,of,o) && !Separated(d,or,r,f,or,of,o) && !Separated(d,of,r,f,or,of,o);
        }
        private bool Separated(FloorPoint d,FloorPoint axis,FloorPoint r,FloorPoint f,FloorPoint or,FloorPoint of,FloorBox o)
        {
            float ra=HalfX*MathF.Abs(FloorPoint.Dot(r,axis))+HalfZ*MathF.Abs(FloorPoint.Dot(f,axis));
            float rb=o.HalfX*MathF.Abs(FloorPoint.Dot(or,axis))+o.HalfZ*MathF.Abs(FloorPoint.Dot(of,axis));
            return MathF.Abs(FloorPoint.Dot(d,axis))>ra+rb;
        }
    }

    /// <summary>Robot footprint in actor space (local x right, local z forward).</summary>
    public readonly struct FloorFootprint
    {
        public readonly float CenterX,CenterZ,HalfX,HalfZ;
        public FloorFootprint(float centerX,float centerZ,float halfX,float halfZ) {CenterX=centerX;CenterZ=centerZ;HalfX=halfX;HalfZ=halfZ;}
        public float TurningRadius=>new FloorPoint(MathF.Abs(CenterX)+HalfX,MathF.Abs(CenterZ)+HalfZ).Length;
        public FloorBox At(FloorPoint root,float yaw)
        {
            var axes=new FloorBox(root,HalfX,HalfZ,yaw);
            return new FloorBox(root+axes.Right*CenterX+axes.Ahead*CenterZ,HalfX,HalfZ,yaw);
        }
    }

    public enum GuardianLegNode { Pass, Approach, FinalService, FinalHome, FinalPlain }

    /// <summary>Service point plus the yaw held there; the rear shelf faces the service.</summary>
    public readonly struct GuardianDock
    {
        public readonly FloorPoint Point;public readonly float Yaw;
        public GuardianDock(FloorPoint point,float yaw) {Point=point;Yaw=FloorPoint.Wrap(yaw);}
        public FloorPoint Approach(float distance)=>Point+FloorPoint.Forward(Yaw)*distance;
    }

    /// <summary>Static floor: bounds, furniture boxes and a cached clearance field.</summary>
    public sealed class GuardianFloorGrid
    {
        public readonly float MinX,MinZ,Cell;public readonly int Width,Height;
        public readonly List<FloorBox> Obstacles=new List<FloorBox>();
        private float[] clearance;
        public GuardianFloorGrid(float minX,float minZ,float maxX,float maxZ,float cell)
        {
            MinX=minX;MinZ=minZ;Cell=cell;
            Width=Math.Max(1,(int)MathF.Ceiling((maxX-minX)/cell));Height=Math.Max(1,(int)MathF.Ceiling((maxZ-minZ)/cell));
        }
        public float MaxX=>MinX+Width*Cell;public float MaxZ=>MinZ+Height*Cell;
        public int Count=>Width*Height;
        public int Index(int x,int z)=>z*Width+x;
        public bool Inside(int x,int z)=>x>=0 && z>=0 && x<Width && z<Height;
        public FloorPoint CenterOf(int index)=>new FloorPoint(MinX+(index%Width+.5f)*Cell,MinZ+(index/Width+.5f)*Cell);
        public int CellX(float x)=>(int)MathF.Floor((x-MinX)/Cell);
        public int CellZ(float z)=>(int)MathF.Floor((z-MinZ)/Cell);
        public int IndexOf(FloorPoint p)=>Index(Math.Clamp(CellX(p.X),0,Width-1),Math.Clamp(CellZ(p.Z),0,Height-1));
        public void Build()
        {
            clearance=new float[Count];
            for(int i=0;i<clearance.Length;i++) clearance[i]=StaticClearance(CenterOf(i));
        }
        /// <summary>Exact distance to the nearest furniture box or floor edge.</summary>
        public float StaticClearance(FloorPoint p)
        {
            float best=MathF.Min(MathF.Min(p.X-MinX,MaxX-p.X),MathF.Min(p.Z-MinZ,MaxZ-p.Z));
            foreach(var o in Obstacles) best=MathF.Min(best,o.Distance(p));
            return best;
        }
        public float CachedClearance(int index)=>clearance[index];
        /// <summary>True when the box touches furniture or leaves the floor.</summary>
        public bool StaticHit(FloorBox box)
        {
            box.Bounds(out float x0,out float z0,out float x1,out float z1);
            if(x0<MinX || z0<MinZ || x1>MaxX || z1>MaxZ) return true;
            int cell=IndexOf(box.Center);
            if(clearance!=null && clearance[cell]-Cell*.71f>box.Circumradius) return false;
            foreach(var o in Obstacles) if(box.Overlaps(o)) return true;
            return false;
        }
        /// <summary>Indices of cells whose squares intersect the box.</summary>
        public void Rasterize(FloorBox box,List<int> output)
        {
            box.Bounds(out float x0,out float z0,out float x1,out float z1);
            int cx0=Math.Max(0,CellX(x0)),cx1=Math.Min(Width-1,CellX(x1)),cz0=Math.Max(0,CellZ(z0)),cz1=Math.Min(Height-1,CellZ(z1));
            float half=Cell*.5f,inner=MathF.Min(box.HalfX,box.HalfZ)-Cell*.71f;
            for(int z=cz0;z<=cz1;z++) for(int x=cx0;x<=cx1;x++)
            {
                var center=new FloorPoint(MinX+(x+.5f)*Cell,MinZ+(z+.5f)*Cell);
                // Cells deep inside the box need no separating-axis test.
                FloorPoint d=center-box.Center;
                if((MathF.Abs(FloorPoint.Dot(d,box.Right))<box.HalfX-Cell*.71f && MathF.Abs(FloorPoint.Dot(d,box.Ahead))<box.HalfZ-Cell*.71f)
                    || new FloorBox(center,half,half,0).Overlaps(box)) output.Add(Index(x,z));
            }
        }
    }

    /// <summary>A planned leg: polyline, node kinds, body yaw per segment and the ordered floor cells it sweeps.</summary>
    public sealed class GuardianLegPlan
    {
        public readonly List<FloorPoint> Points=new List<FloorPoint>();
        public readonly List<GuardianLegNode> Kinds=new List<GuardianLegNode>();
        /// <summary>Body yaw while travelling segment i (Points[i] to Points[i+1]).</summary>
        public readonly List<float> SegmentYaw=new List<float>();
        /// <summary>Arc length at Points[i].</summary>
        public readonly List<float> S=new List<float>();
        public float StartYaw,FinalYaw;public bool ParkAtYaw,LeavesDock,Reverse;
        public readonly Dictionary<int,float> Enter=new Dictionary<int,float>(),Exit=new Dictionary<int,float>();
        /// <summary>Swept cells sorted by entry arc length.</summary>
        public readonly List<int> Order=new List<int>();
        public float Length=>S.Count==0?0:S[S.Count-1];
        public FloorPoint Destination=>Points[Points.Count-1];
        public bool ReverseSegment(int segment)=>Reverse && segment==Points.Count-2;
        public float TurnAt(int node)=>node<=0 || node>=Points.Count-1?0:MathF.Abs(FloorPoint.DeltaAngle(SegmentYaw[node-1],SegmentYaw[node]));
        public FloorPoint PointAt(float s)
        {
            for(int i=1;i<Points.Count;i++) if(s<=S[i] || i==Points.Count-1)
            {float len=S[i]-S[i-1];return len<1e-5f?Points[i]:FloorPoint.Lerp(Points[i-1],Points[i],Math.Clamp((s-S[i-1])/len,0,1));}
            return Points[Points.Count-1];
        }
    }

    /// <summary>Planner settings in metres and degrees.</summary>
    public sealed class GuardianDirectOptions
    {
        public float Margin=.1f,CornerMargin=.2f,SampleStep=.1f,RotationStep=6f,ApproachDistance=1f,PathClearance=.12f;
        public float ReservedCellPenalty=1.2f,NarrowPenalty=1.5f,MaximumDetour=1.25f,ExitHysteresis=.12f,MaximumSoftShortcut=2.2f;
    }

    public enum GuardianDirectRequestResult { Committed, DestinationOccupied, NoRoute }

    /// <summary>Plans smooth obstacle-aware legs and keeps a FIFO passing order per floor cell.
    /// A robot may enter a cell only after every robot ahead of it in that cell's queue has left.
    /// A new leg keeps its existing queue positions and is appended to every other cell, so it only
    /// adds edges from itself to earlier robots; it also avoids every cell held by robots that already
    /// (transitively) wait for it. The waits-for graph therefore stays acyclic: no reservation deadlock.</summary>
    public sealed class GuardianDirectTraffic
    {
        private sealed class Leg
        {
            public GuardianLegPlan Plan;public float Progress;public int Cursor;
            public readonly HashSet<int> Entered=new HashSet<int>(),Exited=new HashSet<int>();
            public bool Pending(int c)=>!Entered.Contains(c) && !Exited.Contains(c);
        }
        public readonly GuardianFloorGrid Grid;public readonly GuardianDirectOptions Options;
        public readonly List<GuardianDock> Docks=new List<GuardianDock>();
        private readonly Dictionary<int,List<string>> queues=new Dictionary<int,List<string>>();
        private readonly Dictionary<string,Leg> legs=new Dictionary<string,Leg>();
        private readonly Dictionary<string,HashSet<int>> held=new Dictionary<string,HashSet<int>>();
        private readonly List<int> scratch=new List<int>();
        public int Commits {get;private set;}public string LastFailure {get;private set;}="";
        /// <summary>Changes whenever a cell is taken or released; lets blocked requests retry promptly.</summary>
        public int Version {get;private set;}
        public GuardianDirectTraffic(GuardianFloorGrid grid,GuardianDirectOptions options=null) {Grid=grid;Options=options??new GuardianDirectOptions();}

        public void Clear() {queues.Clear();legs.Clear();held.Clear();}
        private float pedestrianTurnClock=float.NaN;
        /// <summary>
        /// One route request per physics step for all people (walking guests and diners together), so their
        /// retries never stack several full searches into one frame. Robots plan on their own budget.
        /// </summary>
        public bool TakePedestrianTurn(float clock)
        {
            if(clock==pedestrianTurnClock) return false;
            pedestrianTurnClock=clock;return true;
        }
        public bool HasLeg(string id)=>legs.ContainsKey(id);
        public bool Holds(string id)=>held.ContainsKey(id);
        public GuardianLegPlan PlanOf(string id)=>legs.TryGetValue(id,out var leg)?leg.Plan:null;
        public float ProgressOf(string id)=>legs.TryGetValue(id,out var leg)?leg.Progress:0;
        public int HeldCells(string id)=>held.TryGetValue(id,out var cells)?cells.Count:0;
        public IEnumerable<string> Moving=>legs.Keys;
        public bool TryDock(FloorPoint p,float tolerance,out GuardianDock dock)
        {
            foreach(var d in Docks) if(FloorPoint.Distance(d.Point,p)<=tolerance) {dock=d;return true;}
            dock=default;return false;
        }

        /// <summary>Hold the robot's current footprint indefinitely (parked, after reset or at leg end).</summary>
        public void Park(string id,FloorBox footprint)
        {
            Remove(id);scratch.Clear();Grid.Rasterize(footprint.Inflate(Options.Margin),scratch);
            var cells=new HashSet<int>(scratch);held[id]=cells;Version++;
            // Physically present: first in each queue. Nobody else may queue on
            // indefinitely held cells (they are hard obstacles for new legs).
            foreach(int c in cells) Queue(c).Insert(0,id);
        }
        public void Remove(string id)
        {
            if(held.TryGetValue(id,out var cells)) foreach(int c in cells) Dequeue(c,id);
            held.Remove(id);legs.Remove(id);Version++;
        }
        private List<string> Queue(int cell) {if(!queues.TryGetValue(cell,out var q)) queues[cell]=q=new List<string>();return q;}
        private void Dequeue(int cell,string id) {if(!queues.TryGetValue(cell,out var q)) return;q.Remove(id);if(q.Count==0) queues.Remove(cell);}

        /// <summary>Robots queued ahead of this leg on cells it has not entered yet.</summary>
        private void Blockers(string id,Leg leg,HashSet<string> result)
        {
            for(int i=leg.Cursor;i<leg.Plan.Order.Count;i++)
            {
                int c=leg.Plan.Order[i];if(!leg.Pending(c)) continue;
                foreach(string other in queues[c]) {if(other==id) break;result.Add(other);}
            }
        }
        /// <summary>Diagnostic: a queue where an indefinitely held cell blocks a later robot, or null.</summary>
        public string FindIndefiniteBlock()
        {
            foreach(var pair in queues)
            {
                var q=pair.Value;
                for(int i=0;i+1<q.Count;i++)
                {
                    bool indefinite=!legs.TryGetValue(q[i],out var leg) || float.IsPositiveInfinity(leg.Plan.Exit.TryGetValue(pair.Key,out float x)?x:0);
                    if(indefinite) return "cell "+Grid.CenterOf(pair.Key)+" queue="+string.Join(">",q);
                }
            }
            return null;
        }
        /// <summary>Diagnostic: a waits-for cycle among current legs, or null when the graph is acyclic.</summary>
        public string FindCycle()
        {
            var waits=new Dictionary<string,HashSet<string>>();
            foreach(var pair in legs) {var set=new HashSet<string>();Blockers(pair.Key,pair.Value,set);waits[pair.Key]=set;}
            foreach(string start in waits.Keys)
            {
                var path=new List<string>();var seen=new HashSet<string>();
                if(Cycle(start,start,waits,path,seen)) return string.Join(">",path);
            }
            return null;
        }
        private static bool Cycle(string start,string at,Dictionary<string,HashSet<string>> waits,List<string> path,HashSet<string> seen)
        {
            path.Add(at);
            if(waits.TryGetValue(at,out var next)) foreach(string n in next)
            {
                if(n==start) {path.Add(n);return true;}
                if(seen.Add(n) && Cycle(start,n,waits,path,seen)) return true;
            }
            path.RemoveAt(path.Count-1);return false;
        }
        /// <summary>Robots that (transitively) wait for <paramref name="id"/> to leave a cell.</summary>
        public HashSet<string> WaitingFor(string id)
        {
            var waits=new Dictionary<string,HashSet<string>>();
            foreach(var pair in legs) {var set=new HashSet<string>();Blockers(pair.Key,pair.Value,set);waits[pair.Key]=set;}
            var result=new HashSet<string>();var stack=new Stack<string>();stack.Push(id);
            while(stack.Count>0)
            {
                string target=stack.Pop();
                foreach(var pair in waits) if(pair.Key!=id && !result.Contains(pair.Key) && pair.Value.Contains(target)) {result.Add(pair.Key);stack.Push(pair.Key);}
            }
            return result;
        }
        /// <summary>The robot this leg currently waits for at its frontier, or null.</summary>
        public string FrontierBlocker(string id)
        {
            if(!legs.TryGetValue(id,out var leg)) return null;
            for(int i=leg.Cursor;i<leg.Plan.Order.Count;i++)
            {
                int c=leg.Plan.Order[i];if(!leg.Pending(c)) continue;
                var q=queues[c];if(q[0]!=id) return q[0];
            }
            return null;
        }
        /// <summary>Diagnostic: the first cell this leg waits on and that cell's queue.</summary>
        public string DescribeWait(string id)
        {
            if(!legs.TryGetValue(id,out var leg)) return id+(held.ContainsKey(id)?" parked cells="+held[id].Count:" none");
            for(int i=leg.Cursor;i<leg.Plan.Order.Count;i++)
            {
                int c=leg.Plan.Order[i];if(!leg.Pending(c)) continue;
                var q=queues[c];if(q[0]!=id) return id+" waits cell "+Grid.CenterOf(c)+" enter="+leg.Plan.Enter[c].ToString("F2")+" exit="+leg.Plan.Exit[c].ToString("F2")+" queue="+string.Join(">",q)+" progress="+leg.Progress.ToString("F2");
            }
            return id+" free progress="+leg.Progress.ToString("F2");
        }
        /// <summary>Arc length the robot may reach before an earlier robot must clear a cell (infinite when free).</summary>
        public float Frontier(string id)
        {
            if(!legs.TryGetValue(id,out var leg)) return float.PositiveInfinity;
            for(int i=leg.Cursor;i<leg.Plan.Order.Count;i++)
            {
                int c=leg.Plan.Order[i];if(!leg.Pending(c)) continue;
                if(queues[c][0]!=id) return leg.Plan.Enter[c];
            }
            return float.PositiveInfinity;
        }
        /// <summary>Advance entry/exit bookkeeping from monotone progress along the leg.</summary>
        public void UpdateProgress(string id,float progress)
        {
            if(!legs.TryGetValue(id,out var leg)) return;
            leg.Progress=MathF.Max(leg.Progress,progress);var plan=leg.Plan;var cells=held[id];
            while(leg.Cursor<plan.Order.Count)
            {
                int c=plan.Order[leg.Cursor];if(plan.Enter[c]>leg.Progress+1e-4f) break;
                if(leg.Pending(c)) {if(queues[c][0]!=id) break;leg.Entered.Add(c);}
                leg.Cursor++;
            }
            scratch.Clear();
            foreach(int c in leg.Entered) if(plan.Exit[c]+Options.ExitHysteresis<leg.Progress) scratch.Add(c);
            foreach(int c in scratch) {leg.Entered.Remove(c);leg.Exited.Add(c);cells.Remove(c);Dequeue(c,id);}
            if(scratch.Count>0) Version++;
        }

        private string Holder(int cell,string except)
        {
            foreach(var pair in held) if(pair.Key!=except && pair.Value.Contains(cell)) return pair.Key;
            return "?";
        }
        /// <summary>Cells nobody else may sweep: parked footprints and every leg's final footprint.</summary>
        private HashSet<int> IndefiniteCells(string except)
        {
            var result=new HashSet<int>();
            foreach(var pair in held)
            {
                if(pair.Key==except) continue;
                if(!legs.TryGetValue(pair.Key,out var leg)) result.UnionWith(pair.Value);
                else foreach(int c in pair.Value) if(float.IsPositiveInfinity(leg.Plan.Exit[c])) result.Add(c);
            }
            return result;
        }

        /// <summary>Plan and commit a leg, or report why the robot must keep waiting.</summary>
        /// <param name="velocity">Current planar velocity; a moving robot first brakes along its heading.</param>
        /// <param name="commit">False for a diagnostic dry run.</param>
        public GuardianDirectRequestResult Request(string id,FloorPoint start,float startYaw,FloorFootprint footprint,FloorPoint goal,bool service,bool home,float homeYaw,
            out GuardianLegPlan plan,FloorPoint velocity=default,float deceleration=0,bool commit=true)
        {
            plan=null;
            // Never sweep a parked/arriving footprint or a cell of a robot already waiting for us.
            // The final footprint is held indefinitely, so it must not contain even our own queue
            // positions that a waiting robot stands behind.
            var final=IndefiniteCells(id);
            var waiting=WaitingFor(id);
            foreach(string w in waiting) if(held.TryGetValue(w,out var cells)) final.UnionWith(cells);
            var hard=new HashSet<int>(final);
            var soft=new HashSet<int>();
            foreach(var pair in held) if(pair.Key!=id && !waiting.Contains(pair.Key)) soft.UnionWith(pair.Value);
            held.TryGetValue(id,out var own);
            if(own!=null) {hard.ExceptWith(own);soft.ExceptWith(own);}
            float finalYaw=service && TryDock(goal,.2f,out var dock)?dock.Yaw:home?homeYaw:startYaw;
            scratch.Clear();Grid.Rasterize(footprint.At(goal,finalYaw).Inflate(Options.Margin),scratch);
            foreach(int c in scratch) if(final.Contains(c)) {LastFailure="destination occupied by "+Holder(c,id);return GuardianDirectRequestResult.DestinationOccupied;}
            plan=Plan(start,startYaw,footprint,goal,service,home,homeYaw,hard,soft,velocity,deceleration,final);
            if(plan==null) return GuardianDirectRequestResult.NoRoute;
            if(commit) Commit(id,plan,own);
            return GuardianDirectRequestResult.Committed;
        }

        private void Commit(string id,GuardianLegPlan plan,HashSet<int> own)
        {
            own=own??new HashSet<int>();
            HashSet<int> occupied=legs.TryGetValue(id,out var old)?old.Entered:own;
            foreach(int c in own) if(!plan.Enter.ContainsKey(c)) Dequeue(c,id);
            var leg=new Leg{Plan=plan};
            foreach(int c in plan.Order)
            {
                // Keep existing queue positions (no new waits-for edges there); append elsewhere.
                if(own.Contains(c) && Queue(c).Contains(id)) {if(occupied.Contains(c)) leg.Entered.Add(c);}
                else Queue(c).Add(id);
            }
            held[id]=new HashSet<int>(plan.Order);legs[id]=leg;Commits++;Version++;
            UpdateProgress(id,0);
        }

        /// <summary>Leg finished: release transit cells and keep the planned final footprint, whose
        /// queue positions are already ordered. (Re-rasterizing the actual pose could claim cells
        /// that others legitimately queue on.)</summary>
        public void Complete(string id)
        {
            if(!legs.TryGetValue(id,out var leg)) return;
            var cells=held[id];
            foreach(int c in new List<int>(cells)) if(!float.IsPositiveInfinity(leg.Plan.Exit[c])) {cells.Remove(c);Dequeue(c,id);}
            legs.Remove(id);Version++;
        }

        /// <summary>Spatial plan plus sweep. <paramref name="hard"/> cells must stay untouched;
        /// <paramref name="soft"/> cells (other robots' corridors) are avoided when it is cheap;
        /// the final footprint must avoid <paramref name="final"/> (defaults to hard).</summary>
        public GuardianLegPlan Plan(FloorPoint start,float startYaw,FloorFootprint footprint,FloorPoint goal,bool service,bool home,float homeYaw,HashSet<int> hard,HashSet<int> soft,
            FloorPoint velocity=default,float deceleration=0,HashSet<int> final=null)
        {
            hard=hard??new HashSet<int>();soft=soft??new HashSet<int>();final=final??hard;
            string failure=null;
            // The approach is where the robot turns its rear shelf to the service point.
            // Try a few distances along the service axis; parked neighbours may block one.
            foreach(float approach in new[]{Options.ApproachDistance,Options.ApproachDistance*.8f,Options.ApproachDistance*1.25f,Options.ApproachDistance*.65f})
            {
                var plan=PlanOnce(start,startYaw,footprint,goal,service,home,homeYaw,hard,soft,velocity,deceleration,final,approach);
                if(plan!=null) return plan;
                failure=failure??LastFailure;
                if(!service || LastFailure=="no clear route") break;
            }
            LastFailure=failure;return null;
        }

        private GuardianLegPlan PlanOnce(FloorPoint start,float startYaw,FloorFootprint footprint,FloorPoint goal,bool service,bool home,float homeYaw,HashSet<int> hard,HashSet<int> soft,
            FloorPoint velocity,float deceleration,HashSet<int> final,float approach)
        {
            hardDistance=null; // rebuilt by Search for this hard set
            var plan=new GuardianLegPlan{StartYaw=FloorPoint.Wrap(startYaw),ParkAtYaw=home,FinalYaw=FloorPoint.Wrap(homeYaw)};
            plan.Points.Add(start);plan.Kinds.Add(GuardianLegNode.Pass);
            FloorPoint from=start;
            GuardianDock goalDock=default;bool docking=service && TryDock(goal,.2f,out goalDock);
            float speed=velocity.Length;
            if(speed>.15f && deceleration>0 && FloorPoint.Dot(velocity,FloorPoint.Forward(startYaw))>.5f*speed)
            {
                // Re-plan while driving forward: keep braking straight ahead instead of
                // turning on the spot and backing up to where the request arrived.
                var lead=start+velocity*((speed/(2*deceleration)+.05f)/speed);
                if(!LeadBlocked(start,lead,FloorPoint.YawOf(velocity),footprint,hard)) {plan.Points.Add(lead);plan.Kinds.Add(GuardianLegNode.Pass);from=lead;}
            }
            else if(FloorPoint.Distance(start,goal)>.05f && TryDock(start,.2f,out var startDock) && MathF.Abs(FloorPoint.DeltaAngle(startYaw,startDock.Yaw))<20)
            {
                // Pull straight out, open front first; never turn beside furniture.
                from=startDock.Approach(Options.ApproachDistance);plan.Points.Add(from);plan.Kinds.Add(GuardianLegNode.Pass);plan.LeavesDock=true;
            }
            FloorPoint to=docking?goalDock.Approach(approach):goal;
            if(FloorPoint.Distance(from,to)>.05f)
            {
                var mid=Search(from,to,footprint,hard,soft);
                if(mid==null) {LastFailure="no clear route";return null;}
                for(int i=1;i<mid.Count;i++) {plan.Points.Add(mid[i]);plan.Kinds.Add(GuardianLegNode.Pass);}
            }
            else if(plan.Points.Count==1 && FloorPoint.Distance(from,to)>.005f) {plan.Points.Add(to);plan.Kinds.Add(GuardianLegNode.Pass);}
            if(docking && FloorPoint.Distance(goal,plan.Points[plan.Points.Count-1])>.02f)
            {
                if(plan.Points.Count==1) {plan.Points.Add(to);plan.Kinds.Add(GuardianLegNode.Pass);}
                plan.Kinds[plan.Kinds.Count-1]=GuardianLegNode.Approach;plan.Points.Add(goal);plan.Kinds.Add(GuardianLegNode.FinalService);plan.Reverse=true;
            }
            else plan.Kinds[plan.Kinds.Count-1]=home?GuardianLegNode.FinalHome:docking?GuardianLegNode.FinalService:GuardianLegNode.FinalPlain;
            plan.S.Add(0);
            for(int i=1;i<plan.Points.Count;i++) plan.S.Add(plan.S[i-1]+FloorPoint.Distance(plan.Points[i-1],plan.Points[i]));
            for(int i=0;i+1<plan.Points.Count;i++)
            {
                float travel=FloorPoint.YawOf(plan.Points[i+1]-plan.Points[i]);
                plan.SegmentYaw.Add(plan.ReverseSegment(i)?FloorPoint.Wrap(travel+180):travel);
            }
            if(docking) plan.FinalYaw=goalDock.Yaw;
            else if(!home) plan.FinalYaw=plan.SegmentYaw.Count>0?plan.SegmentYaw[plan.SegmentYaw.Count-1]:plan.StartYaw;
            if(!Sweep(plan,footprint,hard)) return null;
            foreach(var pair in plan.Exit) if(float.IsPositiveInfinity(pair.Value) && final.Contains(pair.Key)) {LastFailure="final footprint queued by another robot";return null;}
            return plan;
        }

        private bool LeadBlocked(FloorPoint a,FloorPoint b,float yaw,FloorFootprint footprint,HashSet<int> hard)
        {
            int steps=Math.Max(1,(int)MathF.Ceiling(FloorPoint.Distance(a,b)/Options.SampleStep));
            for(int k=1;k<=steps;k++) if(Blocked(footprint.At(FloorPoint.Lerp(a,b,(float)k/steps),yaw),hard)) return true;
            return false;
        }
        /// <summary>Furniture contact, or any cell the reservation would take (same margin) being hard.</summary>
        /// <param name="staticMargin">Extra furniture clearance; negative skips the furniture test.</param>
        private bool Blocked(FloorBox box,HashSet<int> hard,float margin=-1,float staticMargin=0)
        {
            if(staticMargin>=0 && Grid.StaticHit(staticMargin>0?box.Inflate(staticMargin):box)) return true;
            if(hard.Count==0) return false;
            if(margin<0) margin=Options.Margin;
            var inflated=box.Inflate(margin);
            // Conservative early-out: the 3x3 chamfer overestimates Euclidean distance by
            // under 9%; allow both cell half-diagonals (box-center cell and hard cell).
            if(hardDistance!=null && hardDistance[Grid.IndexOf(box.Center)]*Grid.Cell>(inflated.Circumradius+Grid.Cell*1.42f)*1.09f+.01f) return false;
            scratch.Clear();Grid.Rasterize(inflated,scratch);
            foreach(int c in scratch) if(hard.Contains(c)) return true;
            return false;
        }

        private bool Sweep(GuardianLegPlan plan,FloorFootprint footprint,HashSet<int> hard)
        {
            try {return SweepCells(plan,footprint,hard);}
            finally {touched.Clear();touchedSet.Clear();}
        }
        private bool SweepCells(GuardianLegPlan plan,FloorFootprint footprint,HashSet<int> hard)
        {
            if(enterBuffer==null || enterBuffer.Length!=Grid.Count) {enterBuffer=new float[Grid.Count];exitBuffer=new float[Grid.Count];}
            void Touch(FloorBox box,float s,bool final,float margin)
            {
                scratch.Clear();Grid.Rasterize(box.Inflate(margin),scratch);
                float x=final?float.PositiveInfinity:s;
                foreach(int c in scratch)
                {
                    if(touchedSet.Add(c)) {touched.Add(c);enterBuffer[c]=s;exitBuffer[c]=x;continue;}
                    if(s<enterBuffer[c]) enterBuffer[c]=s;
                    if(x>exitBuffer[c]) exitBuffer[c]=x;
                }
            }
            bool Rotate(FloorPoint at,float from,float to,float s,bool final,float margin,bool corner)
            {
                float delta=FloorPoint.DeltaAngle(from,to);int steps=Math.Max(1,(int)MathF.Ceiling(MathF.Abs(delta)/Options.RotationStep));
                for(int k=0;k<=steps;k++)
                {
                    var box=footprint.At(at,from+delta*k/steps);
                    // Corners may be cut slightly while passing, so keep furniture further away there.
                    if((k>0 || corner) && Blocked(box,hard,margin,corner?Options.CornerMargin*.75f:0)) return false;
                    Touch(box,s,final,margin);
                }
                return true;
            }
            int n=plan.Points.Count;
            float firstYaw=plan.SegmentYaw.Count>0?plan.SegmentYaw[0]:plan.FinalYaw;
            // The start pose may sit beside furniture by design, but its reserved cells must not
            // belong to anyone we would have to wait for (a moving re-plan then keeps its old leg).
            if(Blocked(footprint.At(plan.Points[0],plan.StartYaw),hard,Options.Margin,-1)) {LastFailure="start footprint shared";return false;}
            if(!Rotate(plan.Points[0],plan.StartYaw,firstYaw,0,n==1,Options.Margin,false)) {LastFailure="start turn blocked";return false;}
            for(int i=0;i+1<n;i++)
            {
                float yaw=plan.SegmentYaw[i],len=plan.S[i+1]-plan.S[i];int steps=Math.Max(1,(int)MathF.Ceiling(len/Options.SampleStep));
                for(int k=1;k<=steps;k++)
                {
                    float t=(float)k/steps;var box=footprint.At(FloorPoint.Lerp(plan.Points[i],plan.Points[i+1],t),yaw);
                    bool last=i+2==n && k==steps;
                    if(Blocked(box,hard)) {LastFailure="segment "+i+" blocked";return false;}
                    // A home leg's final turn is transit; only the parked box stays held.
                    Touch(box,plan.S[i]+len*t,last && !plan.ParkAtYaw,Options.Margin);
                }
                if(i+2<n)
                {
                    bool pass=plan.Kinds[i+1]==GuardianLegNode.Pass && plan.TurnAt(i+1)>1;
                    if(!Rotate(plan.Points[i+1],yaw,plan.SegmentYaw[i+1],plan.S[i+1],false,pass?Options.CornerMargin:Options.Margin,pass)) {LastFailure="turn at node "+(i+1)+" blocked";return false;}
                }
            }
            if(plan.ParkAtYaw && n>1)
            {
                if(!Rotate(plan.Points[n-1],plan.SegmentYaw[n-2],plan.FinalYaw,plan.Length,false,Options.Margin,false)) {LastFailure="parking turn blocked";return false;}
                Touch(footprint.At(plan.Points[n-1],plan.FinalYaw),plan.Length,true,Options.Margin);
            }
            else if(plan.ParkAtYaw) Touch(footprint.At(plan.Points[0],plan.FinalYaw),0,true,Options.Margin);
            foreach(int c in touched) {plan.Enter[c]=enterBuffer[c];plan.Exit[c]=exitBuffer[c];}
            plan.Order.AddRange(touched);
            plan.Order.Sort((a,b)=>{int k=plan.Enter[a].CompareTo(plan.Enter[b]);return k!=0?k:a.CompareTo(b);});
            return true;
        }
        private float[] enterBuffer,exitBuffer;private readonly List<int> touched=new List<int>();private readonly HashSet<int> touchedSet=new HashSet<int>();

        private List<FloorPoint> Search(FloorPoint from,FloorPoint to,FloorFootprint footprint,HashSet<int> hard,HashSet<int> soft)
        {
            // Keep room to turn on the spot anywhere along the route (also looks calmer).
            float radius=MathF.Max(footprint.HalfX+Options.PathClearance,footprint.TurningRadius+Options.Margin*.5f);
            var blocked=BlockedMask(from,to,radius,footprint,hard);
            // Avoid other robots' corridors when it is a small detour; otherwise take the
            // natural route and yield at the crossing (long swerves look erratic).
            if(softBuffer==null || softBuffer.Length!=Grid.Count) softBuffer=new bool[Grid.Count];
            var softMask=softBuffer;foreach(int c in soft) softMask[c]=true;
            try
            {
                var avoiding=SearchOnce(from,to,radius,blocked,soft.Count>0?softMask:null,footprint,hard,soft);
                if(avoiding==null || soft.Count==0) return avoiding;
                var direct=SearchOnce(from,to,radius,blocked,null,footprint,hard,soft);
                return direct!=null && Length(avoiding)>Length(direct)*Options.MaximumDetour+.3f?direct:avoiding;
            }
            finally {foreach(int c in soft) softMask[c]=false;}
        }
        private static float Length(List<FloorPoint> points) {float sum=0;for(int i=1;i<points.Count;i++) sum+=FloorPoint.Distance(points[i-1],points[i]);return sum;}

        private bool[] staticMask;private float staticMaskRadius=-1;
        private float[] hardDistance; // chamfer distance in cells to the nearest hard cell, for the current search
        // Per-search work arrays, one entry per grid cell. Reused: a fresh set per request allocated
        // megabytes each time and made the garbage collector stall the frame.
        private bool[] blockedBuffer,softBuffer,closedBuffer;private float[] distanceBuffer,costBuffer;private int[] parentBuffer;
        private bool[] BlockedMask(FloorPoint from,FloorPoint to,float radius,FloorFootprint footprint,HashSet<int> hard)
        {
            int count=Grid.Count;
            if(staticMask==null || staticMaskRadius!=radius)
            {
                staticMask=new bool[count];staticMaskRadius=radius;
                for(int i=0;i<count;i++) staticMask[i]=Grid.CachedClearance(i)<radius;
            }
            if(blockedBuffer==null || blockedBuffer.Length!=count) blockedBuffer=new bool[count];
            var blocked=blockedBuffer;Array.Copy(staticMask,blocked,count);
            if(hard.Count>0)
            {
                // Chamfer distance (in cells) to the nearest hard cell; block within the path radius.
                if(distanceBuffer==null || distanceBuffer.Length!=count) distanceBuffer=new float[count];
                var d=distanceBuffer;for(int i=0;i<count;i++) d[i]=float.PositiveInfinity;
                foreach(int c in hard) d[c]=0;
                int w=Grid.Width,h=Grid.Height;const float D1=1,D2=1.41421356f;
                for(int z=0;z<h;z++) for(int x=0;x<w;x++)
                {
                    int i=z*w+x;float v=d[i];
                    if(x>0) v=MathF.Min(v,d[i-1]+D1);
                    if(z>0) {v=MathF.Min(v,d[i-w]+D1);if(x>0) v=MathF.Min(v,d[i-w-1]+D2);if(x<w-1) v=MathF.Min(v,d[i-w+1]+D2);}
                    d[i]=v;
                }
                for(int z=h-1;z>=0;z--) for(int x=w-1;x>=0;x--)
                {
                    int i=z*w+x;float v=d[i];
                    if(x<w-1) v=MathF.Min(v,d[i+1]+D1);
                    if(z<h-1) {v=MathF.Min(v,d[i+w]+D1);if(x<w-1) v=MathF.Min(v,d[i+w+1]+D2);if(x>0) v=MathF.Min(v,d[i+w-1]+D2);}
                    d[i]=v;
                }
                float limit=(radius+Grid.Cell*.71f)/Grid.Cell;
                for(int i=0;i<count;i++) if(d[i]<=limit) blocked[i]=true;
                hardDistance=d;
            }
            else hardDistance=null;
            // Endpoint escapes: start/approach poses sit close to furniture by design.
            foreach(var p in new[]{from,to})
            {
                int cx=Grid.CellX(p.X),cz=Grid.CellZ(p.Z);
                for(int dz=-4;dz<=4;dz++) for(int dx=-4;dx<=4;dx++)
                {
                    int x=cx+dx,z=cz+dz;if(!Grid.Inside(x,z)) continue;int i=Grid.Index(x,z);
                    if(FloorPoint.Distance(Grid.CenterOf(i),p)<.32f && !hard.Contains(i) && Grid.CachedClearance(i)>footprint.HalfX*.5f) blocked[i]=false;
                }
            }
            blocked[Grid.IndexOf(from)]=blocked[Grid.IndexOf(to)]=false;
            return blocked;
        }

        /// <summary>Binary min-heap of (priority, cell) used by A*.</summary>
        private sealed class OpenSet
        {
            private (float f,int c)[] items=new (float,int)[256];public int Count;
            public void Clear()=>Count=0;
            public void Push(float f,int c)
            {
                if(Count==items.Length) Array.Resize(ref items,Count*2);
                int i=Count++;items[i]=(f,c);
                while(i>0) {int p=(i-1)/2;if(items[p].f<=items[i].f) break;(items[p],items[i])=(items[i],items[p]);i=p;}
            }
            public int Pop()
            {
                int top=items[0].c;items[0]=items[--Count];int i=0;
                while(true)
                {
                    int l=2*i+1,r=l+1,m=i;
                    if(l<Count && items[l].f<items[m].f) m=l;if(r<Count && items[r].f<items[m].f) m=r;
                    if(m==i) break;(items[m],items[i])=(items[i],items[m]);i=m;
                }
                return top;
            }
        }
        private readonly OpenSet open=new OpenSet();
        private static readonly int[] StepX={1,-1,0,0,1,1,-1,-1},StepZ={0,0,1,-1,1,-1,1,-1};

        private List<FloorPoint> SearchOnce(FloorPoint from,FloorPoint to,float radius,bool[] blocked,bool[] softMask,FloorFootprint footprint,HashSet<int> hard,HashSet<int> soft)
        {
            int count=Grid.Count,width=Grid.Width,startCell=Grid.IndexOf(from),goalCell=Grid.IndexOf(to);
            if(costBuffer==null || costBuffer.Length!=count) {costBuffer=new float[count];parentBuffer=new int[count];closedBuffer=new bool[count];}
            var g=costBuffer;var parent=parentBuffer;var closed=closedBuffer;Array.Clear(closed,0,count);
            for(int i=0;i<count;i++) {g[i]=float.PositiveInfinity;parent[i]=-1;}
            FloorPoint goalCenter=Grid.CenterOf(goalCell);
            open.Clear();g[startCell]=0;open.Push(FloorPoint.Distance(Grid.CenterOf(startCell),goalCenter),startCell);
            int[] dxs=StepX,dzs=StepZ;
            while(open.Count>0)
            {
                int cur=open.Pop();
                if(closed[cur]) continue;closed[cur]=true;if(cur==goalCell) break;
                int x=cur%width,z=cur/width;
                for(int k=0;k<8;k++)
                {
                    int nx=x+dxs[k],nz=z+dzs[k];if(!Grid.Inside(nx,nz)) continue;int nb=nz*width+nx;
                    if(blocked[nb] || closed[nb]) continue;
                    if(k>=4 && (blocked[z*width+nx] || blocked[nz*width+x])) continue;
                    float step=(k<4?1:1.41421356f)*Grid.Cell;
                    // Prefer open floor and other robots' free corridors; never forbid them.
                    float cost=step*(1+(softMask!=null && softMask[nb]?Options.ReservedCellPenalty:0)+Options.NarrowPenalty*MathF.Max(0,1-(Grid.CachedClearance(nb)-radius)/.6f));
                    float next=g[cur]+cost;if(next>=g[nb]) continue;
                    g[nb]=next;parent[nb]=cur;open.Push(next+FloorPoint.Distance(Grid.CenterOf(nb),goalCenter),nb);
                }
            }
            if(!closed[goalCell]) return null;
            var cells=new List<int>();for(int c=goalCell;c>=0;c=parent[c]) cells.Add(c);cells.Reverse();
            var raw=new List<FloorPoint>{from};
            for(int i=1;i+1<cells.Count;i++) raw.Add(Grid.CenterOf(cells[i]));
            raw.Add(to);
            // String-pull: one or two straight runs for most legs. Cheap mask test first,
            // then the exact footprint check; fall back to shorter shortcuts.
            var result=new List<FloorPoint>{from};int at=0;float yaw=float.NaN;
            while(at<raw.Count-1)
            {
                int best=at+1;
                for(int j=raw.Count-1;j>at+1;j--)
                    if(MaskVisible(raw[at],raw[j],blocked) && Visible(raw[at],raw[j],yaw,footprint,hard,soft)) {best=j;break;}
                yaw=FloorPoint.YawOf(raw[best]-raw[at]);result.Add(raw[best]);at=best;
            }
            return result;
        }
        private float Heuristic(int a,int b)=>FloorPoint.Distance(Grid.CenterOf(a),Grid.CenterOf(b));
        private bool MaskVisible(FloorPoint a,FloorPoint b,bool[] blocked)
        {
            float len=FloorPoint.Distance(a,b);int steps=Math.Max(1,(int)MathF.Ceiling(len/(Grid.Cell*.5f)));
            for(int k=1;k<steps;k++) if(blocked[Grid.IndexOf(FloorPoint.Lerp(a,b,(float)k/steps))]) return false;
            return true;
        }
        private bool Visible(FloorPoint a,FloorPoint b,float previousYaw,FloorFootprint footprint,HashSet<int> hard,HashSet<int> soft)
        {
            float len=FloorPoint.Distance(a,b);if(len<1e-4f) return true;float yaw=FloorPoint.YawOf(b-a);
            // All conditions must hold and none has side effects, so the cheapest go first: the turn at
            // the corner (most rejections) before sampling the whole segment, and the shared-corridor
            // length as soon as it is exceeded. Same answers, a fraction of the work on long routes.
            if(!float.IsNaN(previousYaw))
            {
                float delta=FloorPoint.DeltaAngle(previousYaw,yaw);int turns=Math.Max(1,(int)MathF.Ceiling(MathF.Abs(delta)/Options.RotationStep));
                for(int k=0;k<=turns;k++) if(Blocked(footprint.At(a,previousYaw+delta*k/turns),hard,Options.CornerMargin,Options.CornerMargin*.75f)) return false;
            }
            int steps=Math.Max(1,(int)MathF.Ceiling(len/Options.SampleStep));float softLength=0;
            for(int k=1;k<=steps;k++)
            {
                var box=footprint.At(FloorPoint.Lerp(a,b,(float)k/steps),yaw);
                // Short exposure to another robot's corridor is fine (a crossing); long shared
                // runs are left to the grid route, which already balanced the detour.
                if(soft.Count>0 && soft.Contains(Grid.IndexOf(box.Center)) && (softLength+=len/steps)>Options.MaximumSoftShortcut) return false;
                if(Grid.StaticHit(box.Inflate(Options.Margin*.5f)) && k<steps) return false;
                if(Blocked(box,hard)) return false;
            }
            return true;
        }
    }

    /// <summary>Pure path-following step shared by the Unity driver and offline tests.</summary>
    public static class GuardianDirectControl
    {
        public struct Input
        {
            public FloorPoint Position,Velocity;public float Yaw,YawRate,MaximumSpeed,Acceleration,Frontier,Progress;public int Node;
        }
        public struct Output
        {
            public FloorPoint Velocity;public float Yaw,Progress;public bool HoldYaw,AdvanceNode,Arrived,Waiting;public string Reason;
        }
        public const float ArrivalTolerance=.075f,StopGap=.04f,ServiceSpeed=.8f,SharpTurn=70;

        /// <summary>Arc length of the projection on the segment that ends at <paramref name="node"/>.</summary>
        public static float Project(GuardianLegPlan plan,int node,FloorPoint p)
        {
            if(plan.Points.Count<2) return 0;node=Math.Clamp(node,1,plan.Points.Count-1);
            FloorPoint a=plan.Points[node-1],ab=plan.Points[node]-a;float len2=FloorPoint.Dot(ab,ab);
            float t=len2<1e-8f?1:Math.Clamp(FloorPoint.Dot(p-a,ab)/len2,0,1);
            return plan.S[node-1]+t*(plan.S[node]-plan.S[node-1]);
        }

        public static Output Step(GuardianLegPlan plan,Input input)
        {
            var o=new Output{Yaw=input.Yaw,Progress=input.Progress};
            int n=plan.Points.Count;
            if(n<2) return Terminal(plan,input,o,plan.Kinds[0],plan.Points[0],0);
            int node=Math.Clamp(input.Node,1,n-1);
            float s=MathF.Max(input.Progress,Project(plan,node,input.Position));o.Progress=s;
            var kind=plan.Kinds[node];var target=plan.Points[node];float toNode=FloorPoint.Distance(input.Position,target),speed=input.Velocity.Length;
            float allowed=input.Frontier-s;bool reverse=plan.ReverseSegment(node-1);float bodyYaw=plan.SegmentYaw[node-1];
            if(node==1 && s<.02f && MathF.Abs(FloorPoint.DeltaAngle(input.Yaw,bodyYaw))>3)
            {
                // Turn in place before the first run; that sweep was reserved at s=0.
                if(allowed<=.001f) {o.HoldYaw=true;o.Waiting=true;o.Reason="waiting to turn";return o;}
                o.Yaw=bodyYaw;o.Velocity=(plan.Points[0]-input.Position)*2.5f;o.Reason="turning toward route";return o;
            }
            bool sharp=kind==GuardianLegNode.Pass && plan.TurnAt(node)>SharpTurn;
            if(kind==GuardianLegNode.Pass && ((!sharp && (Project(plan,node,input.Position)>=plan.S[node]-.02f || toNode<.15f+speed*.1f))
                || (sharp && toNode<ArrivalTolerance*1.5f && speed<.25f && allowed>plan.S[node]-s+.001f)))
            {
                // Pass gentle corners without stopping; sharp ones after a short stop and turn.
                o.AdvanceNode=true;
                if(sharp) {o.Yaw=plan.SegmentYaw[node];o.Velocity=new FloorPoint(0,0);o.Reason="turning at corner";return o;}
            }
            float limit=input.MaximumSpeed,stopAt=plan.S[n-1];bool sharpAhead=false;
            for(int i=node;i<n;i++)
            {
                if(plan.Kinds[i]!=GuardianLegNode.Pass) {stopAt=plan.S[i];break;}
                float turn=plan.TurnAt(i);if(turn>SharpTurn) {stopAt=plan.S[i];sharpAhead=true;break;}
                float corner=input.MaximumSpeed*MathF.Cos(turn*MathF.PI/180);
                limit=MathF.Min(limit,MathF.Sqrt(corner*corner+2*input.Acceleration*MathF.Max(0,plan.S[i]-s)));
            }
            float toStop=MathF.Max(0,stopAt-s);
            if(kind!=GuardianLegNode.Pass) toStop=MathF.Max(toStop,toNode); // overshoot: the projection clamps at the node
            limit=MathF.Min(limit,MathF.Min(toStop*2.5f,MathF.Sqrt(2*input.Acceleration*MathF.Max(0,toStop-.025f)))+(sharpAhead?.05f:0));
            if(!float.IsPositiveInfinity(allowed))
            {
                float room=allowed-StopGap;
                limit=MathF.Min(limit,MathF.Max(0,MathF.Min(room*2.5f,MathF.Sqrt(2*input.Acceleration*MathF.Max(0,room)))));
                if(room<=.01f) {o.Waiting=true;o.Reason="yielding at crossing";}
            }
            if(reverse) limit=MathF.Min(limit,ServiceSpeed);
            // Pure-pursuit aim, never past the next stop or the reservation frontier.
            float aim=MathF.Min(s+MathF.Min(.8f,.3f+.3f*speed),stopAt);
            if(!float.IsPositiveInfinity(allowed)) aim=MathF.Min(aim,MathF.Max(s,input.Frontier-StopGap));
            FloorPoint look=plan.PointAt(aim),toward=look-input.Position;
            if(toward.Length<.05f) toward=target-input.Position;
            float desiredYaw=reverse?bodyYaw:toward.Length>.05f?FloorPoint.YawOf(toward):bodyYaw;
            if(!reverse && kind!=GuardianLegNode.Pass && toNode<.6f && FloorPoint.Dot(toward,FloorPoint.Forward(input.Yaw))<0)
            {
                // Slightly past a stop node: back up straight instead of turning around.
                desiredYaw=input.Yaw;limit=MathF.Min(limit,.5f);
            }
            float yawError=MathF.Abs(FloorPoint.DeltaAngle(input.Yaw,desiredYaw));
            limit*=Math.Clamp(1-yawError/65f,0,1);
            o.Yaw=desiredYaw;o.Velocity=toward.Length>1e-4f?toward*(limit/toward.Length):new FloorPoint(0,0);
            if(o.Waiting && limit<.02f) {o.HoldYaw=true;o.Yaw=input.Yaw;}
            if(kind!=GuardianLegNode.Pass && toNode<ArrivalTolerance && speed*speed<.025f)
            {
                if(input.Frontier<=plan.S[node]+.001f) {o.Velocity=(target-input.Position)*2.5f;o.HoldYaw=true;o.Yaw=input.Yaw;o.Waiting=true;o.Reason="waiting for final footprint";return o;}
                return Terminal(plan,input,o,kind,target,node);
            }
            if(o.Reason==null) o.Reason=reverse?"reversing into service point":"moving / direct route";
            return o;
        }

        private static Output Terminal(GuardianLegPlan plan,Input input,Output o,GuardianLegNode kind,FloorPoint target,int node)
        {
            o.Velocity=(target-input.Position)*2.5f;
            float goalYaw=kind==GuardianLegNode.Approach?plan.SegmentYaw[node]:kind==GuardianLegNode.FinalPlain?input.Yaw:plan.FinalYaw;
            bool aligned=MathF.Abs(FloorPoint.DeltaAngle(input.Yaw,goalYaw))<3 && MathF.Abs(input.YawRate)<.12f;
            // A turn here sweeps cells entered at this node; wait until they are ours.
            if(!aligned && input.Frontier<=(plan.S.Count>node?plan.S[node]:0)+.001f) {o.HoldYaw=true;o.Yaw=input.Yaw;o.Waiting=true;o.Reason="waiting to turn";return o;}
            o.Yaw=goalYaw;
            if(aligned) {if(kind==GuardianLegNode.Approach) o.AdvanceNode=true;else o.Arrived=true;}
            else o.Reason=kind==GuardianLegNode.Approach?"aligning rear tray at approach":"parking turn";
            return o;
        }
    }
}
