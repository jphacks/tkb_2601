using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using Stopwatch=System.Diagnostics.Stopwatch;
namespace Guardian.UnitySim
{
    [DefaultExecutionOrder(10000)]public sealed class GuardianEnduranceVerification : MonoBehaviour
    {
        [Serializable]public sealed class Report
        {
            public string status="RUNNING",error="",startedUtc,finishedUtc,scope="One persistent Editor process, repeated FULLY DRAINED 3/5-robot connected scenarios; epoch resets bound history, not uninterrupted business history",revision;
            public int processId,epoch,completedEpochs,threeRobotEpochs,fiveRobotEpochs,fleet,trackedAgents,completedTasks,physicalSamples,maxTasks,maxRequests,maxDishes,maxDishEvents,maxHumanLog,maxTrace;
            public double requestedRealSeconds,realSeconds,simulatedSeconds,controllerP50Us,controllerP95Us,controllerP99Us,meanControllerGcBytes,drainingSeconds;
            public long managedBytes,nativeAllocatedBytes,workingSetBytes,privateBytes,peakManagedBytes,peakNativeAllocatedBytes,peakPrivateBytes,controllerGcBytes,logBytes;
            public float maxHumanPenetration;public bool smoke,measureIncludesVerificationCallbacks=true;
            public bool allocationAvailable,memoryAvailable;public string allocationScope;public long allocationPositiveControl;public int activeTasks,currentScenarioPhase;
            public string timingScope="Controller FixedUpdate scripts incl inventory/decisions/guard/verification; excludes native Physics.Simulate, rendering and Editor overhead";
            public string limits="One logical CPU / BelowNormal / 2 job workers externally; Update sleeps 20ms; logs budget16MiB checked every5s; private-memory budget2GiB; trace128/human512/scene12. Task/dish histories reset ONLY after all jobs AND robots/claims drain. CSV snapshot every5s plus one overwritten scenario CSV.";
        }
        public Report Data {get;}=new Report();public bool Done{get;private set;}public bool Passed=>Data.status=="PASS";public string OutputRoot {get;private set;}
        private GuardianInteractiveExperience experience;private GuardianSimulator sim;private GuardianInteractiveVerification scenario;
        private readonly Stopwatch watch=new Stopwatch();private readonly double[] times=new double[120000];private int timeCount;private long lastSample=-1;private float completedSim;private double nextSample,drainAt;
        private StreamWriter memory;private System.Diagnostics.Process process;private bool finishing,initialized;
        private IEnumerator Start()
        {
            string[] args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-guardianEnduranceSeconds");Data.requestedRealSeconds=at>=0?double.Parse(args[at+1],System.Globalization.CultureInfo.InvariantCulture):1800;
            Data.smoke=Array.IndexOf(args,"-guardianEnduranceSmoke")>=0;int output=Array.IndexOf(args,"-guardianEnduranceOutput");OutputRoot=output>=0?args[output+1]:Path.Combine(Directory.GetParent(Application.dataPath).FullName,"EnduranceRuns/current");Directory.CreateDirectory(OutputRoot);
            int rev=Array.IndexOf(args,"-guardianRevision");Data.revision=rev>=0?args[rev+1]:"uncommitted-preflight";
            experience=GetComponent<GuardianInteractiveExperience>();while(!experience.Ready)yield return null;sim=experience.Sim;experience.enabled=false;sim.Settings.VerboseRuntimeLogs=false;
            GuardianRuntimeMetrics.Open();yield return null;Data.allocationAvailable=GuardianRuntimeMetrics.Validate();Data.allocationScope=GuardianRuntimeMetrics.AllocationScope;Data.allocationPositiveControl=GuardianRuntimeMetrics.ProfilerPositiveControl;
            if(!Data.allocationAvailable){Finish(new InvalidOperationException("GC positive-control failed"));yield break;}sim.MeasureFixedPerformance=true;
            Application.runInBackground=true;Application.targetFrameRate=30;QualitySettings.vSyncCount=0;Time.maximumDeltaTime=.08f;process=System.Diagnostics.Process.GetCurrentProcess();Data.processId=process.Id;Data.startedUtc=DateTime.UtcNow.ToString("o");
            memory=new StreamWriter(Path.Combine(OutputRoot,"samples.csv"));memory.WriteLine("utc,real_seconds,sim_total,epoch,fleet,completed_epochs,completed_tasks,managed,native_allocated,working_set,private_bytes,controller_samples,controller_gc,log_bytes,tasks,dishes,dish_events,human_log,trace");memory.AutoFlush=true;
            watch.Start();Application.logMessageReceived+=OnLog;BeginEpoch(Data.smoke);Sample();initialized=true;
        }
        private void BeginEpoch(bool five)
        {
            sim.HumanExperience?.EndWithoutReset();sim.ReserveFleet=five;sim.Settings.VerboseRuntimeLogs=false;
            scenario=gameObject.AddComponent<GuardianInteractiveVerification>();scenario.AutomationTimeScale=Data.smoke?8:1;scenario.ExerciseReserveFleet=five;scenario.OutputDirectory=Path.Combine(OutputRoot,"scenario");Data.epoch++;Data.fleet=five?5:3;Data.trackedAgents=Data.fleet+1;Data.drainingSeconds=0;drainAt=0;
        }
        private void FixedUpdate()
        {
            if(!initialized||Done)return;if(sim.PerformanceSampleId==lastSample)return;lastSample=sim.PerformanceSampleId;
            if(timeCount>=times.Length){Finish(new InvalidOperationException("Bounded timing storage exhausted"));return;}
            times[timeCount++]=sim.LastFixedElapsedTicks*1000000.0/Stopwatch.Frequency;Data.controllerGcBytes+=sim.LastFixedAllocatedBytes;Data.physicalSamples=timeCount;
        }
        private void Update()
        {
            if(!initialized||Done)return;try
            {
                Time.timeScale=Data.smoke?8:1;Data.realSeconds=watch.Elapsed.TotalSeconds;Data.simulatedSeconds=completedSim+sim.DemoClock;
                if(scenario!=null){Data.maxHumanPenetration=Mathf.Max(Data.maxHumanPenetration,scenario.MaximumMeasuredHumanPenetration);Data.currentScenarioPhase=scenario.CurrentPhase;}
                if(scenario!=null && scenario.Done)
                {
                    if(!scenario.Passed)throw new InvalidOperationException("Connected endurance epoch failed; "+sim.DemoClock);
                    if(drainAt==0)drainAt=watch.Elapsed.TotalSeconds;
                    bool drained=sim.Tasks.All(t=>t.Phase==TaskPhase.Completed)&&sim.Robots.All(r=>r.IsIdle)&&sim.Traffic.JobClaimCount==0&&sim.Traffic.Reservations.Count==0&&sim.Traffic.Owner==null;
                    Data.drainingSeconds=watch.Elapsed.TotalSeconds-drainAt;
                    if(drained)
                    {
                        Data.completedEpochs++;if(Data.fleet==3)Data.threeRobotEpochs++;else Data.fiveRobotEpochs++;Data.completedTasks+=sim.Tasks.Count;completedSim+=sim.DemoClock;
                        Destroy(scenario);scenario=null;if(Data.realSeconds<Data.requestedRealSeconds)BeginEpoch(Data.fleet==3);
                    }
                    else if(Data.drainingSeconds>(Data.smoke?80:400))throw new TimeoutException("Completed scenario did not drain safely");
                }
                if(Data.realSeconds>=nextSample){nextSample=Data.realSeconds+5;Sample();}
                if(Data.realSeconds>=Data.requestedRealSeconds)
                {
                    if(Data.smoke && Data.fiveRobotEpochs<1)throw new InvalidOperationException("Smoke did not complete and drain a five-robot epoch");
                    if(!Data.smoke && (Data.realSeconds<1800||Data.threeRobotEpochs<1||Data.fiveRobotEpochs<1))throw new InvalidOperationException("Real endurance duration/profile requirements not met");Finish(null);
                }
                // This is a hidden resource-limited test, not an interactive frame benchmark.
                if(!Done)System.Threading.Thread.Sleep(20);
            }catch(Exception ex){Finish(ex);}
        }
        private void Sample()
        {
            Data.managedBytes=GC.GetTotalMemory(false);Data.nativeAllocatedBytes=Profiler.GetTotalAllocatedMemoryLong();Data.memoryAvailable=GuardianRuntimeMetrics.ProcessMemory(out Data.workingSetBytes,out Data.privateBytes);if(!Data.memoryAvailable)throw new InvalidOperationException("Native process-memory metric unavailable");Data.activeTasks=sim.Tasks.Count(t=>t.Phase!=TaskPhase.Completed);
            Data.peakManagedBytes=Math.Max(Data.peakManagedBytes,Data.managedBytes);Data.peakNativeAllocatedBytes=Math.Max(Data.peakNativeAllocatedBytes,Data.nativeAllocatedBytes);Data.peakPrivateBytes=Math.Max(Data.peakPrivateBytes,Data.privateBytes);
            string log=Application.consoleLogPath;if(File.Exists(log))Data.logBytes=new FileInfo(log).Length;
            int trace=0;foreach(var item in experience.Trace)trace++;
            Data.maxTasks=Math.Max(Data.maxTasks,sim.Tasks.Count);Data.maxRequests=Math.Max(Data.maxRequests,sim.Requests.Count);Data.maxDishes=Math.Max(Data.maxDishes,sim.Inventory.Items.Count);Data.maxDishEvents=Math.Max(Data.maxDishEvents,sim.Inventory.Events.Count);Data.maxHumanLog=Math.Max(Data.maxHumanLog,sim.HumanExperience?.Events.Count??0);Data.maxTrace=Math.Max(Data.maxTrace,trace);
            if(Data.logBytes>16*1024*1024 || Data.privateBytes>2L*1024*1024*1024)throw new InvalidOperationException("Endurance log/private-memory budget exceeded");
            if(Data.maxTasks>5||Data.maxRequests>5||Data.maxDishes>16||Data.maxDishEvents>128||Data.maxHumanLog>512||Data.maxTrace>128)throw new InvalidOperationException("Epoch retention budget exceeded");
            memory.WriteLine(FormattableString.Invariant($"{DateTime.UtcNow:o},{Data.realSeconds:F3},{Data.simulatedSeconds:F3},{Data.epoch},{Data.fleet},{Data.completedEpochs},{Data.completedTasks},{Data.managedBytes},{Data.nativeAllocatedBytes},{Data.workingSetBytes},{Data.privateBytes},{timeCount},{Data.controllerGcBytes},{Data.logBytes},{sim.Tasks.Count},{sim.Inventory.Items.Count},{sim.Inventory.Events.Count},{sim.HumanExperience?.Events.Count??0},{trace}"));
        }
        private void OnLog(string message,string stack,LogType type)
        {if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert)Finish(new InvalidOperationException(message+"\n"+stack));}
        private void Finish(Exception error)
        {
            if(finishing||Done)return;finishing=true;Data.status=error==null?"PASS":"FAIL";Data.error=error?.ToString()??"";Data.realSeconds=watch.Elapsed.TotalSeconds;Data.finishedUtc=DateTime.UtcNow.ToString("o");Data.simulatedSeconds=completedSim+(scenario!=null?sim.DemoClock:0);
            var sorted=new double[timeCount];Array.Copy(times,sorted,timeCount);Array.Sort(sorted);if(timeCount>0){Data.controllerP50Us=sorted[(int)(timeCount*.50)];Data.controllerP95Us=sorted[Math.Min(timeCount-1,(int)(timeCount*.95))];Data.controllerP99Us=sorted[Math.Min(timeCount-1,(int)(timeCount*.99))];Data.meanControllerGcBytes=(double)Data.controllerGcBytes/timeCount;}
            Done=true;Application.logMessageReceived-=OnLog;memory?.Dispose();Time.timeScale=1;UnityEngine.Debug.Log("GUARDIAN_ENDURANCE "+Data.status);finishing=false;
        }
        private void OnDestroy(){Application.logMessageReceived-=OnLog;memory?.Dispose();process?.Dispose();}
    }
}
