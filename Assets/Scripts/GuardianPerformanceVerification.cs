using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Stopwatch=System.Diagnostics.Stopwatch;
namespace Guardian.UnitySim
{
    public sealed class GuardianPerformanceVerification : MonoBehaviour
    {
        [Serializable]public sealed class Row
        {
            public string layout;public int population,robots,people,samples=128,possiblePairs,maxCandidates,cells,fallbacks;public float width,depth;
            public double meanCandidates,allP50Us,allP95Us,allP99Us,hashP50Us,hashP95Us,hashP99Us;
            public long allGcBytes,hashGcBytes,managedBegin,managedEnd,oracleHazards,detectedHazards,misses,falseEvents;public int maxDetectionDelayFrames;
            public double nativeP50Ms,nativeP95Ms,nativeP99Ms;public long nativeGcBytes;public float nativeMaxPenetration;
            public string latencyScope="Same-snapshot oracle comparison; wall cost is guard update time, not sensor/network reaction";
            public string nativeScope="Separate local PhysX dither: half dynamic robot boxes/half kinematic person capsules, no art/navigation/jobs, no high-speed avoidance claim";
        }
        [Serializable]private sealed class Report
        {
            public string status="FAIL",error="",scope="Managed production risk-kernel replay and separate generic PhysX body dither, NOT 100-robot restaurant throughput/FPS",unity,cpu;
            public double realSeconds;public List<Row> rows=new List<Row>();
            public string allocationScope;public long allocationPositiveControl,unsupportedSystemPositiveControl;
        }
        private readonly Report report=new Report();public bool Done{get;private set;}public bool Passed=>report.status=="PASS";public object ExportReport=>report;
        private GuardianPlanarDynamics model;private double began;
        private IEnumerator Start()
        {
            began=Time.realtimeSinceStartupAsDouble;report.unity=Application.unityVersion;report.cpu=SystemInfo.processorType;
            GuardianRuntimeMetrics.Open();yield return null;
            if(!GuardianRuntimeMetrics.Validate()){Finish(new InvalidOperationException("Allocation meter positive-control failed; zero is not a measurement"));yield break;}
            report.allocationScope=GuardianRuntimeMetrics.AllocationScope;report.allocationPositiveControl=GuardianRuntimeMetrics.ProfilerPositiveControl;report.unsupportedSystemPositiveControl=GuardianRuntimeMetrics.SystemPositiveControl;
            var go=new GameObject("Risk benchmark parameter body");var body=go.AddComponent<Rigidbody>();body.useGravity=false;model=new GuardianPlanarDynamics(body,new Vector3(.9f,1.7f,.9f),new GuardianPhysicalSettings{Enabled=true});
            foreach(string layout in new[]{"fixed-density","fixed-area","coincident-worst"})foreach(int n in new[]{10,25,50,100})
            {
                try{Run(n,layout);}catch(Exception ex){Finish(ex);yield break;}yield return null;
            }
            Destroy(go);Finish(null);
        }
        private void Run(int n,string layout)
        {
            var row=new Row{population=n,robots=(n+1)/2,people=n/2,layout=layout};row.possiblePairs=row.robots*row.people;
            if(layout=="fixed-density"){row.width=Mathf.Sqrt(n*6.25f*1.6f);row.depth=row.width/1.6f;}else{row.width=20;row.depth=12.5f;}
            var agents=new GuardianSafetyAgent[n];var all=new GuardianSafetyBroadphase();var hash=new GuardianSafetyBroadphase();var fullStamp=new int[n*n];var gridStamp=new int[n*n];
            var ta=new double[row.samples];var tg=new double[row.samples];row.managedBegin=GC.GetTotalMemory(false);
            for(int f=-32;f<row.samples;f++)
            {
                Fill(agents,row,f+32);int stamp=f+33;long aa,ag;double ua,ug;
                if((f&1)==0){Measure(all,agents,false,fullStamp,stamp,out ua,out aa);Measure(hash,agents,true,gridStamp,stamp,out ug,out ag);}
                else{Measure(hash,agents,true,gridStamp,stamp,out ug,out ag);Measure(all,agents,false,fullStamp,stamp,out ua,out aa);}
                if(f<0)continue;ta[f]=ua;tg[f]=ug;row.allGcBytes+=aa;row.hashGcBytes+=ag;row.meanCandidates+=hash.Pairs.Count;row.maxCandidates=Math.Max(row.maxCandidates,hash.Pairs.Count);row.cells=Math.Max(row.cells,hash.CellCount);
                for(int i=0;i<n;i++)if(agents[i].Robot)for(int j=0;j<n;j++)if(!agents[j].Robot)
                {
                    bool expected=fullStamp[i*n+j]==stamp,found=gridStamp[i*n+j]==stamp;
                    if(expected)row.oracleHazards++;if(found)row.detectedHazards++;if(expected&&!found)row.misses++;if(found&&!expected)row.falseEvents++;
                }
            }
            row.meanCandidates/=row.samples;row.fallbacks=hash.Fallbacks;
            row.allP50Us=Percentile(ta,.5);row.allP95Us=Percentile(ta,.95);row.allP99Us=Percentile(ta,.99);row.hashP50Us=Percentile(tg,.5);row.hashP95Us=Percentile(tg,.95);row.hashP99Us=Percentile(tg,.99);
            if(layout!="coincident-worst")NativeDither(row);row.managedEnd=GC.GetTotalMemory(false);report.rows.Add(row);
            if(row.misses!=0||row.falseEvents!=0)throw new InvalidOperationException("Broadphase oracle mismatch at N="+n+" "+layout+" misses="+row.misses+" false="+row.falseEvents);
            if(row.nativeMaxPenetration>.001f)throw new InvalidOperationException("Generic low-speed dither overlapped; not a fleet navigation test");
        }
        private void Fill(GuardianSafetyAgent[] agents,Row row,int f)
        {
            int cols=Mathf.CeilToInt(Mathf.Sqrt(row.population*row.width/row.depth)),rows=Mathf.CeilToInt((float)row.population/cols);
            for(int i=0;i<agents.Length;i++)
            {
                float t=f*.02f,phase=i*2.399963f;bool robot=(i&1)==0;
                Vector3 p=new Vector3(((i%cols)+.5f)*row.width/cols-row.width*.5f,0,((i/cols)+.5f)*row.depth/rows-row.depth*.5f);
                p+=new Vector3(Mathf.Sin(t+phase),0,Mathf.Cos(t*.7f+phase))*.3f;if(row.layout=="coincident-worst")p=Vector3.zero;
                Vector3 direction=new Vector3(Mathf.Sin(phase+t),0,Mathf.Cos(phase+t));
                agents[i]=new GuardianSafetyAgent{Robot=robot,Position=p,Velocity=direction*(robot?(i%4==0?3.8f:1.9f):.8f),Intent=robot?new Vector3(direction.z,0,-direction.x)*3.8f:Vector3.zero,Radius=robot?.74f:.40f,MaximumSpeed=3.8f,Model=robot?model:null};
            }
            // Fast future cross-cell motion and negative cell boundaries exercise
            // swept envelopes rather than only nearby current positions.
            if(f%19==0){var a=agents[0];a.Position=new Vector3(-2.500001f,0,-.000001f);a.Velocity=Vector3.right*20;agents[0]=a;}
        }
        private static void Measure(GuardianSafetyBroadphase search,GuardianSafetyAgent[] a,bool grid,int[] stamps,int stamp,out double us,out long allocated)
        {
            long before=GuardianRuntimeMetrics.Allocated(),start=Stopwatch.GetTimestamp();search.Build(a,a.Length,.02f,grid);
            for(int i=0;i<search.Pairs.Count;i++){var p=search.Pairs[i];if(GuardianSafetyBroadphase.Hazard(a[p.Robot],a[p.Person],.02f))stamps[p.Robot*a.Length+p.Person]=stamp;}
            us=(Stopwatch.GetTimestamp()-start)*1000000.0/Stopwatch.Frequency;allocated=GuardianRuntimeMetrics.Allocated()-before;
        }
        private void NativeDither(Row row)
        {
            var scene=SceneManager.CreateScene("Generic body dither "+row.population+" "+row.layout,new CreateSceneParameters(LocalPhysicsMode.Physics3D));var world=scene.GetPhysicsScene();int n=row.population;
            var bodies=new Rigidbody[n];var shapes=new Collider[n];var motors=new GuardianPlanarDynamics[n];var anchors=new Vector3[n];int cols=Mathf.CeilToInt(Mathf.Sqrt(n*row.width/row.depth)),rows=Mathf.CeilToInt((float)n/cols);
            for(int i=0;i<n;i++)
            {
                var go=new GameObject("Original generic agent "+i);SceneManager.MoveGameObjectToScene(go,scene);anchors[i]=new Vector3(((i%cols)+.5f)*row.width/cols-row.width*.5f,0,((i/cols)+.5f)*row.depth/rows-row.depth*.5f);go.transform.position=anchors[i];
                var b=go.AddComponent<Rigidbody>();b.useGravity=false;b.constraints=RigidbodyConstraints.FreezePositionY|RigidbodyConstraints.FreezeRotationX|RigidbodyConstraints.FreezeRotationZ;b.solverIterations=12;b.solverVelocityIterations=6;bodies[i]=b;
                if((i&1)==0){var s=go.AddComponent<BoxCollider>();s.size=new Vector3(.9f,1.7f,.9f);s.center=Vector3.up*.85f;shapes[i]=s;motors[i]=new GuardianPlanarDynamics(b,s.size,new GuardianPhysicalSettings{Enabled=true});}
                else{b.isKinematic=true;var s=go.AddComponent<CapsuleCollider>();s.radius=.4f;s.height=1.83f;s.center=Vector3.up*.93f;shapes[i]=s;}
            }
            Physics.SyncTransforms();var times=new double[128];
            for(int f=-32;f<128;f++)
            {
                float t=(f+32)*.02f;long bytes=GuardianRuntimeMetrics.Allocated(),start=Stopwatch.GetTimestamp();
                for(int i=0;i<n;i++){Vector3 goal=anchors[i]+new Vector3(Mathf.Sin(t+i),0,Mathf.Cos(t+i))*.12f;if(motors[i]!=null)motors[i].Apply(Vector3.ClampMagnitude((goal-bodies[i].position)*2,.4f),0,.02f,false);else bodies[i].MovePosition(goal);}
                world.Simulate(.02f);if(f>=0){times[f]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;row.nativeGcBytes+=GuardianRuntimeMetrics.Allocated()-bytes;}
                if(f>=0&&f%16==0)for(int i=0;i<n;i++)for(int j=i+1;j<n;j++)if(Physics.ComputePenetration(shapes[i],bodies[i].position,bodies[i].rotation,shapes[j],bodies[j].position,bodies[j].rotation,out var axis,out float depth))row.nativeMaxPenetration=Mathf.Max(row.nativeMaxPenetration,depth);
            }
            row.nativeP50Ms=Percentile(times,.5);row.nativeP95Ms=Percentile(times,.95);row.nativeP99Ms=Percentile(times,.99);for(int i=0;i<n;i++)DestroyImmediate(bodies[i].gameObject);SceneManager.UnloadSceneAsync(scene);
        }
        private static double Percentile(double[] values,double quantile){Array.Sort(values);return values[Math.Min(values.Length-1,(int)Math.Ceiling(values.Length*quantile)-1)];}
        private void Finish(Exception ex){report.status=ex==null?"PASS":"FAIL";report.error=ex?.ToString()??"";report.realSeconds=Time.realtimeSinceStartupAsDouble-began;Done=true;UnityEngine.Debug.Log("GUARDIAN_PERFORMANCE "+report.status);if(ex!=null)UnityEngine.Debug.LogException(ex);}
    }
}
