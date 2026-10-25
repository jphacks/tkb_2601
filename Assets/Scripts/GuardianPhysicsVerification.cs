using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    [DefaultExecutionOrder(10000)]
    public sealed partial class GuardianPhysicsVerification : MonoBehaviour
    {
        private GuardianSimulator sim;
        private readonly List<string> report=new List<string>();
        private struct ProgressGuardState { public float Previous,Paused; }
        private readonly Dictionary<GuardianPhysicsDriver,ProgressGuardState> progressGuards=new Dictionary<GuardianPhysicsDriver,ProgressGuardState>();
        private Action<bool> completion;
        private string suite,output;
        private bool active,failed;
        private int fixedSteps,maxAssigned,maxMoving;
        private float worstPenetration,workingParallelSeconds,workingSeconds,parallelFirst,parallelLast;
        private GuardianWaitBreakdown breakdown;
        private readonly HashSet<int> parallelWindows=new HashSet<int>();
        private readonly List<Collider> temporaryObstacles=new List<Collider>();
        public void Begin(string name,Action<bool> done=null)
        {
            if (active) return;
            sim=GetComponent<GuardianSimulator>(); suite=name; completion=done;
            report.Add("DEFAULT_MOTION="+sim.MotionSpeedMultiplier+" DEFAULT_SUPERVISION="+sim.OperatorSupervisionEnabled);
            report.Add("PHYSICAL_FORCE_MODEL="+sim.Settings.Physical.Enabled+" ASSUMED_CHASSIS_KG="+sim.Settings.Physical.ChassisMass+" DRIVE_N="+sim.Settings.Physical.DriveForce+" BRAKE_N="+sim.Settings.Physical.BrakeForce);
            sim.OperatorSupervisionEnabled=false; // Autonomous regressions; live decision UX is exercised explicitly.
            string[] motionArgs=Environment.GetCommandLineArgs(); int motionIndex=Array.IndexOf(motionArgs,"-guardianMotionSpeed");
            int motion=motionIndex>=0 && motionIndex+1<motionArgs.Length?int.Parse(motionArgs[motionIndex+1]):2;
            if(!sim.TrySetMotionSpeed(motion)) throw new ArgumentException("Only 1x/2x physical motion is supported");
            output=Path.Combine(Directory.GetParent(Application.dataPath).FullName,"Recordings/physics-tests",suite);
            Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output,"penetration-maxima.csv"),"suite,seed,fleet,sim_seconds,fixed_step,robot,other,depth_m,axis,robot_motion,other_position,other_rotation\n"); active=true;
            Application.logMessageReceived+=OnLog; Application.runInBackground=true;
            Time.captureFramerate=0; Time.timeScale=12; QualitySettings.vSyncCount=0; Application.targetFrameRate=90;
            StartCoroutine(Run());
        }
        private void Start()
        {
            string[] args=Environment.GetCommandLineArgs();
            if (!active && args.Contains("-guardianPhysicsTest"))
            {
                int index=Array.IndexOf(args,"-guardianPhysicsSuite");
                Begin(index>=0 && index+1<args.Length?args[index+1]:"full");
            }
        }
        private void Check(bool valid,string message)
        { report.Add((valid?"PASS: ":"FAIL: ")+message); if (!valid) failed=true; }
        private void OnLog(string text,string stack,LogType type)
        {
            if (!active) return;
            if (text.StartsWith("[Guardian]")) report.Add(text);
            if (type==LogType.Error || type==LogType.Exception || type==LogType.Assert)
            {
                if (stack.Contains("UnityEditor.Search.SearchDatabase")) report.Add("EDITOR_STARTUP_ISSUE: "+text);
                else { failed=true; report.Add("FAIL: "+text+"\n"+stack); }
            }
        }
        private IEnumerator Run()
        {
            report.Add("Unity="+Application.unityVersion+" graphics="+SystemInfo.graphicsDeviceName+" suite="+suite);
            Check(Application.unityVersion=="6000.3.12f1","exact Unity version");
            report.Add("INITIAL_CONFIG collectEmptyOnDelivery="+sim.Settings.CollectEmptyOnDelivery+" mealSeconds="+sim.Settings.MealSeconds);
            report.Add("MOTION_MULTIPLIER="+sim.MotionSpeedMultiplier+" fixedDeltaTime="+Time.fixedDeltaTime+" testTimeScale="+Time.timeScale+" (automation only)");
            if(suite=="serve-clear" || suite=="serve-clear-stress" || suite=="serve-clear-waiting" || suite=="full") Check(sim.Settings.CollectEmptyOnDelivery,"normal scene enables same-visit collection by default");
            report.Add("MODEL_PROFILE="+(sim.Robots.All(r=>r.transform.Find("Robot Visual/Nekobot Model")!=null)?"local-nekobot":"owned-placeholder"));
            if(suite=="motion-demo") { yield return MotionDemoRegression(); Finish(); yield break; }
            if(suite=="reservation7") { yield return StressSeeds(); Finish(); yield break; }
            if(suite=="tray-probe")
            {
                foreach(var r in sim.Robots)
                {
                    var tray=r.GetComponent<GuardianCargoTray>()??r.gameObject.AddComponent<GuardianCargoTray>(); tray.Configure();
                    report.Add("TRAY_PROBE actor="+r.RobotId+" local="+tray.LocalModel+" front="+tray.FaceCenter.ToString("F5"));
                    for(int n=0;n<tray.Shelves.Count;n++)report.Add("TRAY_SHELF actor="+r.RobotId+" n="+n+" bounds="+tray.Shelves[n].ToString("F5")+" anchor="+r.transform.InverseTransformPoint(tray.Anchors[n].position).ToString("F5"));
                }
                Finish(); yield break;
            }
            if(suite=="tray-service") { yield return TrayServiceRegression(); Finish(); yield break; }
            if(suite=="human-held-collections") { yield return HumanHeldCollectionRegression(); Finish(); yield break; }
            if(suite=="human-hold-probe") { yield return HumanSameVisitHoldProbe(); Finish(); yield break; }
            if(suite=="human-handoff-probe") { yield return HumanHandoffProbe(); Finish(); yield break; }
            if(suite=="human-intervention") { yield return HumanInterventionRegression(); Finish(); yield break; }
            if(suite=="linked-state") { yield return LinkedStateRegression(); Finish(); yield break; }
            if (suite=="serve-clear") { yield return ServeClearRegression(); Finish(); yield break; }
            if (suite=="serve-clear-stress") { yield return ServeClearStress(); Finish(); yield break; }
            if (suite=="serve-clear-waiting") { yield return ServeClearWaitingRecovery(); Finish(); yield break; }
            if (suite.StartsWith("contact42")) { yield return StressSeeds(); Finish(); yield break; }
            if (suite=="deploydiag") { yield return DeployDiagnostic(); Finish(); yield break; }
            if (suite=="diagnose42" || suite=="replay42") { yield return Diagnose42(); Finish(); yield break; }
            if (suite=="legacy") { yield return LegacyRegression(); Finish(); yield break; }
            if (suite=="returns") { yield return ReturnBatchRegression(); Finish(); yield break; }
            if (suite=="lifecycle") { yield return RetirementLifecycle(); Finish(); yield break; }
            if (suite=="stress42") { yield return StressSeeds(); Finish(); yield break; }
            if (suite=="reviewgui") { yield return PanelModeRegression(); Finish(); yield break; }
            if (suite=="review") { yield return ReviewRegression(); Finish(); yield break; }
            yield return Smoke();
            if (!failed && suite=="full") yield return LinkedStateRegression();
            if (!failed && suite=="full") { yield return ReviewRegression(); yield return RetirementLifecycle(); yield return ReturnBatchRegression(); }
            if (!failed && (suite=="fleet" || suite=="full")) yield return FleetOperations();
            if (!failed && (suite=="waves" || suite=="full")) yield return MultiWave();
            if (!failed && (suite=="scenarios" || suite=="full")) yield return PolicyAndOwnership();
            if (!failed && (suite=="scenarios" || suite=="full")) yield return OpposingReturnsAndObstacle();
            if (!failed && (suite=="full" || suite=="stress")) yield return StressSeeds();
            if(!failed && suite=="full") yield return ServeClearRegression();
            if(!failed && suite=="full") yield return TrayServiceRegression();
            if(!failed && suite=="full") yield return HumanInterventionRegression();
            Finish();
        }
        private void Reset(int seed=2026,bool serveClear=false,[System.Runtime.CompilerServices.CallerMemberName] string scenario=null)
        {
            clearanceCase=scenario??suite;
            progressGuards.Clear();
            ResetDishVisibilityChecks();
            sim.Settings.CollectEmptyOnDelivery=serveClear; sim.Settings.MealSeconds=8; sim.Settings.MaximumOpenTasks=24;
            sim.Settings.Seed=seed; sim.Settings.RandomFaults=false; sim.AutomaticTasks=false;
            sim.ReserveFleet=false; sim.Settings.MaximumDepartureDelay=.5f; sim.SetMode(GuardianSimulationMode.Physics);
            maxAssigned=maxMoving=0; workingParallelSeconds=workingSeconds=0; parallelFirst=-1; parallelLast=-1; parallelWindows.Clear();
            Physics.SyncTransforms();
        }
        private IEnumerator Wait(Func<bool> ready,float seconds,string name)
        {
            float end=sim.DemoClock+seconds;
            while (!failed && !ready() && sim.DemoClock<end) yield return new WaitForFixedUpdate();
            Check(!failed && ready(),name+" clock="+sim.DemoClock.ToString("F2"));
            if(failed) foreach(var robot in sim.Robots) report.Add("WAIT_STATE "+robot.RobotId+" task="+robot.CurrentTask?.Id+" phase="+robot.CurrentTask?.Phase+" reason="+robot.CurrentTask?.PendingReason+" "+robot.PhysicsDriver.MotionState);
        }
        private IEnumerator Smoke()
        {
            Reset();
            for (int c=0;c<3;c++) for (int k=0;k<3;k++)
            {
                bool expected=k==2 || c==0 || (c==1 && k==1);
                Check(GuardianCapabilityPolicy.Allows((RobotCondition)c,(DeliveryKind)k)==expected,"matrix "+(RobotCondition)c+" / "+(DeliveryKind)k+"="+expected);
                Check(!GuardianCapabilityPolicy.Allows((RobotCondition)c,(DeliveryKind)k,true),"emergency stop blocks "+(RobotCondition)c+" / "+(DeliveryKind)k);
            }
            Check(GuardianCapabilityPolicy.FromVibration(.3f,sim.Settings)==RobotCondition.Warning
                && GuardianCapabilityPolicy.FromVibration(.7f,sim.Settings)==RobotCondition.Limited,"adjustable threshold boundaries");
            DeliveryTask soup=sim.QueueTask(DeliveryKind.Soup,0,GuardianMenu.Soup);
            DeliveryTask food=sim.QueueTask(DeliveryKind.Food,1,GuardianMenu.CurryRice);
            DeliveryTask empty=sim.QueueTask(DeliveryKind.EmptyDishes,2);
            string emptyId=empty.DishIds.Single();
            Check(sim.Inventory.Items[emptyId].Owner==GuardianDishOwner.Table,"reservation leaves empty dish on table");
            Check(sim.Tasks.Count==3 && sim.Robots.Select(r=>r.PhysicsDriver.Home).Distinct().Count()==3,"three jobs accounted for at distinct bays");
            Check(sim.Robots.All(r=>!r.PhysicsDriver.Body.isKinematic && r.PhysicsDriver.Shape.enabled),"three dynamic rigidbodies with real collision envelopes");
            Check(sim.Robots.All(r=>Mathf.Abs(r.PhysicsDriver.Body.centerOfMass.x)<.00001f
                && Mathf.Abs(r.PhysicsDriver.Body.centerOfMass.z)<.00001f),"chassis-centered mass prevents yaw from orbiting the navigation origin");
            yield return Wait(()=>empty.Phase==TaskPhase.ToDropoff || empty.Phase==TaskPhase.Completed,180,"empty dish physically picked up");
            if (failed) yield break;
            Check(sim.Inventory.Items[emptyId].Owner!=GuardianDishOwner.Table,"picked-up empty dish removed from table");
            yield return Wait(()=>sim.Tasks.All(t=>t.Phase==TaskPhase.Completed) && sim.Robots.All(r=>r.IsIdle),240,"soup / food / return complete and three robots return to separate homes");
            if (failed) yield break;
            report.Add("BUSINESS_CONCURRENCY seconds="+workingParallelSeconds.ToString("F2")+" working window="+workingSeconds.ToString("F2"));
            Check(workingParallelSeconds>=8 && workingParallelSeconds/Mathf.Max(1,workingSeconds)>=.10f,"at least two assigned robots physically moving for meaningful measured time");
            Check(sim.Inventory.Items[soup.DishIds.Single()].Owner==GuardianDishOwner.Table,"soup ownership kitchen -> robot -> table");
            Check(sim.Inventory.Items[food.DishIds.Single()].Owner==GuardianDishOwner.Table,"food ownership kitchen -> robot -> table");
            Check(sim.Inventory.Items[emptyId].Owner==GuardianDishOwner.ReturnCounter,"empty ownership table -> robot -> return counter");
            Check(sim.Traffic.Owner==null && sim.Traffic.QueueLength==0,"all route/endpoint/home reservations released");
            foreach (GuardianRobot robot in sim.Robots)
            {
                Check(robot.PhysicsDriver.LastParkDistance<.075f && robot.PhysicsDriver.LastParkSpeed<Mathf.Sqrt(.025f),
                    "reservation released only inside original arrival tolerances "+robot.RobotId+
                    " error="+robot.PhysicsDriver.LastParkDistance.ToString("F5")+" speed="+robot.PhysicsDriver.LastParkSpeed.ToString("F5"));
                report.Add("Actor "+robot.RobotId+" shape="+robot.PhysicsDriver.Shape.size+" center="+robot.PhysicsDriver.Shape.center+
                    " home="+robot.PhysicsDriver.Home+" longest progress stall="+robot.PhysicsDriver.MaximumStall.ToString("F3"));
            }
        }
        private void FixedUpdate()
        {
            if (!active || failed || sim.Mode!=GuardianSimulationMode.Physics) return;
            fixedSteps++; breakdown?.Tick(Time.fixedDeltaTime);
            CaptureTrafficTrace();
            maxAssigned=Mathf.Max(maxAssigned,sim.Robots.Count(r=>r.CurrentTask!=null));
            maxMoving=Mathf.Max(maxMoving,sim.Robots.Count(r=>r.CurrentTask!=null && new Vector2(r.PhysicsDriver.Body.linearVelocity.x,r.PhysicsDriver.Body.linearVelocity.z).magnitude>.1f));
            if (sim.Robots.Any(r=>r.CurrentTask!=null))
            {
                workingSeconds+=Time.fixedDeltaTime;
                if (sim.Robots.Count(r=>r.CurrentTask!=null && new Vector2(r.PhysicsDriver.Body.linearVelocity.x,r.PhysicsDriver.Body.linearVelocity.z).magnitude>.1f)>=2)
                {
                    workingParallelSeconds+=Time.fixedDeltaTime; parallelWindows.Add(Mathf.FloorToInt(sim.DemoClock/10));
                    if (parallelFirst<0) parallelFirst=sim.DemoClock; parallelLast=sim.DemoClock;
                }
            }
            try
            {
                sim.Inventory.ValidateOwnership();
                VerifyDishVisibility();
                GuardianRobot[] robots=sim.Robots.ToArray();
                for (int i=0;i<robots.Length;i++)
                {
                    var a=robots[i].PhysicsDriver;
                    if(checkedBodies.Add(robots[i].GetInstanceID())) VerifyMeshEnvelope(robots[i],robots[i].transform.Find(NekobotVisualFactory.VisualRootName));
                    for (int j=i+1;j<robots.Length;j++) TestPenetration(a,robots[j].PhysicsDriver.Shape);
                    foreach (Collider obstacle in sim.PhysicsWorld.Obstacles)
                        if (obstacle!=null && obstacle.enabled && obstacle.GetComponent<GuardianPhysicsObstacle>()?.Floor!=true) TestPenetration(a,obstacle);
                    foreach (Collider obstacle in temporaryObstacles)
                        if (obstacle!=null && obstacle.enabled) TestPenetration(a,obstacle);
                    float movingWait=MovementGuardWait(a,sim.Traffic.HasStoppedBlocker(a));
                    if (movingWait>120) throw new InvalidOperationException("Unresolved reservation/movement wait "+robots[i].RobotId+" activeSeconds="+movingWait+" rawSeconds="+a.ProgressWait+" "+a.MotionState);
                    if (a.MaximumStall>16) throw new InvalidOperationException("Unresolved traffic blockage "+robots[i].RobotId+" "+a.MotionState);
                }
            }
            catch (Exception error) { failed=true; report.Add("FAIL: "+error.Message); }
        }
        private float MovementGuardWait(GuardianPhysicsDriver driver,bool stoppedBlocker)
        {
            progressGuards.TryGetValue(driver,out var state);
            float raw=driver.ProgressWait;
            if(raw<state.Previous) state.Paused=0; // Physical progress/reset starts a new interval.
            if(stoppedBlocker) state.Paused+=Mathf.Max(0,raw-state.Previous);
            state.Previous=raw; progressGuards[driver]=state;
            // The existing stop exemption must survive the first recovery steps,
            // before the body has moved enough to reset its raw progress anchor.
            return Mathf.Max(0,raw-state.Paused);
        }
        private IEnumerator PolicyAndOwnership()
        {
            Reset(); foreach (GuardianRobot robot in sim.Robots) sim.SetCondition(robot.RobotId,RobotCondition.Limited);
            DeliveryTask soup=sim.QueueTask(DeliveryKind.Soup,0,GuardianMenu.Soup);
            string id=soup.DishIds.Single();
            yield return new WaitForSeconds(.3f);
            Check(soup.Phase==TaskPhase.Pending && soup.AssignedTo==null && !string.IsNullOrEmpty(soup.PendingReason),"all ineligible: soup pending with reason, identity retained");
            Check(sim.Inventory.Items[id].Owner==GuardianDishOwner.Kitchen,"ineligible pending food remains in kitchen");
            sim.SetCondition("A",RobotCondition.Normal);
            Check(soup.AssignedTo?.RobotId=="A","normal recovery assigns pending soup");
            int version=soup.AssignmentVersion;
            sim.SetCondition("A",RobotCondition.Warning); sim.SetCondition("A",RobotCondition.Limited);
            Check(soup.Phase==TaskPhase.Pending && soup.AssignmentVersion==version && soup.RevokedVersion==version,"pre-pickup fault revoked once per assignment");
            sim.SetCondition("B",RobotCondition.Normal);
            GuardianRobot b=sim.Robots.Single(r=>r.RobotId=="B");
            yield return Wait(()=>b.Carrying && Vector3.Distance(b.PhysicsDriver.Body.position,new Vector3(soup.Pickup.x,b.PhysicsDriver.Body.position.y,soup.Pickup.z))>2,130,"soup physically carried more than 2m from pickup");
            if (failed) yield break;
            sim.SetCondition("B",RobotCondition.Warning);
            int returning=report.Count(l=>l.Contains("B returning cargo to handoff point"));
            sim.SetCondition("B",RobotCondition.Limited);
            Check(soup.Phase==TaskPhase.ReturningCargo && sim.Inventory.Items[id].Owner==GuardianDishOwner.Robot
                && sim.Inventory.Items[id].OwnerKey=="B","mid-route fault retains sole carried ownership until handoff");
            Check(report.Count(l=>l.Contains("B returning cargo to handoff point"))==returning,"repeated ineligible condition does not restart carried return");
            yield return Wait(()=>soup.Phase==TaskPhase.Pending && sim.Inventory.Items[id].Owner==GuardianDishOwner.Kitchen,130,"cargo physically returned to kitchen before reallocation");
            if (failed) yield break;
            Check(soup.AssignedTo==null && soup.DishIds.Single()==id,"no eligible actor: same returned dish stays pending");
            sim.SetCondition("A",RobotCondition.Normal);
            yield return Wait(()=>soup.Phase==TaskPhase.Completed && sim.Robots.All(r=>r.IsIdle),240,"recovered A completes the same soup and returns home");
            if (failed) yield break;
            Check(sim.Inventory.Items[id].Owner==GuardianDishOwner.Table && soup.DishIds.Count==1,"reassignment neither duplicates nor loses the dish");

            // Deliberately corrupt only a test item's parent to prove the guard.
            GuardianDishItem dish=sim.Inventory.Items[id]; Transform old=dish.transform.parent;
            dish.transform.SetParent(sim.transform,true); bool rejected=false;
            try { sim.Inventory.ValidateOwnership(); } catch (InvalidOperationException) { rejected=true; }
            finally { dish.transform.SetParent(old,true); }
            Check(rejected,"unique-owner guard rejects mismatched physical parent");

            foreach (RobotCondition state in Enum.GetValues(typeof(RobotCondition)))
            {
                sim.SetCondition("C",state); yield return null; yield return null;
                GuardianRobot c=sim.Robots.Single(r=>r.RobotId=="C"); Transform display=c.transform.Find("Guardian status display");
                var text=display.GetComponentInChildren<TextMesh>();
                Check(text.text.Contains(state.ToString().ToUpperInvariant()) && text.text.Contains(GuardianCapabilityPolicy.AllowedText(state)),"manual state label and allowed tasks agree: "+state);
                var lamps=display.GetComponentsInChildren<Renderer>().Where(r=>r.GetComponent<TextMesh>()==null).ToArray();
                int bright=0; var property=new MaterialPropertyBlock();
                foreach (Renderer lamp in lamps)
                { lamp.GetPropertyBlock(property); Color color=property.GetColor("_Color"); if (Mathf.Max(color.r,Mathf.Max(color.g,color.b))>.5f) bright++; }
                Check(lamps.Length==3 && bright==1,"exactly one live green/yellow/red status lamp: "+state);
            }
            Reset(); foreach (GuardianRobot robot in sim.Robots) sim.SetCondition(robot.RobotId,RobotCondition.Normal);
            DeliveryTask food=sim.QueueTask(DeliveryKind.Food,1,GuardianMenu.Hamburg);
            yield return Wait(()=>sim.Traffic.Owner!=null && Vector3.Distance(sim.Traffic.Owner.Body.position,sim.Traffic.Owner.Home)>1,60,"stop probe entered shared route");
            if (failed) yield break;
            GuardianRobot stopped=sim.Traffic.Owner.Robot;
            sim.SetEmergencyStop(stopped.RobotId,true); sim.SetCondition(stopped.RobotId,RobotCondition.Limited);
            yield return new WaitForSeconds(2);
            Vector3 halt=stopped.PhysicsDriver.Body.position;
            yield return new WaitForSeconds(1);
            Check(stopped.EmergencyStopped && sim.Traffic.Owner==stopped.PhysicsDriver && Vector3.Distance(halt,stopped.PhysicsDriver.Body.position)<.02f,
                "emergency stop remains immobile and retains occupied route; Limited does not override stop");
            sim.SetEmergencyStop(stopped.RobotId,false);
            yield return Wait(()=>food.Phase==TaskPhase.Completed && sim.Robots.All(r=>r.IsIdle),260,"stop recovery clears route and eligible actor completes retained food job");
        }
        private IEnumerator OpposingReturnsAndObstacle()
        {
            Reset();
            GuardianRobot a=sim.Robots.Single(r=>r.RobotId=="A"),c=sim.Robots.Single(r=>r.RobotId=="C");
            a.DriveTo(new Vector3(3.6f,a.PhysicsDriver.PlaneY,3.4f));
            c.DriveTo(new Vector3(3.6f,c.PhysicsDriver.PlaneY,-3.4f));
            yield return Wait(()=>sim.Robots.All(r=>r.IsIdle),240,"opposing corridor requests wait at bays then traverse and return");
            if (failed) yield break;
            Check(sim.Traffic.Grants>=4 && sim.Traffic.Reservations.Count==0,"two opposing trips completed, reservations cleared");
            Reset();
            foreach (GuardianRobot robot in sim.Robots) robot.DriveTo(new Vector3(-6,robot.PhysicsDriver.PlaneY,-3.6f));
            yield return Wait(()=>sim.Robots.All(r=>r.IsIdle),300,"simultaneous return requests do not stack at common endpoint");
            if (failed) yield break;
            Check(sim.Traffic.Grants>=6 && sim.Traffic.Owner==null && sim.Traffic.QueueLength==0,"all three return trips drain fairly");
            Reset(); a=sim.Robots.Single(r=>r.RobotId=="A");
            GameObject obstacle=GameObject.CreatePrimitive(PrimitiveType.Cube); obstacle.name="Test temporary stationary barrier";
            obstacle.transform.position=new Vector3(-2.5f,1,-5.25f); obstacle.transform.localScale=new Vector3(.2f,2,1.6f);
            Collider shape=obstacle.GetComponent<Collider>(); temporaryObstacles.Add(shape); Physics.SyncTransforms();
            a.DriveTo(new Vector3(3.6f,a.PhysicsDriver.PlaneY,3.4f));
            yield return new WaitForSeconds(5);
            Vector3 stop=a.PhysicsDriver.Body.position;
            yield return new WaitForSeconds(1);
            Check(Vector3.Distance(stop,a.PhysicsDriver.Body.position)<.03f && a.PhysicsDriver.WaitReason.Contains("obstacle"),"stationary obstacle causes physical braking/wait without penetration");
            shape.enabled=false; temporaryObstacles.Remove(shape); Destroy(obstacle);
            yield return Wait(()=>sim.Robots.All(r=>r.IsIdle),150,"removing temporary obstruction resumes trip and releases reservation");
        }
        private IEnumerator PanelModeRegression()
        {
            Reset(); Time.timeScale=1;
            yield return new WaitForSecondsRealtime(1);
            string request=Path.Combine(output,"button-request.txt");
            File.WriteAllText(request,"legacy,"+sim.ModeButtonClientPoint.x+","+sim.ModeButtonClientPoint.y+","+Screen.width+","+Screen.height);
            yield return Wait(()=>sim.Mode==GuardianSimulationMode.LegacyStory,40,"native mouse clicks rendered Physics button to Legacy without exception");
            if (failed) yield break;
            yield return new WaitForSecondsRealtime(.5f);
            File.WriteAllText(request,"physics,"+sim.ModeButtonClientPoint.x+","+sim.ModeButtonClientPoint.y+","+Screen.width+","+Screen.height);
            yield return Wait(()=>sim.Mode==GuardianSimulationMode.Physics,40,"native mouse clicks rendered Legacy button back to Physics without exception");
            Check(sim.PanelModeActivations==2,"two real GUI button activations; no F7/direct mode API used by test");
            if (failed) yield break;
            File.WriteAllText(request,"done");
            sim.SetReserveFleet(true); Time.timeScale=4;
            yield return new WaitForSecondsRealtime(.4f);
            yield return NativeRequest("deploy/D","deploy-D-first");
            var d=sim.Robots.Single(r=>r.RobotId=="D");
            yield return Wait(()=>d.DispatchEnabled,30,"native Deploy D activates dispatch");
            var job=sim.QueueTask(DeliveryKind.Soup,0);
            yield return Wait(()=>d.Carrying,100,"deployed D physically picks up soup before native stop/retirement");
            if(failed) yield break;
            yield return NativeRequest("stop/D","stop-D");
            yield return Wait(()=>d.EmergencyStopped,20,"native STOP D immobilizes actor");
            yield return NativeRequest("retire/D","retire-D");
            yield return Wait(()=>d.FleetRole==GuardianFleetRole.Retiring && sim.Fleet.Commands.Last().State==GuardianCommandState.Blocked,20,"native Retire D shows immobilized blocked reason");
            yield return NativeRequest("recover/D","recover-D");
            yield return Wait(()=>d.FleetRole==GuardianFleetRole.Standby && !d.EmergencyStopped && d.IsIdle,120,"native recovery physically returns carried dish and parks D");
            yield return Wait(()=>job.Phase==TaskPhase.Completed && sim.Robots.All(r=>r.IsIdle),180,"same soup completes after native operator handoff");
            if(failed) yield break;
            yield return NativeRequest("seed-field","seed-field","4242");
            yield return Wait(()=>sim.SeedInputValue=="4242",20,"native keyboard enters seed in actual text field");
            yield return NativeRequest("seed-apply","seed-apply");
            yield return Wait(()=>sim.Settings.Seed==4242,20,"native Apply seed changes configuration and resets scenario");
            Check(sim.NativeClickCount("deploy/D")>=1 && sim.NativeClickCount("retire/D")>=1 && sim.NativeClickCount("recover/D")>=1 && sim.NativeClickCount("stop/D")>=1 && sim.NativeClickCount("seed-apply")>=1,"major fleet/stop/seed controls exercised by native events; queue/condition buttons not claimed");
            yield return NativeRequest("deploy/D","deploy-D-second"); yield return Wait(()=>sim.Robots.Single(r=>r.RobotId=="D").DispatchEnabled,20,"native spare deployment after seed reset");
            yield return NativeRequest("retire/B","retire-B-at-bay"); yield return Wait(()=>sim.Robots.Single(r=>r.RobotId=="B").FleetRole==GuardianFleetRole.Standby,20,"native retirement of parked B");
            File.WriteAllText(request,"done"); sim.QueueTask(DeliveryKind.Soup,0);
            yield return new WaitForSecondsRealtime(.4f);
            if (Environment.GetCommandLineArgs().Contains("-guardianPublicEvidence") && Environment.GetCommandLineArgs().Contains("true"))
            { Check(sim.Robots.All(r=>r.transform.Find("Robot Visual/Nekobot Model")==null),"PUBLIC_EVIDENCE_OWNED_PLACEHOLDERS_OK"); if (failed) yield break; }
            ScreenCapture.CaptureScreenshot(Path.Combine(output,"five-robot-panel-local.png"));
            yield return new WaitForSecondsRealtime(1);
            Check(sim.Mode==GuardianSimulationMode.Physics && sim.PanelModeActivations==2,"GUI protocol sends no extra mode click during capture");
        }
        private IEnumerator NativeRequest(string key,string stage,string text=null)
        {
            if(failed) yield break;
            // Keep the live scrolling layout stable between its measured Repaint
            // and the OS mouse event. The button handler still receives real input.
            float resume=Time.timeScale; Time.timeScale=0;
            Vector2 previous=new Vector2(-999,-999); int stable=0;
            for(int i=0;i<30;i++)
            {
                sim.PrepareNativeControl(key); yield return new WaitForSecondsRealtime(.1f);
                var point=sim.NativePoint(key);
                stable=point.y>15 && point.y<Screen.height-35 && Vector2.Distance(point,previous)<.1f?stable+1:0;
                previous=point; if(stable>=2) break;
            }
            int clicks=sim.NativeClickCount(key); var p=sim.NativePoint(key);
            ScreenCapture.CaptureScreenshot(Path.Combine(output,"native-"+stage+".png"));
            File.WriteAllText(Path.Combine(output,"button-request.txt"),stage+","+p.x+","+p.y+","+Screen.width+","+Screen.height+(text==null?"":","+text));
            float deadline=Time.realtimeSinceStartup+15;
            while(Time.realtimeSinceStartup<deadline && (text==null?sim.NativeClickCount(key)==clicks:sim.SeedInputValue!=text)) yield return new WaitForSecondsRealtime(.05f);
            Check(text==null?sim.NativeClickCount(key)>clicks:sim.SeedInputValue==text,"native input acknowledged "+stage+" point="+p);
            Time.timeScale=resume;
        }
        private IEnumerator ReviewRegression()
        {
            Reset();
            DeliveryTask soup=sim.QueueTask(DeliveryKind.Soup,0);
            var a=sim.Robots.Single(r=>r.RobotId=="A"); var b=sim.Robots.Single(r=>r.RobotId=="B");
            yield return Wait(()=>a.Carrying && a.PhysicsDriver.OwnsRoute && a.PhysicsDriver.Body.position.z<3,150,"controlled carried actor enters central crossing");
            if (failed) yield break;
            sim.Settings.MaximumSpeed=.4f;
            b.DriveTo(new Vector3(3.6f,b.PhysicsDriver.PlaneY,0));
            yield return Wait(()=>a.PhysicsDriver.Body.position.z<=.025f,40,"fault timed at rounded z=0 while B owns both z0 branches");
            if (failed) yield break;
            Check(sim.Traffic.Reservations.Any(p=>p.Key=="branch/-3.0/0.0" && p.Value=="B") && sim.Traffic.Reservations.Any(p=>p.Key=="branch/1.0/0.0" && p.Value=="B"),"B holds both z0 approach branches before controlled fault");
            sim.SetCondition("A",RobotCondition.Warning);
            yield return new WaitForSeconds(2);
            report.Add("CONTROLLED_FAULT "+a.PhysicsDriver.MotionState+" admitted="+sim.Traffic.IsAdmitted(a.PhysicsDriver));
            Check(sim.Traffic.Owner==a.PhysicsDriver && sim.Traffic.IsAdmitted(a.PhysicsDriver),"central owner retains atomic admission and protected exit during fault replan");
            if (failed) yield break;
            sim.Settings.MaximumSpeed=1.9f; sim.SetCondition("B",RobotCondition.Normal);
            yield return Wait(()=>soup.Phase==TaskPhase.Completed && sim.Robots.All(r=>r.IsIdle),260,"controlled central fault drains same-dish job and both physical return trips");
            if (failed) yield break;
            Check(sim.Traffic.Reservations.Count==0 && sim.Traffic.QueueLength==0 && sim.Traffic.Owner==null,"controlled central-fault reservations fully released");
            bool rejected=false; var invalid=new GuardianSimulationSettings{WarningThreshold=0};
            try { invalid.Validate(); } catch (ArgumentException) { rejected=true; }
            Check(rejected,"zero Warning threshold is rejected because it eliminates Normal range");
            sim.Settings.WarningThreshold=.0001f; sim.Settings.LimitedThreshold=1; sim.Settings.Validate(); sim.SetReserveFleet(true);
            sim.SetCondition("D",RobotCondition.Limited); sim.Fleet.RecoverToStandby("D");
            yield return new WaitForSeconds(.2f);
            Check(sim.Robots.Single(r=>r.RobotId=="D").Condition==RobotCondition.Normal && sim.Fleet.Deploy("D").State==GuardianCommandState.Accepted,"positive lower threshold / upper=1 recovers deployable Normal spare");
            bool localModel=sim.Robots.First().transform.Find("Robot Visual/Nekobot Model")!=null;
            var north=sim.Robots.Single(r=>r.RobotId=="C").PhysicsDriver;
            var backWall=sim.PhysicsWorld.Obstacles.Single(c=>c.name=="Beige back wall");
            Check(north.Home.z<=5.2501f && backWall.bounds.min.z-north.Home.z>=north.TurningRadius+.0199f,
                "north-bay complete offset-aware turning circle has at least 20mm wall clearance for the selected model");
            var size=sim.Robots.First().PhysicsDriver.Shape.size;
            Check(sim.Robots.All(r=>(r.PhysicsDriver.Shape.size-size).sqrMagnitude<.00001f),"all five physical envelopes use the same existing robot visual");
            Check(sim.Robots.All(r=>(r.transform.Find("Robot Visual/Nekobot Model")!=null)==localModel),"all five actors consistently use the selected actual or owned-placeholder profile");
            sim.Settings.WarningThreshold=.3f; sim.Settings.LimitedThreshold=.7f;
        }
        private IEnumerator LegacyRegression()
        {
            Reset(); sim.SetMode(GuardianSimulationMode.LegacyStory); Time.timeScale=1;
            yield return Wait(()=>sim.DemoClock>=41,44,"original story reaches 41 real-time simulation seconds");
            foreach (string id in new[]{"S-101","F-102","R-103","F-204","R-205","R-206"})
                Check(sim.Tasks.Single(t=>t.Id==id).Phase==TaskPhase.Completed,"legacy completion "+id);
            Check(sim.Tasks.Single(t=>t.Id=="F-204").AssignedTo.RobotId=="A" && sim.Robots.Single(r=>r.RobotId=="B").Condition==RobotCondition.Limited,"legacy 18s degradation and eligible A reassignment retained");
            report.Add("Legacy transform reference: physics avoidance is not asserted; no video capture.");
        }
        private IEnumerator ReturnBatchRegression()
        {
            Reset(); foreach (var r in sim.Robots) sim.SetCondition(r.RobotId,RobotCondition.Normal);
            for (int i=0;i<4;i++) sim.QueueTask(DeliveryKind.Food,0,GuardianMenu.CurryRice);
            yield return Wait(()=>sim.Tasks.All(t=>t.Phase==TaskPhase.Completed) && sim.Robots.All(r=>r.IsIdle),420,"four meals join original plate at table 1");
            if (failed) yield break;
            yield return new WaitForSeconds(sim.Settings.MealSeconds+.2f);
            var plateIds=sim.Inventory.Items.Values.Where(i=>i.Owner==GuardianDishOwner.Table && i.OwnerKey=="0" && i.Dirty).Select(i=>i.Id).ToArray();
            Check(plateIds.Length==5,"five actual dirty dish identities at one table");
            var first=sim.QueueTask(DeliveryKind.EmptyDishes,0);
            yield return Wait(()=>first.Phase==TaskPhase.ToDropoff,150,"first return physically picks up four of five dishes");
            if (failed) yield break;
            var second=sim.QueueTask(DeliveryKind.EmptyDishes,0); var merged=sim.QueueTask(DeliveryKind.EmptyDishes,0);
            Check(first.DishIds.Count==4 && second!=null && second!=first && merged==second,"remaining unreserved plate gets a distinct next trip; duplicate pre-pickup request links to that trip");
            Check(sim.Inventory.Items.Values.Count(i=>i.Owner==GuardianDishOwner.Table && i.OwnerKey=="0" && i.Dirty)==1,"next-trip request leaves the fifth plate physically on table");
            yield return Wait(()=>second.Phase==TaskPhase.ToDropoff,200,"next trip physically picks up the remaining plate");
            if (failed) yield break;
            Check(sim.QueueTask(DeliveryKind.EmptyDishes,0)==null && sim.Requests.Last().Status=="Rejected" && sim.Requests.Last().Decision.Contains("No unreserved"),"new request with no source is explicitly rejected with reason");
            yield return Wait(()=>first.Phase==TaskPhase.Completed && second.Phase==TaskPhase.Completed && sim.Robots.All(r=>r.IsIdle),200,"two trips complete all five dishes and return home");
            Check(plateIds.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.ReturnCounter) && sim.Requests.Count==8 && sim.Requests.Count(r=>r.Task!=null)==7 && sim.Tasks.Count==6,"eight requests tracked as six executed tasks, one coalesced request and one explicit rejection; no lost plate");
        }
        private IEnumerator RetirementLifecycle()
        {
            foreach (bool recovery in new[]{false,true})
            {
                Reset(); sim.SetReserveFleet(true);
                foreach (string id in new[]{"A","B","C"}) sim.SetCondition(id,RobotCondition.Limited);
                var d=sim.Robots.Single(r=>r.RobotId=="D"); sim.Fleet.Deploy("D");
                var food=sim.QueueTask(DeliveryKind.Food,0,GuardianMenu.Hamburg);
                yield return Wait(()=>food.Phase==TaskPhase.Completed && d.PhysicsDriver.ApproachingHome && Vector3.Distance(d.PhysicsDriver.Body.position,d.PhysicsDriver.Home)<.8f,220,"D reaches final home segment before "+(recovery?"recover":"retire"));
                if (failed) yield break;
                var command=recovery?sim.Fleet.RecoverToStandby("D"):sim.Fleet.Retire("D");
                Check(d.PhysicsDriver.DeferredReplan,"late-home operator request is deferred while the admitted leg is active");
                yield return Wait(()=>command.State==GuardianCommandState.Completed && d.IsIdle && d.FleetRole==GuardianFleetRole.Standby,60,"late-home operation completes physical standby arrival");
                if (failed) yield break;
                Check(!d.PhysicsDriver.DeferredReplan,"parking consumes the deferred home request before reuse");
                if (failed) yield break;
                Check(sim.Fleet.Deploy("D").State==GuardianCommandState.Accepted,"same actor redeploys after late-home operation");
                var soup=sim.QueueTask(DeliveryKind.Soup,1);
                yield return Wait(()=>soup.Phase==TaskPhase.Completed && sim.Robots.All(r=>r.IsIdle),220,"redeployed D completes next soup with real pickup/dropoff callbacks");
                Check(soup.AssignedTo==d && sim.Traffic.Reservations.Count==0 && sim.Traffic.JobClaimCount==0 && sim.Traffic.Owner==null,"next assignment retains identity and fully releases claims");
                if (failed) yield break;
            }
        }
        private IEnumerator FleetOperations()
        {
            Reset(); sim.SetReserveFleet(true);
            Check(sim.Robots.Count==5 && sim.Robots.Count(r=>r.DispatchEnabled)==3 && sim.Robots.Count(r=>r.FleetRole==GuardianFleetRole.Standby)==2,"five physical actors: three active, two standby");
            Check(sim.Robots.Select(r=>r.PhysicsDriver.Home).Distinct().Count()==5 && sim.Robots.All(r=>!r.PhysicsDriver.Body.isKinematic),"five unique physical bays and dynamic bodies");
            var first=sim.Fleet.Deploy("D"); var second=sim.Fleet.Deploy("E");
            Check(first.State==GuardianCommandState.Accepted && second.State==GuardianCommandState.Accepted && sim.Robots.All(r=>r.DispatchEnabled),"both standby actors accepted for deployment");
            Check(sim.Fleet.Deploy("D").State==GuardianCommandState.Blocked,"duplicate deployment rejected");
            Check(sim.Fleet.Deploy().State==GuardianCommandState.Blocked,"no standby available reported without duplicate actor");
            yield return new WaitForSeconds(.2f);
            Check(first.State==GuardianCommandState.Executing && first.Reason.Contains("waiting"),"deployment awaits actual eligible work with reason");
            Reset(); sim.SetReserveFleet(true);
            DeliveryTask soup=sim.QueueTask(DeliveryKind.Soup,0); string id=soup.DishIds.Single();
            var a=sim.Robots.Single(r=>r.RobotId=="A"); var d=sim.Robots.Single(r=>r.RobotId=="D");
            yield return Wait(()=>a.Carrying && Vector3.Distance(a.PhysicsDriver.Body.position,soup.Pickup)>2,160,"A carrying original dish away from source before retirement");
            if (failed) yield break;
            sim.SetEmergencyStop("A",true); var retirement=sim.Fleet.Retire("A");
            yield return new WaitForSeconds(2);
            Vector3 stopped=a.PhysicsDriver.Body.position;
            Check(retirement.State==GuardianCommandState.Blocked && !retirement.Terminal && retirement.Reason.Contains("Immobilized") && a.FleetRole==GuardianFleetRole.Retiring,"immobilized retirement remains blocked, never reports success");
            Check(sim.Inventory.Items[id].Owner==GuardianDishOwner.Robot && sim.Inventory.Items[id].OwnerKey=="A","blocked retirement retains sole physical cargo ownership");
            Check(sim.Fleet.Retire("A").State==GuardianCommandState.Blocked,"duplicate retirement leaves original command intact");
            var deployment=sim.Fleet.Deploy("D"); sim.SetCondition("B",RobotCondition.Limited); sim.SetCondition("C",RobotCondition.Limited);
            yield return new WaitForSeconds(1);
            Check(Vector3.Distance(stopped,a.PhysicsDriver.Body.position)<.02f,"retirement/deployment does not move immobilized actor displacement="+Vector3.Distance(stopped,a.PhysicsDriver.Body.position).ToString("F6")+" before="+stopped+" after="+a.PhysicsDriver.Body.position);
            var recovery=sim.Fleet.RecoverToStandby("A");
            yield return Wait(()=>d.Carrying && Vector3.Distance(d.PhysicsDriver.Body.position,soup.Pickup)>2,240,"D physically deployed and picked up the same returned dish");
            if (failed) yield break;
            Check(deployment.State==GuardianCommandState.Completed && sim.Inventory.Items[id].OwnerKey=="D","deployment completion requires physical departure and unique handoff");
            var retireD=sim.Fleet.Retire("D"); var deployE=sim.Fleet.Deploy("E");
            yield return Wait(()=>soup.Phase==TaskPhase.Completed && sim.Robots.All(r=>r.IsIdle),360,"E completes twice-handed-off dish while retired actors park");
            if (failed) yield break;
            Check(retirement.State==GuardianCommandState.Completed && recovery.State==GuardianCommandState.Completed && retireD.State==GuardianCommandState.Completed,"retire and recovery finish only after physical standby arrival");
            Check(a.FleetRole==GuardianFleetRole.Standby && d.FleetRole==GuardianFleetRole.Standby && !a.DispatchEnabled && !d.DispatchEnabled,"recovery returns actors to standby without automatic redeployment");
            Check(deployE.State==GuardianCommandState.Completed && soup.DishIds.Single()==id && sim.Inventory.Items[id].Owner==GuardianDishOwner.Table,"E deployment completes original unique dish at table");
            sim.SetReserveFleet(false);
            Check(sim.Robots.Count==3 && sim.GetComponentsInChildren<GuardianRobot>().Length==3,"switching back retains original three actor mode without duplicate spares");
        }
        private IEnumerator MultiWave()
        {
            foreach (bool five in new[]{false,true}) foreach (int seed in new[]{7,42,2026})
            {
                Reset(seed); if (five) { sim.SetReserveFleet(true); sim.Fleet.Deploy("D"); sim.Fleet.Deploy("E"); }
                foreach (var robot in sim.Robots) sim.SetCondition(robot.RobotId,RobotCondition.Normal);
                float begin=sim.DemoClock;
                for (int wave=0;wave<3;wave++)
                {
                    sim.QueueTask(DeliveryKind.Soup,wave%3);
                    sim.QueueTask(DeliveryKind.Food,(wave+1)%3,(GuardianMenu)(1+wave));
                    sim.QueueTask(DeliveryKind.EmptyDishes,(wave+2)%3);
                    int target=(wave+1)*3;
                    yield return Wait(()=>sim.Tasks.Count(t=>t.Phase==TaskPhase.Completed)>=target && sim.Robots.All(r=>r.IsIdle),360,"wave "+wave+" fleet="+sim.Robots.Count+" seed="+seed+" completes and parks");
                    if (failed) yield break;
                }
                var assigned=sim.Tasks.Where(t=>t.FirstAssignedAt>=0).ToArray();
                Check(parallelWindows.Count>=3 && parallelLast-parallelFirst>30 && workingParallelSeconds>=15,"sustained business concurrency distributed across multiple waves");
                report.Add("WAVES fleet="+sim.Robots.Count+" seed="+seed+" completed="+sim.Tasks.Count(t=>t.Phase==TaskPhase.Completed)+" elapsed="+(sim.DemoClock-begin).ToString("F2")+
                    " waitMean="+assigned.Average(t=>t.FirstAssignedAt-t.CreatedAt).ToString("F2")+" waitMax="+assigned.Max(t=>t.FirstAssignedAt-t.CreatedAt).ToString("F2")+
                    " parallelSeconds="+workingParallelSeconds.ToString("F2")+" workSeconds="+workingSeconds.ToString("F2")+" parallelRatio="+(workingParallelSeconds/Mathf.Max(1,workingSeconds)).ToString("F4")+
                    " maxAssigned="+maxAssigned+" maxMoving="+maxMoving+" windows="+parallelWindows.Count+" maxPenetration="+worstPenetration.ToString("F6")+" longestMotorStall="+sim.Robots.Max(r=>r.PhysicsDriver.MaximumStall).ToString("F2")+" maxReservationOrMotionWait="+sim.Robots.Max(r=>r.PhysicsDriver.MaximumProgressWait).ToString("F2"));
            }
        }
        private IEnumerator DeployDiagnostic()
        {
            for(int mode=0;mode<3;mode++)
            {
                Reset();
                if(mode==1) { sim.Settings.WarningThreshold=.0001f; sim.Settings.LimitedThreshold=1; }
                if(mode==2) { sim.SetMode(GuardianSimulationMode.LegacyStory); yield return new WaitForSeconds(.1f); sim.SetMode(GuardianSimulationMode.Physics); }
                sim.SetReserveFleet(true);
                if(mode==1) { sim.SetCondition("D",RobotCondition.Limited); sim.Fleet.RecoverToStandby("D"); }
                yield return new WaitForSeconds(.5f);
                Check(sim.Robots.All(r=>Mathf.Abs(r.PhysicsDriver.Body.position.y-r.PhysicsDriver.Home.y)<.002f),"all physical heights remain at their initialized home after physics steps");
                foreach(var r in sim.Robots) report.Add("DEPLOY_DIAG mode="+mode+" id="+r.RobotId+" role="+r.FleetRole+" condition="+r.Condition+" idle="+r.IsIdle+" busy="+r.PhysicsDriver.Busy+" home="+r.PhysicsDriver.Home+" body="+r.PhysicsDriver.Body.position+" state="+r.PhysicsDriver.MotionState);
                var command=sim.Fleet.Deploy("D"); Check(command.State==GuardianCommandState.Accepted,"deploy mode="+mode+" state="+command.State+" reason="+command.Reason);
                sim.Settings.WarningThreshold=.3f; sim.Settings.LimitedThreshold=.7f;
            }
        }
        private IEnumerator Diagnose42()
        {
            Reset(42); sim.Settings.MinimumTaskInterval=12; sim.Settings.MaximumTaskInterval=24; sim.Settings.MinimumFaultInterval=30; sim.Settings.MaximumFaultInterval=50; sim.ResetScenario();
            GuardianReplayTrace trace=null;
            if(suite=="replay42") trace=GuardianWaitBreakdown.LoadTrace(Path.Combine(Directory.GetParent(Application.dataPath).FullName,"Recordings/physics-tests/diagnose42/inputs.csv"));
            breakdown=new GuardianWaitBreakdown(sim,trace); sim.AutomaticTasks=trace==null; sim.Settings.RandomFaults=trace==null;
            yield return Wait(()=>sim.DemoClock>=300,310,"same seed42 random-demand/fault window reaches 300s");
            sim.AutomaticTasks=false; sim.Settings.RandomFaults=false;
            foreach(var robot in sim.Robots) sim.SetCondition(robot.RobotId,RobotCondition.Normal);
            breakdown.CaptureInputs();
            yield return Wait(()=>sim.Tasks.All(t=>t.Phase==TaskPhase.Completed) && sim.Robots.All(r=>r.IsIdle) && sim.Traffic.Reservations.Count==0 && sim.Traffic.JobClaimCount==0,600,"same seed42 fully drains and parks after recovery");
            sim.Inventory.ValidateOwnership();
            report.Add("DIAG time="+sim.DemoClock.ToString("F2")+" jobs="+sim.Tasks.Count+" completed="+sim.Tasks.Count(t=>t.Phase==TaskPhase.Completed)+" parallelSeconds="+workingParallelSeconds.ToString("F2")+" workSeconds="+workingSeconds.ToString("F2")+" ratio="+(workingParallelSeconds/Mathf.Max(1,workingSeconds)).ToString("F4"));
            breakdown.Save(output); breakdown=null;
        }
        private IEnumerator StressSeeds()
        {
            foreach (bool five in (suite.StartsWith("contact42")?new[]{false}:suite=="stress42" || suite=="reservation7"?new[]{true}:new[]{false,true})) foreach (int seed in (suite=="reservation7"?new[]{7}:suite=="stress42" || suite.StartsWith("contact42")?new[]{42}:new[]{7,42,2026}))
            {
                Reset(seed); sim.ReserveFleet=five;
                sim.Settings.MinimumTaskInterval=12; sim.Settings.MaximumTaskInterval=24;
                sim.Settings.MinimumFaultInterval=30; sim.Settings.MaximumFaultInterval=50;
                sim.ResetScenario();
                if (five) { sim.Fleet.Deploy("D"); sim.Fleet.Deploy("E"); }
                sim.AutomaticTasks=true; sim.Settings.RandomFaults=true;
                yield return Wait(()=>sim.DemoClock>=300,310,"random faults fleet="+sim.Robots.Count+" seed="+seed+" reached 300 simulated seconds");
                if (failed) yield break;
                Check(sim.Tasks.Count>=10 && sim.Tasks.Any(t=>t.Phase==TaskPhase.Completed),"seed generated and completed work");
                Check(sim.Tasks.All(t=>t.Phase==TaskPhase.Completed || t.AssignedTo!=null || !string.IsNullOrEmpty(t.PendingReason)),"no unaccounted pending task");
                int before=sim.Tasks.Count(t=>t.Phase==TaskPhase.Completed);
                report.Add("FAULT_PHASE fleet="+sim.Robots.Count+" seed="+seed+" completed="+before+" pending="+sim.Tasks.Count(t=>t.Phase==TaskPhase.Pending)+" legitimateCapabilityPending="+sim.Tasks.Count(t=>t.Phase==TaskPhase.Pending && t.PendingReason.StartsWith("All robots ineligible")));
                sim.AutomaticTasks=false; sim.Settings.RandomFaults=false;
                foreach (var robot in sim.Robots) sim.SetCondition(robot.RobotId,RobotCondition.Normal);
                yield return new WaitForFixedUpdate();
                yield return Wait(()=>sim.Tasks.All(t=>t.Phase==TaskPhase.Completed) && sim.Robots.All(r=>r.IsIdle) && sim.Traffic.Reservations.Count==0 && sim.Traffic.QueueLength==0 && sim.Traffic.Owner==null && sim.Traffic.JobClaimCount==0,600,"recovery completes every generated task, parks and releases reservations fleet="+sim.Robots.Count+" seed="+seed);
                if (failed) yield break;
                sim.Inventory.ValidateOwnership();
                Check(sim.Inventory.Items.Values.All(i=>i.Owner!=GuardianDishOwner.Robot && string.IsNullOrEmpty(i.ReservedBy)),"no carried/reserved dishes after full drain");
                var remaining=sim.Tasks.Where(t=>t.Phase!=TaskPhase.Completed).ToArray();
                Check(remaining.Length==0,"every generated task completes after recovery; no permanent source-empty requests");
                var assigned=sim.Tasks.Where(t=>t.FirstAssignedAt>=0).ToArray();
                report.Add("SEED fleet="+sim.Robots.Count+" seed="+seed+" time="+sim.DemoClock.ToString("F2")+" tasks="+sim.Tasks.Count+" completedBeforeRecovery="+before+" completed="+sim.Tasks.Count(t=>t.Phase==TaskPhase.Completed)+" pendingSourceEmpty="+remaining.Length+
                    " waitMean="+assigned.Average(t=>t.FirstAssignedAt-t.CreatedAt).ToString("F2")+" waitMax="+assigned.Max(t=>t.FirstAssignedAt-t.CreatedAt).ToString("F2")+
                    " parallelSeconds="+workingParallelSeconds.ToString("F2")+" workSeconds="+workingSeconds.ToString("F2")+" parallelRatio="+(workingParallelSeconds/Mathf.Max(1,workingSeconds)).ToString("F4")+
                    " maxAssigned="+maxAssigned+" maxMoving="+maxMoving+" windows="+parallelWindows.Count+" maxPenetration="+worstPenetration.ToString("F6")+" longestMotorStall="+sim.Robots.Max(r=>r.PhysicsDriver.MaximumStall).ToString("F2")+" maxReservationOrMotionWait="+sim.Robots.Max(r=>r.PhysicsDriver.MaximumProgressWait).ToString("F2"));
            }
        }
        private void TestPenetration(GuardianPhysicsDriver a,Collider b)
        {
            var rb=b.attachedRigidbody;
            Vector3 p=rb==null?b.transform.position:rb.position;
            Quaternion q=rb==null?b.transform.rotation:rb.rotation;
            bool hit=Physics.ComputePenetration(a.Shape,a.Body.position,a.Body.rotation,b,p,q,out Vector3 axis,out float depth);
            var other=rb==null?null:rb.GetComponent<GuardianRobot>();
            if(other!=null) RecordRobotPair(a,other.PhysicsDriver,hit?depth:0);
            if(!hit) return;
            if(depth>worstPenetration+.0000001f)
            {
                string[] cells={suite,sim.Settings.Seed.ToString(),sim.Robots.Count.ToString(),sim.DemoClock.ToString("F4",System.Globalization.CultureInfo.InvariantCulture),fixedSteps.ToString(),a.Robot.RobotId,b.name,depth.ToString("F9",System.Globalization.CultureInfo.InvariantCulture),axis.ToString("F5"),a.MotionState,p.ToString("F5"),q.eulerAngles.ToString("F5")};
                File.AppendAllText(Path.Combine(output,"penetration-maxima.csv"),string.Join(",",cells.Select(cell=>"\""+cell.Replace("\"","\"\"")+"\""))+"\n");
            }
            worstPenetration=Mathf.Max(worstPenetration,depth);
            if(other!=null && suite!="contact42-baseline" && depth>0)
                throw new InvalidOperationException("Robot pair overlap "+a.Robot.RobotId+" / "+b.name+" depth="+depth+"; required=0m");
            if (depth>.008f) throw new InvalidOperationException("Physical penetration "+a.Robot.RobotId+" / "+b.name+" depth="+depth);
        }
        private void Finish()
        {
            SaveTrafficTrace();
            SaveTrayEvidence();
            SaveRobotPairs();
            active=false; Time.timeScale=1;
            report.Add("Fixed physics steps checked="+fixedSteps+" maximum physical penetration="+worstPenetration.ToString("F6")+"m");
            report.Add("Recorder enabled=false; no video or PNG sequence");
            report.Add(failed?"GUARDIAN_PHYSICS_TEST_FAILED":"GUARDIAN_PHYSICS_TEST_OK");
            File.WriteAllLines(Path.Combine(output,"verification.txt"),report);
            Application.logMessageReceived-=OnLog;
            if (completion!=null) completion(!failed); else Application.Quit(failed?1:0);
        }
    }
}
