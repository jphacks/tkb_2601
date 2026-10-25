using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Explicit headless contract tests. Does not claim native mouse or rendered GUI coverage.</summary>
    public sealed class GuardianDemoVerification : MonoBehaviour
    {
        [Serializable] public sealed class Report
        {public string status,unity,graphics,scope="Real Unity fixed steps and selection/decision APIs; no GUI/native mouse acceptance";public List<string> checks=new List<string>();public List<string> trials=new List<string>();}
        private readonly Report report=new Report();
        public Report ExportReport=>report;
        public bool Done {get;private set;}
        public bool Passed {get;private set;}=true;
        private GuardianSimulator sim;
        private IEnumerator routine;
        private void Start(){sim=GetComponent<GuardianSimulator>();routine=Run();}
        private void Update()
        {
            if(Done || routine==null)return;
            try {if(!routine.MoveNext())Finish();}
            catch(Exception e){Passed=false;report.checks.Add("EXCEPTION "+e);Finish();}
        }
        private void Require(bool valid,string text){report.checks.Add((valid?"PASS ":"FAIL ")+text);if(!valid)Passed=false;}
        private GuardianRobot Robot(string id)=>sim.Robots.Single(r=>r.RobotId==id);
        private IEnumerator Run()
        {
            yield return null;
            if(Array.IndexOf(Environment.GetCommandLineArgs(),"-guardianConversationOnly")>=0)
            {VoiceContractChecks();yield break;}
            var ui=sim.DemoInterface;sim.EnableDemoEvidence();sim.RefreshDemoEvidence();
            Require(sim.HumanExperienceActive && Robot("B").EmergencyStopped && ui.SelectedId==null,"input detection creates the initial alert without opening a decision card");
            int initialRevision=sim.HumanExperience.Actors["B"].Revision;float initialValue=Robot("B").Vibration;
            ui.Select("A");Require(!ui.CardVisible && ui.SelectedId==null,"normal robot selection opens no panel or choices");
            ui.Select("B");Require(ui.CardVisible && initialRevision==sim.HumanExperience.Actors["B"].Revision && initialValue==Robot("B").Vibration,"alert selection observes state without injecting or revising a fault");
            var mark=Robot("B").GetComponent<GuardianRobotAlertMarker>();mark.Refresh();
            Require(mark.Visible && mark.Symbol=="×" && mark.Caption.Contains("停止") && mark.TabletHitTarget.isTrigger,"stopped robot has a symbolic Japanese mark and non-blocking chest tablet target");
            ui.Close();Physics.SyncTransforms();var target=mark.TabletHitTarget.bounds.center;
            Require(ui.Pick(new Ray(target+Robot("B").transform.forward*2,-Robot("B").transform.forward)) && ui.SelectedId=="B","ray selection of an alert tablet opens only its robot's card");
            ui.Close();Require(!ui.CardVisible && Robot("B").EmergencyStopped,"closing card preserves the detected safety stop");
            InputContractChecks();
            var human=sim.EnterHumanExperience(false,false);
            // Begin resets source freshness. Observe the first current sample before
            // taking a decision token; Choose must still reject a freshness change.
            sim.RefreshDemoEvidence();
            Require(ui!=null,"normal simulator has the contextual interface");
            var food=sim.QueueTask(DeliveryKind.Food,0,GuardianMenu.CurryRice,"A");
            human.InjectEvidence("A",RobotCondition.Warning,false);ui.Select("A");ui.RefreshState();
            Require(ui.NeedsDecision && ui.Summary.Contains("模擬振動") && ui.Summary.Contains(food.Id),"selected alert explains actual condition and task in Japanese");
            int sel=ui.SelectionVersion,state=ui.StateVersion;
            var result=ui.Choose("A",sel,state,GuardianDemoChoice.ContinueCapable);
            Require(result.Accepted && Robot("A").Condition==RobotCondition.Warning && !Robot("A").EmergencyStopped,"continue allows eligible food without changing capability");
            var activeMark=Robot("A").GetComponent<GuardianRobotAlertMarker>();activeMark.Refresh();
            Require(activeMark.Visible && activeMark.Symbol=="!" && !ui.CardVisible,"acknowledged restriction retains its ! marker while successful action closes choices");
            Require(!ui.Choose("A",sel,state,GuardianDemoChoice.ContinueCapable).Accepted && ui.AcceptedCount==1,"queued double click cannot execute twice");
            human.InjectEvidence("A",RobotCondition.Warning,false);ui.Select("A");sel=ui.SelectionVersion;state=ui.StateVersion;
            human.InjectEvidence("B",RobotCondition.Warning,false);ui.Select("B");Require(!ui.Choose("A",sel,state,GuardianDemoChoice.KeepStopped).Accepted && Robot("A").EmergencyStopped,"stale target cannot affect another selected robot");
            ui.Select("A");sel=ui.SelectionVersion;state=ui.StateVersion;
            human.InjectEvidence("A",RobotCondition.Limited,true);
            Require(!ui.Choose("A",sel,state,GuardianDemoChoice.ContinueCapable).Accepted,"new evidence invalidates old choices");
            ui.RefreshState();Require(!ui.Choose("A",ui.SelectionVersion,ui.StateVersion,GuardianDemoChoice.Transfer).Accepted && Robot("A").EmergencyStopped,"unsafe mobility refuses transfer and preserves stop");
            ui.RefreshState();Require(ui.Choose("A",ui.SelectionVersion,ui.StateVersion,GuardianDemoChoice.KeepStopped).Accepted,"operator can explicitly keep an unsafe robot stopped");
            for(int i=0;i<8;i++)yield return null;
            ui.TrackOperation();Require(ui.OperationStatus.StartsWith("完了"),"accepted stop exposes physical completion status");
            ui.Close();Require(ui.SelectedId==null && !ui.Choose("A",sel,state,GuardianDemoChoice.Transfer).Accepted,"close invalidates pending decisions");
            ui.Select("B");ui.ShowHeader=true;Vector2 header=new Vector2(24,Screen.height-24);ui.PointerDown(header,0);ui.PointerMove(header+Vector2.right*60);ui.PointerUp(header+Vector2.right*60);
            Require(ui.SelectedId=="B","pointer gesture starting on UI cannot select through it");ui.ShowHeader=false;
            var point=new Vector2(Screen.width*.4f,Screen.height*.5f);ui.PointerDown(point,0);ui.PointerMove(point+Vector2.right*50);ui.PointerUp(point+Vector2.right*50);
            Require(ui.SelectedId=="B","camera drag release is not interpreted as robot selection");
            sim.Inventory.ValidateOwnership();Require(true,"dish identities, owners, reservations and tray mounts remain valid after decisions");
            human.EndWithoutReset();
            sim.OperatorSupervisionEnabled=false;sim.ResetScenario();sim.SetCondition("A",RobotCondition.Normal);
            ui.Select("A");activeMark.Refresh();Require(!activeMark.Visible && !ui.CardVisible,"resolved healthy robot has no alert marker or decision panel");
            VoiceContractChecks();
            foreach(int seed in new[]{2026,42,7})
            {
                var trial=MovementTrial(seed);while(trial.MoveNext())yield return null;
            }
        }
        private sealed class MockRecognizer : IGuardianSpeechRecognizer
        {public GuardianSpeechMessage Message;public bool Pending=true;public bool TryRead(out GuardianSpeechMessage message){message=Message;if(!Pending)return false;Pending=false;return true;}}
        private sealed class MockOutput : IGuardianSpeechOutput
        {public readonly List<string> Lines=new List<string>();public void Say(string text){Lines.Add(text);}}
        private void VoiceContractChecks()
        {
            var human=sim.EnterHumanExperience(false,false);var operations=sim.DemoInterface;
            sim.EnableDemoEvidence();sim.RefreshDemoEvidence();
            var task=sim.QueueTask(DeliveryKind.Food,0,GuardianMenu.CurryRice,"A");human.InjectEvidence("A",RobotCondition.Warning,false);human.InjectEvidence("B",RobotCondition.Warning,false);
            var chat=new GuardianRuleConversation(sim);int accepted=operations.AcceptedCount;double now=Time.realtimeSinceStartupAsDouble;int sequence=0;
            Func<string,float,GuardianSpeechMessage> msg=(text,confidence)=>new GuardianSpeechMessage{Id="voice-test-"+(sequence++),Text=text,Confidence=confidence,ReceivedAt=now};
            chat.Process(msg("引き継いで",1),now);Require(!chat.HasPending && operations.AcceptedCount==accepted,"conversation refuses implicit target when several robots have alerts");
            chat.Process(msg("F停止",1),now);Require(!chat.HasPending,"unknown conversation target is refused");
            chat.Process(msg("A止めないで",1),now);Require(!chat.HasPending,"unsupported or negated instructions never map to an action");
            chat.Process(msg("A停止",.2f),now);Require(!chat.HasPending && operations.AcceptedCount==accepted,"low-confidence speech is clarification only");
            chat.Process(msg("A引き継いで",1),now);Require(chat.HasPending && operations.AcceptedCount==accepted && !operations.CardVisible,"conversation proposes a target/action without executing or opening the click panel");
            var confirm=msg("実行",1);chat.Process(confirm,now);Require(operations.AcceptedCount==accepted+1 && !chat.HasPending,"explicit conversation confirmation uses the shared guarded command path");
            chat.Process(confirm,now);chat.Process(msg("実行",1),now);Require(operations.AcceptedCount==accepted+1,"repeated speech message and repeated confirmation do not execute twice");
            human.InjectEvidence("A",RobotCondition.Warning,false);chat.Process(msg("A停止",1),now);human.InjectEvidence("A",RobotCondition.Limited,true);
            chat.Process(msg("実行",1),now);Require(operations.AcceptedCount==accepted+1 && chat.Reply.StartsWith("拒否"),"conversation reconfirms the exact current state and rejects an outdated proposal");
            chat.Process(msg("A続けて",1),now);chat.Process(msg("実行",1),now);Require(operations.AcceptedCount==accepted+1 && Robot("A").EmergencyStopped,"conversation cannot bypass an unsafe mobility hold");
            chat.Process(msg("B停止",1),now);chat.Process(msg("キャンセル",1),now);chat.Process(msg("実行",1),now);Require(!chat.HasPending && operations.AcceptedCount==accepted+1,"cancellation consumes the pending conversation operation");
            var expired=msg("B停止",1);expired.ReceivedAt=now-11;chat.Process(expired,now);Require(!chat.HasPending,"expired speech envelopes are not executable");
            chat.Process(msg("C停止",1),now);Require(!chat.HasPending,"normal robot cannot be turned into a fault by conversation selection");
            string root=Path.Combine("Recordings","conversation-contract");Directory.CreateDirectory(root);string request=Path.Combine(root,"test.request");
            File.WriteAllLines(request,new[]{"local-text-test","1",Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("B停止"))});
            var bridge=new GuardianLocalSpeechBridge(root);Require(bridge.TryRead(out var packet) && packet.Text=="B停止" && packet.Confidence==1,"local text envelope traverses the replaceable recognition boundary");
            bridge.Say("確認しました");Require(File.ReadAllText(Path.Combine(root,"reply.txt"))=="確認しました","local response adapter writes text without speaker playback");
            if(sim.Conversation!=null)
            {
                var output=new MockOutput();sim.Conversation.Recognizer=new MockRecognizer{Message=msg("状況",1)};sim.Conversation.Output=output;sim.Conversation.Poll();
                Require(sim.InteractionMode==GuardianInteractionMode.RuleVoice && output.Lines.Any(l=>l.Contains("ロボット")),"voice-mode bootstrap accepts replaceable recognizer/output mocks and Japanese alert guidance");
            }
            sim.Inventory.ValidateOwnership();human.EndWithoutReset();
        }
        private sealed class SampleAdapter : IGuardianEvidenceAdapter
        {
            public string Id;public GuardianEvidenceSample Sample;private bool pending=true;
            public bool TryRead(out string id,out GuardianEvidenceSample sample){id=Id;sample=Sample;if(!pending)return false;pending=false;return true;}
        }
        private void InputContractChecks()
        {
            sim.HumanExperience?.EndWithoutReset();sim.OperatorSupervisionEnabled=false;
            var input=new GuardianEvidenceInput();input.Initialize(sim);
            input.SelectSource("A",GuardianInputOrigin.Measured,"contract-mock");var c=input.Channels["A"];var hold=Robot("A").GetComponent<GuardianMotionInterlock>();
            var sample=new GuardianEvidenceSample{Timestamp=100,Source=c.Source,Metric=c.Metric,Unit=c.Unit,Clock=GuardianEvidenceInput.ReceiverClock,Value=.12f,Valid=true,Origin=GuardianInputOrigin.Measured};
            Require(!input.Submit("A",sample,100) && hold.Held,"unconnected uncalibrated external source is unavailable rather than normal");
            c.Connected=c.Calibrated=true;
            Require(input.Poll(new SampleAdapter{Id="A",Sample=sample},100)==1 && c.Fresh,"mock adapter uses the same bounded input contract as a future device adapter");
            var original=Robot("A").Condition;sample.Timestamp=101;sample.Unit="unsupported-unit";
            Require(!input.Submit("A",sample,101) && Robot("A").Condition==original,"unit mismatch is rejected without converting to a normal value");sample.Unit=c.Unit;
            sample.Metric="unsupported-metric";Require(!input.Submit("A",sample,101),"unsupported metric is rejected");sample.Metric=c.Metric;
            sample.Clock="device-clock-not-mapped";Require(!input.Submit("A",sample,101),"unmapped measurement clock is rejected");sample.Clock=GuardianEvidenceInput.ReceiverClock;
            sample.Timestamp=102;Require(!input.Submit("A",sample,101),"future acquisition time is rejected");
            sample.Timestamp=98;Require(!input.Submit("A",sample,101),"old acquisition is rejected even when newly received");
            sample.Timestamp=100;Require(!input.Submit("A",sample,101),"duplicate or out-of-order source timestamp is rejected");
            sample.Timestamp=101;sample.Value=float.NaN;Require(!input.Submit("A",sample,101),"non-finite samples preserve the input safety hold");sample.Value=.12f;
            sample.Unit=null;Require(!input.Submit("A",sample,101),"missing unit is unavailable");sample.Unit=c.Unit;
            Require(input.Submit("A",sample,101) && c.Fresh && hold.Held && !hold.Release(sim.DemoClock),"fresh recovery clears current input fault but does not automatically release the latched stop");
            input.Tick(103);Require(!c.Fresh && hold.Held && !hold.Release(sim.DemoClock+1),"missing external samples become stale and cannot release a stop");
            sample.Timestamp=103;
            bool delayedAccepted=input.Submit("A",sample,104.4);
            input.Tick(104.6);
            Require(delayedAccepted && !c.Fresh && hold.Held,"accepted delayed evidence expires from acquisition time even before the receive timeout");
            Require(!input.SetMockValue("A",0),"external-source selection never silently falls back to mock zero");
            Require(!input.Submit("unknown-robot",sample,103),"unknown robot identity is rejected");
            Require(!input.Submit(null,sample,103),"missing robot identity is rejected without a default target");
            sim.ResetScenario();sim.OperatorSupervisionEnabled=true;
        }
        private IEnumerator MovementTrial(int seed)
        {
            Time.timeScale=4;
            sim.HumanExperience?.EndWithoutReset();sim.OperatorSupervisionEnabled=false;sim.Settings.Seed=seed;
            sim.Settings.WalkingGuests=true;sim.Settings.RandomFaults=false;sim.AutomaticTasks=false;sim.Settings.MealSeconds=4;
            sim.Settings.Physical.Enabled=true;sim.Settings.MotionSpeedMultiplier=seed==42?2:1;sim.MeasureFixedPerformance=true;
            sim.ResetScenario();foreach(var robot in sim.Robots)sim.SetCondition(robot.RobotId,RobotCondition.Normal);
            sim.OperatorSupervisionEnabled=true;
            Require(sim.Walkers!=null && sim.Walkers.People.Count==2 && sim.Traffic.DirectActive,"seed "+seed+": two animated guests share the existing direct grid/queues");
            var tasks=new[]{sim.QueueTask(DeliveryKind.Food,0,GuardianMenu.CurryRice,"A"),sim.QueueTask(DeliveryKind.Food,1,GuardianMenu.Hamburg,"B"),sim.QueueTask(DeliveryKind.Soup,2,GuardianMenu.Soup,"C")};
            bool injected=false,reassigned=false,concurrent=false,animated=false;int overlaps=0,cycles=0,samples=0;float penetration=0,next=0;
            long perfId=-1;var fixedMs=new List<double>();var rows=new List<string>{"sim,actor,x,z,phase,wait"};
            while(sim.DemoClock<110)
            {
                if(seed==2026 && !injected && Robot("B").Carrying)
                {
                    var ids=tasks[1].DishIds.ToArray();sim.SubmitVibration("B",.9f);var ui=sim.DemoInterface;ui.Select("B");ui.RefreshState();
                    Require(ui.Choose("B",ui.SelectionVersion,ui.StateVersion,GuardianDemoChoice.Transfer).Accepted,"loaded food restriction accepts operator transfer");
                    Require(ui.OperationStatus.StartsWith("受理"),"transfer first exposes acceptance rather than claiming completion");
                    Require(ids.SequenceEqual(tasks[1].DishIds) && Robot("B").Carrying && tasks[1].Phase==TaskPhase.ReturningCargo,"loaded transfer preserves dish identities until physical source return");injected=true;
                    ui.TrackOperation();Require(ui.OperationStatus.StartsWith("実行中"),"physical cargo return exposes in-progress status");
                }
                if(injected && tasks[1].AssignedTo!=null && tasks[1].AssignedTo.RobotId!="B")reassigned=true;
                if(sim.PerformanceSampleId!=perfId){perfId=sim.PerformanceSampleId;fixedMs.Add(sim.LastFixedElapsedTicks*1000.0/System.Diagnostics.Stopwatch.Frequency);}
                if(sim.DemoClock>=next)
                {
                    next=sim.DemoClock+.1f;samples++;sim.Inventory.ValidateOwnership();
                    if(sim.Robots.Count(r=>r.PhysicsDriver.PlanarVelocity.magnitude>.1f)>=2 && sim.Walkers.People.Any(w=>w.Motion.Velocity.magnitude>.1f))concurrent=true;
                    if(sim.Walkers.People.All(w=>w.Motion.Velocity.magnitude>.1f && w.Motion.GetComponent<RiggedRestaurantGuest>().Animator.GetCurrentAnimatorStateInfo(0).fullPathHash==Animator.StringToHash("Base Layer.Walk")))animated=true;
                    string cycle=sim.Traffic.Direct.FindCycle();if(cycle!=null)cycles++;
                    var shapes=sim.Robots.Select(r=>(Collider)r.PhysicsDriver.Shape).Concat(sim.Walkers.People.Select(w=>(Collider)w.Motion.Shape)).ToArray();
                    for(int i=0;i<shapes.Length;i++)
                    {
                        for(int j=i+1;j<shapes.Length;j++)MeasureContact(shapes[i],shapes[j],ref overlaps,ref penetration);
                        foreach(var obstacle in sim.PhysicsWorld.Obstacles)if(obstacle.GetComponent<GuardianPhysicsObstacle>()?.Floor!=true)MeasureContact(shapes[i],obstacle,ref overlaps,ref penetration);
                    }
                    if(samples%10==0)
                    {
                        foreach(var r in sim.Robots)rows.Add(sim.DemoClock.ToString("F2")+","+r.RobotId+","+r.transform.position.x.ToString("F3")+","+r.transform.position.z.ToString("F3")+","+r.CurrentTask?.Phase+",\""+r.PhysicsDriver.WaitReason+"\"");
                        foreach(var w in sim.Walkers.People)rows.Add(sim.DemoClock.ToString("F2")+","+w.Id+","+w.Motion.Body.position.x.ToString("F3")+","+w.Motion.Body.position.z.ToString("F3")+","+w.CompletedLegs+",\""+w.Motion.WaitReason+"\"");
                        Directory.CreateDirectory("docs/evidence/contextual-demo");File.WriteAllLines("docs/evidence/contextual-demo/trace-"+seed+".csv",rows);
                    }
                }
                yield return null;
            }
            fixedMs.Sort();
            string summary="seed="+seed+" speed="+sim.MotionSpeedMultiplier+"x sim="+sim.DemoClock.ToString("F2")+" completed="+sim.Tasks.Count(t=>t.Phase==TaskPhase.Completed)+"/"+sim.Tasks.Count
                +" collisionSamples="+overlaps+" maximumPenetration="+penetration.ToString("F5")+" cycles="+cycles+" concurrent="+concurrent+" robotPlans="+sim.Traffic.DirectPlanAttempts+" guestPlans="+sim.Walkers.PlanningAttempts
                +" guests="+string.Join(";",sim.Walkers.People.Select(w=>w.Id+" legs="+w.CompletedLegs+" distance="+w.Distance.ToString("F2")+" maxStationary="+w.MaximumBlocked.ToString("F2")))
                +" fixedCodeP95Ms="+(fixedMs.Count>0?fixedMs[(int)((fixedMs.Count-1)*.95)].ToString("F3"):"unavailable");
            foreach(var r in sim.Robots.Where(r=>!r.IsIdle))summary+=" UNFINISHED "+r.RobotId+" "+r.PhysicsDriver.MotionState+" "+r.PhysicsDriver.DiagnosticRoute;
            report.trials.Add(summary);
            Require(concurrent,"seed "+seed+": robots and walking guests move concurrently");
            Require(overlaps==0 && cycles==0,"seed "+seed+": sampled physical penetration and reservation cycles absent");
            Require(tasks.All(t=>t.Phase==TaskPhase.Completed) && sim.Robots.All(r=>r.IsIdle),"seed "+seed+": requested meals complete and all robots return home");
            Require(sim.Walkers.People.All(w=>w.CompletedLegs>=8 && w.Distance>20),"seed "+seed+": both people keep walking node circuits");
            Require(animated,"seed "+seed+": authored walk animations run while both people move");
            if(seed==2026)
            {
                Require(injected && reassigned && tasks[1].Phase==TaskPhase.Completed,"loaded restriction, physical handoff and delivery by another actor complete");
                sim.DemoInterface.TrackOperation();Require(sim.DemoInterface.OperationStatus.StartsWith("完了"),"transfer shows completion only after physical task completion");
            }
            Directory.CreateDirectory("docs/evidence/contextual-demo");File.WriteAllLines("docs/evidence/contextual-demo/trace-"+seed+".csv",rows);
        }
        private static void MeasureContact(Collider a,Collider b,ref int contacts,ref float maximum)
        {
            if(a==null || b==null || !a.enabled || !b.enabled)return;
            if(Physics.ComputePenetration(a,a.transform.position,a.transform.rotation,b,b.transform.position,b.transform.rotation,out var direction,out float depth) && depth>.002f)
            {contacts++;maximum=Mathf.Max(maximum,depth);}
        }
        private void Finish()
        {
            report.status=Passed?"PASS":"FAIL";report.unity=Application.unityVersion;report.graphics=SystemInfo.graphicsDeviceType.ToString();
            Debug.Log("GUARDIAN_CONTEXTUAL_DEMO_"+report.status);Done=true;
        }
    }
}
