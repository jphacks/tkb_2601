using System;
using System.Runtime.InteropServices;
using Unity.Profiling;
namespace Guardian.UnitySim
{
    public static class GuardianRuntimeMetrics
    {
        [StructLayout(LayoutKind.Sequential)]private struct Memory
        {public uint cb,faults;public UIntPtr peakWorking,working,peakPaged,paged,peakNonpaged,nonpaged,pagefile,peakPagefile,privateBytes;}
        [DllImport("psapi.dll",SetLastError=true)]private static extern bool GetProcessMemoryInfo(IntPtr process,out Memory counters,uint size);
        private static ProfilerRecorder gc;private static bool opened;
        public static bool AllocationAvailable {get;private set;}public static string AllocationScope {get;private set;}="unvalidated";
        public static long SystemPositiveControl {get;private set;}public static long ProfilerPositiveControl {get;private set;}
        public static void Open(){if(opened)return;gc=ProfilerRecorder.StartNew(ProfilerCategory.Memory,"GC Allocated In Frame");opened=true;}
        public static bool Validate()
        {
            long a=GC.GetAllocatedBytesForCurrentThread(),x=gc.Valid?gc.CurrentValue:0;var bytes=new byte[1024*1024];bytes[0]=1;SystemPositiveControl=GC.GetAllocatedBytesForCurrentThread()-a;ProfilerPositiveControl=gc.Valid?gc.CurrentValue-x:0;GC.KeepAlive(bytes);
            AllocationAvailable=SystemPositiveControl>=1024*1024 || ProfilerPositiveControl>=1024*1024;
            AllocationScope=SystemPositiveControl>=1024*1024?"System current-thread allocation counter, positive-control validated":ProfilerPositiveControl>=1024*1024?"Unity GC Allocated In Frame CurrentValue delta; ALL threads during measurement window, positive-control validated":"UNAVAILABLE: positive-control failed; zero is not an allocation measurement";
            return AllocationAvailable;
        }
        public static long Allocated()=>!AllocationAvailable?-1:SystemPositiveControl>=1024*1024?GC.GetAllocatedBytesForCurrentThread():gc.CurrentValue;
        public static bool ProcessMemory(out long working,out long privateBytes)
        {Memory m;bool ok=GetProcessMemoryInfo(new IntPtr(-1),out m,(uint)Marshal.SizeOf(typeof(Memory)));working=ok?(long)m.working.ToUInt64():-1;privateBytes=ok?(long)m.privateBytes.ToUInt64():-1;return ok;}
        public static bool ProfilerValid=>gc.Valid;public static long ProfilerCurrent=>gc.Valid?gc.CurrentValue:-1;public static long ProfilerLast=>gc.Valid?gc.LastValue:-1;public static string ProfilerUnit=>gc.Valid?gc.UnitType.ToString():"unavailable";
    }
}
