using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed class GuardianInteractiveVerification : MonoBehaviour
    {
        public float AutomationTimeScale=8;public bool ExerciseReserveFleet;public string OutputDirectory="docs/evidence/interactive-mvp";
        public bool Done {get;private set;}public bool Passed {get;private set;}
        public object ExportReport=>report;
        public float MaximumMeasuredHumanPenetration=>report.maxHumanPenetration;public int CurrentPhase=>report.phase;
        [Serializable]private sealed class Report
        {
            public string status="FAIL",error="",scope="Headless Unity physics and programmatic input; no user clicks, graphics acceptance or hardware";
            public int checks,phase,peakMoving,peakAssigned;public float clock,crossingAlertLatency,minConservativeGap=float.MaxValue,maxHumanPenetration;public bool resumed,transferred,originalTaskCompleted;
            public string originalDishId;public int originalDishInstance;
            public bool invalidInputWhileMoving,invalidInputStopped,invalidInputResumed;
            public float invalidInputSpeed,invalidInputStopBound,invalidInputStopTravel,assumedReactionSeconds;
            public string crossingAlertMetric="Elapsed Walk request to first hold; not hazard-to-actuator latency";
            public bool physicalForceModel;public float actualLoadedMass,actualEmptyMassAfterTransfer;
        }
        private Report report=new Report();private GuardianInteractiveExperience experience;private GuardianSimulator sim;private GuardianRobot a;
        private DeliveryTask original;private int phase;private float began,phaseAt,crossingAt;private StreamWriter samples;
        private Vector3 invalidLastPosition;
        private IEnumerator Start()
        {
            experience=GetComponent<GuardianInteractiveExperience>();while(!experience.Ready)yield return null;sim=experience.Sim;
            try
            {
                UnitChecks();experience.BeginDemo();a=sim.Robots.Single(r=>r.RobotId=="A");original=sim.Tasks.Single(t=>t.RequestedRobotId=="A");
                report.physicalForceModel=sim.Robots.All(r=>r.PhysicsDriver.Dynamics!=null);Check(report.physicalForceModel,"Force actuator missing from interactive fleet");
                report.originalDishId=original.DishIds.Single();report.originalDishInstance=sim.Inventory.Items[report.originalDishId].GetInstanceID();
                if(ExerciseReserveFleet){sim.Fleet.Deploy("D");sim.Fleet.Deploy("E");sim.QueueTask(DeliveryKind.Food,1,GuardianMenu.CurryRice,"D");sim.QueueTask(DeliveryKind.Food,2,GuardianMenu.Hamburg,"E");}
                Directory.CreateDirectory(OutputDirectory);samples=new StreamWriter(Path.Combine(OutputDirectory,"scenario.csv"));samples.WriteLine("clock,phase,robot,x,z,vx,vz,condition,localHold,task,dishOwner,observed");
                began=phaseAt=sim.DemoClock;Time.timeScale=AutomationTimeScale;sim.PhysicsStepBeginning+=Tick;
            }
            catch(Exception ex){Finish(ex);}
        }
        private void Check(bool condition,string message) {report.checks++;if(!condition)throw new InvalidOperationException(message);}
        private void UnitChecks()
        {
            var l=GuardianInteractiveExperience.ComputeLayout(480,320);Check(l.Panel.xMin>=0 && l.Panel.yMax<=320 && l.Viewport.height>.5f,"Small viewport layout inaccessible");
            var risk=GuardianPedestrianSafety.Predict(Vector3.zero,Vector3.forward*3.8f,new Vector3(-1,0,3),Vector3.right,1.3f,6,.02f);Check(risk.Hazard,"Crossing prediction missed");
            Check(!GuardianPedestrianSafety.Predict(Vector3.zero,Vector3.forward,Vector3.right*4,Vector3.forward,1,6,.02f).Hazard,"Parallel separate tracks falsely intersect");
            var real=sim.Robots[0];var channel=experience.Inputs.Channels[real.RobotId];
            var condition=real.Condition;Check(!experience.Inputs.Submit(real.RobotId,new GuardianEvidenceSample{Timestamp=1,Source="mock",Value=float.NaN,Valid=true},1),"NaN accepted");Check(real.Condition==condition && real.GetComponent<GuardianMotionInterlock>().Held,"Invalid input treated normal");
            experience.Inputs.SelectSource(real.RobotId,GuardianInputOrigin.Measured,"unconfigured-adapter");Check(!channel.Connected && !channel.Calibrated && !channel.Fresh,"Unconnected adapter treated valid");
            experience.Inputs.SelectSource(real.RobotId,GuardianInputOrigin.Mock,"mock");experience.Inputs.Tick(Time.realtimeSinceStartupAsDouble);
            Camera view=experience.CameraRig.View;view.rect=l.Viewport;experience.CameraRig.ResetView();Check(experience.CameraRig.Valid,"Initial camera invalid");
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.name="Temporary camera/selection occluder";wall.transform.position=new Vector3(0,3,0);wall.transform.localScale=new Vector3(.4f,4,4);wall.AddComponent<GuardianCameraObstacle>();Physics.SyncTransforms();
            view.transform.position=wall.transform.position;experience.CameraRig.Step(.2f);Check(experience.CameraRig.Valid && experience.CameraRig.Clear(view.transform.position),"Embedded camera not recovered");
            experience.CameraRig.Pan(new Vector2(5000,-5000));experience.CameraRig.Zoom(100);view.fieldOfView=160;experience.CameraRig.Step(.25f);Check(experience.CameraRig.Valid && view.fieldOfView<=70 && experience.CameraRig.Distance>=2,"Fast pan/zoom/FOV unsafe");
            Check(experience.CameraRig.Radius>=GuardianSafeCamera.NearRadius(view.nearClipPlane,view.fieldOfView,view.aspect)-.00001f,"Near-plane volume too small");
            var target=GameObject.CreatePrimitive(PrimitiveType.Sphere);target.transform.position=new Vector3(0,3,3);target.AddComponent<GuardianInteractionTarget>().DisplayName="Temporary pick target";Physics.SyncTransforms();
            Check(!experience.Pick(new Ray(new Vector3(0,3,-3),Vector3.forward)),"Selected through physical wall");wall.SetActive(false);Physics.SyncTransforms();
            Check(experience.Pick(new Ray(new Vector3(0,3,-3),Vector3.forward)),"Visible target not selected");int version=experience.SelectionVersion;experience.Select(null);Check(!experience.Command(GuardianHumanCommand.Transfer,version),"Stale selection command accepted");
            view.transform.SetPositionAndRotation(new Vector3(0,3,-3),Quaternion.identity);Vector3 screen=view.WorldToScreenPoint(target.transform.position);experience.PointerDown(screen,0);Check(experience.PointerUp(screen) && experience.Selected!=null,"Programmatic screen click did not select visible world target");
            LayoutChecks();experience.CameraRig.Focus(target.transform);DestroyImmediate(target);experience.CameraRig.Step(.2f);Check(experience.Selected==null && experience.CameraRig.Valid,"Destroyed target left invalid selection/camera");Destroy(wall);experience.CameraRig.ResetView();
        }
        private void LayoutChecks()
        {
            var layout=GuardianInteractiveExperience.ComputeLayout(Screen.width,Screen.height);Vector2 p=new Vector2(layout.Panel.center.x,Screen.height-layout.Panel.center.y);
            var before=experience.Selected;experience.PointerDown(p,0);experience.PointerMove(new Vector2(20,Screen.height-80));Check(!experience.PointerUp(new Vector2(20,Screen.height-80)) && experience.Selected==before,"UI drag leaked into world selection");
        }
        private void Tick(float clock)
        {
            if(Done)return;
            try
            {
                report.phase=phase;report.clock=clock-began;int moving=0,assigned=0;
                foreach(var r in sim.Robots)
                {
                    if(r.CurrentTask!=null)assigned++;if(r.PhysicsDriver.Body.linearVelocity.magnitude>.3f)moving++;
                    Vector3 p=r.PhysicsDriver.Body.position,v=r.PhysicsDriver.Body.linearVelocity;
                    float gap=Vector2.Distance(new Vector2(p.x,p.z),new Vector2(experience.Person.Body.position.x,experience.Person.Body.position.z))-r.PhysicsDriver.TurningRadius-GuardianPedestrianMotion.Radius;
                    report.minConservativeGap=Mathf.Min(report.minConservativeGap,gap);
                    if(Physics.ComputePenetration(r.PhysicsDriver.Shape,p,r.PhysicsDriver.Body.rotation,experience.Person.Shape,experience.Person.Body.position,experience.Person.Body.rotation,out Vector3 direction,out float depth))report.maxHumanPenetration=Mathf.Max(report.maxHumanPenetration,depth);
                    if(samples!=null && Mathf.RoundToInt(clock/Time.fixedDeltaTime)%5==0)samples.WriteLine(FormattableString.Invariant($"{clock:F4},{phase},{r.RobotId},{p.x:F4},{p.z:F4},{v.x:F4},{v.z:F4},{r.Condition},{r.GetComponent<GuardianMotionInterlock>().Held},{r.CurrentTask?.Id},{sim.Inventory.Items[report.originalDishId].Owner},{experience.Safety.Observed}"));
                }
                report.peakMoving=Mathf.Max(report.peakMoving,moving);report.peakAssigned=Mathf.Max(report.peakAssigned,assigned);
                Check(report.maxHumanPenetration<.001f,"Human/robot physical penetration");
                var dish=sim.Inventory.Items[report.originalDishId];Check(dish.GetInstanceID()==report.originalDishInstance && dish.gameObject.activeInHierarchy,"Dish identity/activity lost");
                if(phase==4)
                {
                    var motor=a.PhysicsDriver;report.invalidInputStopTravel+=Vector3.Distance(invalidLastPosition,motor.Body.position);invalidLastPosition=motor.Body.position;
                    if(motor.PlanarVelocity.magnitude<.005f)
                    {
                        report.invalidInputStopped=true;Check(report.invalidInputStopTravel<=report.invalidInputStopBound+.01f,"Invalid-input force stop exceeded bound");
                        Check(a.CurrentTask==original && a.Carrying && dish.OwnerKey=="A" && a.Condition==RobotCondition.Normal,"Invalid input changed cargo/task/capability");
                        if(a.GetComponent<GuardianMotionInterlock>().CanRelease(clock)){Check(experience.ResumeSafety(a),"Valid fresh input + guarded ACK refused");report.invalidInputResumed=true;phase=0;phaseAt=clock;}
                    }
                }
                if(phase==0 && a.Carrying && Mathf.Abs(a.PhysicsDriver.Body.position.x+1)<.4f && a.PhysicsDriver.Body.linearVelocity.z<-.6f)
                {
                    if(!report.invalidInputWhileMoving)
                    {
                        var motor=a.PhysicsDriver;report.invalidInputWhileMoving=true;report.invalidInputSpeed=motor.PlanarVelocity.magnitude;report.invalidInputStopBound=motor.Dynamics.StoppingBound(report.invalidInputSpeed,motor.EffectiveMaximumSpeed,Time.fixedDeltaTime);report.assumedReactionSeconds=motor.Dynamics.ReactionSeconds(Time.fixedDeltaTime);invalidLastPosition=motor.Body.position;
                        report.actualLoadedMass=motor.Dynamics.Mass;Check(Mathf.Abs(report.actualLoadedMass-sim.Settings.Physical.ChassisMass-sim.Settings.Physical.SoupMass)<.0001f,"Actual soup ownership did not update mass");
                        Check(!experience.Inputs.Submit("A",new GuardianEvidenceSample{Timestamp=Time.realtimeSinceStartupAsDouble,Source="mock",Value=float.NaN,Valid=true,Origin=GuardianInputOrigin.Mock},Time.realtimeSinceStartupAsDouble),"Moving invalid sample accepted");
                        Check((a.GetComponent<GuardianMotionInterlock>().Current&GuardianMotionHold.InputUnavailable)!=0,"Moving invalid sample did not hold");phase=4;phaseAt=clock;
                    }
                    else {experience.Person.Walk();crossingAt=phaseAt=clock;phase=1;experience.Add("AUTOMATION: adult crossing starts");}
                }
                else if(phase==1 && a.GetComponent<GuardianMotionInterlock>().Held)
                {
                    report.crossingAlertLatency=clock-crossingAt;Check(a.Condition==RobotCondition.Normal && a.CurrentTask==original && a.Carrying && dish.OwnerKey=="A","Pedestrian stop changed capability/task/owner");
                    experience.Select(a.GetComponent<GuardianInteractionTarget>());Check(!experience.ResumeSafety(a),"Unsafe crossing restart accepted");Check(!experience.Command(GuardianHumanCommand.ContinueDelivery,experience.SelectionVersion),"Unsafe motion command accepted");phase=2;phaseAt=clock;
                }
                else if(phase==2 && experience.Person.Body.position.x>=experience.Person.EndPoint.x-.03f)
                {
                    bool clear=true;foreach(var r in sim.Robots)if(r.GetComponent<GuardianMotionInterlock>().Held && !r.GetComponent<GuardianMotionInterlock>().CanRelease(clock))clear=false;
                    if(clear)
                    {
                        foreach(var r in sim.Robots)if(r.GetComponent<GuardianMotionInterlock>().Held)Check(experience.ResumeSafety(r),"Safe acknowledgement refused");
                        Check(a.CurrentTask==original && dish.OwnerKey=="A" && a.Carrying,"Ownership changed during crossing hold");report.resumed=true;
                        var channel=experience.Inputs.Channels["A"];channel.MockValue=.85f;
                        Check(experience.Inputs.Submit("A",new GuardianEvidenceSample{Timestamp=Time.realtimeSinceStartupAsDouble,Source=channel.Source,Metric=channel.Metric,Unit=channel.Unit,Clock=GuardianEvidenceInput.ReceiverClock,Value=.85f,Valid=true,Origin=GuardianInputOrigin.Mock},Time.realtimeSinceStartupAsDouble),"Mock fault input refused");
                        experience.Select(a.GetComponent<GuardianInteractionTarget>());Check(a.Condition==RobotCondition.Limited && a.CurrentTask==original,"Fault lost original task");
                        Check(!experience.Command(GuardianHumanCommand.ContinueDelivery,experience.SelectionVersion),"Limited soup continuation accepted");Check(experience.Command(GuardianHumanCommand.Transfer,experience.SelectionVersion),"Safe physical handoff request refused");report.transferred=true;phase=3;phaseAt=clock;
                    }
                }
                else if(phase==3 && original.Phase==TaskPhase.Completed)
                {
                    Check(original.AssignedTo!=a && dish.Owner==GuardianDishOwner.Table,"Handoff did not finish with eligible replacement");report.originalTaskCompleted=true;
                    report.actualEmptyMassAfterTransfer=a.PhysicsDriver.Dynamics.Mass;Check(Mathf.Abs(report.actualEmptyMassAfterTransfer-sim.Settings.Physical.ChassisMass)<.0001f,"Returned soup mass remained on original robot");
                    Check(report.peakAssigned>=3 && report.peakMoving>=2,"Concurrent fleet operation not observed");Check(report.invalidInputStopped && report.invalidInputResumed,"Moving invalid-input stop/resume missing");Finish(null);
                }
                if(clock-phaseAt>(ExerciseReserveFleet?240:120) || clock-began>(ExerciseReserveFleet?420:300))throw new TimeoutException("Interactive scenario phase "+phase+" stalled; "+string.Join("\n",sim.Robots.Select(r=>r.RobotId+" "+r.PhysicsDriver.MotionState+" "+r.PhysicsDriver.DiagnosticRoute+" stop="+r.PhysicsDriver.LastStoppingDistance)));
            }
            catch(Exception ex){Finish(ex);}
        }
        private void Finish(Exception error)
        {
            if(Done)return;Done=true;Passed=error==null;report.status=Passed?"PASS":"FAIL";report.error=error?.ToString()??"";
            if(sim!=null)sim.PhysicsStepBeginning-=Tick;samples?.Dispose();Time.timeScale=1;
            if(error!=null)Debug.LogException(error);Debug.Log("GUARDIAN_INTERACTIVE_MVP "+report.status);
        }
        private void OnDestroy() {if(sim!=null)sim.PhysicsStepBeginning-=Tick;samples?.Dispose();}
    }
}
