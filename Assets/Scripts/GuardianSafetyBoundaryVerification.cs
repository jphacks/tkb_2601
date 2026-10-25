using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
namespace Guardian.UnitySim
{
    /// <summary>Actual hash/predictor and native PhysX boundary checks; no benchmark or presentation claim.</summary>
    public sealed class GuardianSafetyBoundaryVerification : MonoBehaviour
    {
        [Serializable]public sealed class Case {public string name;public bool passed;public int pairs,fallbacks,hits,unique,capacity,saturations;public double milliseconds;}
        [Serializable]public sealed class Report
        {public string status="FAIL",error="",scope="Production broadphase/physical forecast boundary inputs and native OverlapBox buffer growth/All fallback; owned fixtures only";public List<Case> cases=new List<Case>();}
        private readonly Report report=new Report();public bool Done {get;private set;}public bool Passed=>report.status=="PASS";public object ExportReport=>report;
        private void Start()
        {
            GameObject modelObject=null;
            try
            {
                modelObject=new GameObject("Boundary physical model");var body=modelObject.AddComponent<Rigidbody>();body.useGravity=false;
                var settings=new GuardianPhysicalSettings();var model=new GuardianPlanarDynamics(body,new Vector3(.6f,1.4f,.8f),settings);
                var agents=new[]{new GuardianSafetyAgent{Robot=true,Position=Vector3.zero,Velocity=Vector3.right,Intent=Vector3.right,Radius=.5f,MaximumSpeed=2,Model=model},new GuardianSafetyAgent{Position=new Vector3(50,0,0),Radius=.4f,MaximumSpeed=1.4f},new GuardianSafetyAgent{Robot=true,Position=new Vector3(0,0,2),Velocity=Vector3.right,Intent=Vector3.right,Radius=.5f,MaximumSpeed=2,Model=model},new GuardianSafetyAgent{Position=new Vector3(60,0,0),Radius=.4f,MaximumSpeed=1.4f}};
                foreach(float brake in new[]{float.Epsilon,1e-20f})
                {settings.BrakeForce=brake;Fallback("tiny-brake-"+brake.ToString("R",System.Globalization.CultureInfo.InvariantCulture),agents,.02f,2.5f);var watch=Stopwatch.StartNew();bool hazard=GuardianSafetyBroadphase.Hazard(agents[0],agents[1],.02f);watch.Stop();report.cases.Add(new Case{name="unbounded-forecast-"+brake.ToString("R",System.Globalization.CultureInfo.InvariantCulture),passed=hazard&&watch.Elapsed.TotalSeconds<1,milliseconds=watch.Elapsed.TotalMilliseconds});}
                settings.BrakeForce=520;
                var saved=agents[0];agents[0].Position=new Vector3(1e30f,0,0);Fallback("finite-huge-position",agents,.02f,2.5f);agents[0]=saved;
                agents[0].Radius=1e10f;Fallback("signed-int-span-overflow",agents,.02f,2.5f);agents[0]=saved;
                Fallback("tiny-cell-global-range",agents,.02f,float.Epsilon);Fallback("zero-cell",agents,.02f,0);Fallback("nan-cell",agents,.02f,float.NaN);Fallback("invalid-step",agents,0,2.5f);
                agents[0].Position=new Vector3(float.NaN,0,0);Fallback("nan-agent",agents,.02f,2.5f);report.cases.Add(new Case{name="invalid-agent-fails-closed",passed=GuardianSafetyBroadphase.Hazard(agents[0],agents[1],.02f)});agents[0]=saved;
                // A negative cell seam still contains the actual nearby hazard pair, with no duplicate.
                agents[0].Position=new Vector3(-2.5f,0,-2.5f);agents[1].Position=new Vector3(-2.49f,0,-2.49f);var hash=new GuardianSafetyBroadphase();hash.Build(agents,agents.Length,.02f);int found=0;foreach(var pair in hash.Pairs)if(pair.Robot==0&&pair.Person==1)found++;
                report.cases.Add(new Case{name="negative-cell-seam",passed=found==1&&hash.Fallbacks==0,pairs=hash.Pairs.Count,fallbacks=hash.Fallbacks});
                Box(64);Box(4097);
                report.status=report.cases.TrueForAll(c=>c.passed)?"PASS":"FAIL";if(report.status!="PASS")report.error="See individual boundary cases";
            }
            catch(Exception ex){report.error=ex.ToString();}
            finally {if(modelObject!=null)DestroyImmediate(modelObject);Done=true;UnityEngine.Debug.Log("GUARDIAN_SAFETY_BOUNDARY_TEST "+report.status);}
        }
        private void Fallback(string name,GuardianSafetyAgent[] agents,float dt,float cell)
        {
            var watch=Stopwatch.StartNew();var hash=new GuardianSafetyBroadphase{CellSize=cell};hash.Build(agents,agents.Length,dt);watch.Stop();var unique=new HashSet<ulong>();bool typed=true;
            foreach(var pair in hash.Pairs){typed&=agents[pair.Robot].Robot&&!agents[pair.Person].Robot;unique.Add(((ulong)(uint)pair.Robot<<32)|(uint)pair.Person);}
            report.cases.Add(new Case{name=name,passed=typed&&hash.Fallbacks==1&&hash.Pairs.Count==4&&unique.Count==4&&watch.Elapsed.TotalSeconds<1,pairs=hash.Pairs.Count,unique=unique.Count,fallbacks=hash.Fallbacks,milliseconds=watch.Elapsed.TotalMilliseconds});
        }
        private void Box(int count)
        {
            var root=new GameObject("Owned overlap-box fixtures "+count);Vector3 center=new Vector3(200,200,200);
            try
            {
                var expected=new HashSet<int>();for(int i=0;i<count;i++){var go=new GameObject("box "+i);go.transform.SetParent(root.transform);go.transform.position=center+new Vector3(i%17*.1f-.8f,i/17%17*.1f-.8f,i/289*.1f-.8f);var c=go.AddComponent<BoxCollider>();c.size=Vector3.one*.01f;expected.Add(c.GetInstanceID());}
                Physics.SyncTransforms();var query=new GuardianQueryBuffer();int hits=query.Box(center,Vector3.one*2,Quaternion.Euler(0,17,0));var unique=new HashSet<int>();bool owned=true;for(int i=0;i<hits;i++){int id=query.Colliders[i].GetInstanceID();unique.Add(id);owned&=expected.Contains(id);}
                report.cases.Add(new Case{name=count==64?"box-nonalloc-growth":"box-all-fallback",passed=owned&&hits==count&&unique.Count==count&&query.Saturations==(count==64?2:8)&&query.Colliders.Length==(count==64?128:4097),hits=hits,unique=unique.Count,capacity=query.Colliders.Length,saturations=query.Saturations});
            }
            finally {DestroyImmediate(root);Physics.SyncTransforms();}
        }
    }
}
