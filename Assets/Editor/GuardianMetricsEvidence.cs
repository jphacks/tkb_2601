using System;
using System.IO;
using UnityEditor;
using UnityEngine;
namespace Guardian.UnitySim.Editor
{
    [InitializeOnLoad]public static class GuardianMetricsEvidence
    {
        private static bool running;private static int frames;static GuardianMetricsEvidence(){EditorApplication.update+=Tick;}
        public static void Headless(){if(!Application.isBatchMode||SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)throw new InvalidOperationException("headless only");GuardianRuntimeMetrics.Open();running=true;frames=0;}
        [Serializable]private sealed class Report{public bool available,profilerValid,memoryValid;public long systemPositive,profilerPositive,current,last,working,privateBytes;public string scope,unit;}
        private static void Tick()
        {
            if(!running||++frames<4)return;running=false;var r=new Report{available=GuardianRuntimeMetrics.Validate(),systemPositive=GuardianRuntimeMetrics.SystemPositiveControl,profilerPositive=GuardianRuntimeMetrics.ProfilerPositiveControl,scope=GuardianRuntimeMetrics.AllocationScope,profilerValid=GuardianRuntimeMetrics.ProfilerValid,current=GuardianRuntimeMetrics.ProfilerCurrent,last=GuardianRuntimeMetrics.ProfilerLast,unit=GuardianRuntimeMetrics.ProfilerUnit};r.memoryValid=GuardianRuntimeMetrics.ProcessMemory(out r.working,out r.privateBytes);Directory.CreateDirectory("docs/evidence/performance");File.WriteAllText("docs/evidence/performance/metrics-control.json",JsonUtility.ToJson(r,true)+"\n");EditorApplication.Exit(r.available&&r.memoryValid?0:1);
        }
    }
}
