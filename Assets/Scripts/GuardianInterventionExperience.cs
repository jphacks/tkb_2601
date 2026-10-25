using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuardianHumanCommand { ContinueDelivery, ClearDishes, Transfer, KeepStopped, DeploySpare, Inspect, ResumeWork }
    public enum GuardianHumanStage { Inactive, InitialRun, YellowDecision, WaitingTransfer, RedDecision, WaitingClear, UnsafeDecision, WaitingFinal, Complete, Free }
    public sealed class GuardianHumanActor
    {
        public string Id,Explanation;
        public int Revision;
        public bool AwaitingDecision,MobilityUnsafe,HoldStopped;
        // Live, KeepServing: the lamp went yellow while the robot kept working. Soup stays off, even after the lamp is
        // green again, until a person confirms ("作業受付を再開" or the inspection).
        public bool LiquidsHeld;
        public RobotCondition PreviousCondition,AlertCondition;
        public string AlertCause,DecisionFeedback;
        public float AlertClock,AlertVibration;
        internal DeliveryTask HeldTask;
    }
    public sealed class GuardianHumanEvent
    {
        public int Sequence;
        public float Clock;
        public string Kind,Actor,Message;
    }
    public struct GuardianHumanResult
    {
        public bool Accepted;
        public string Reason,TaskId;
    }

    /// <summary>Local, deterministic human decisions over the existing physical task engine.</summary>
    public sealed partial class GuardianInterventionExperience : MonoBehaviour
    {
        private GuardianSimulator sim;
        private bool saved,muting,mutingStop,oldAuto,oldFaults,oldFive;
        private float oldMeal,oldDeparture,oldScale;
        private int oldSeed,dishCursor,sequence;
        private readonly Dictionary<string,GuardianHumanActor> actors=new Dictionary<string,GuardianHumanActor>();
        private struct RobotSnapshot {public RobotCondition Condition;public GuardianFleetRole Role;public bool Stopped;public DeliveryTask Task;public TaskPhase Phase;public string Wait;}
        private struct TaskSnapshot {public TaskPhase Phase;public GuardianRobot Assigned;}
        private readonly Dictionary<string,RobotSnapshot> robotSnapshots=new Dictionary<string,RobotSnapshot>();
        private readonly Dictionary<string,TaskSnapshot> taskSnapshots=new Dictionary<string,TaskSnapshot>();
        private readonly List<GuardianHumanEvent> events=new List<GuardianHumanEvent>();
        public bool Active { get; private set; }
        public bool Guided { get; private set; }
        public bool Live { get; private set; }
        public GuardianHumanStage Stage { get; private set; }
        public string SelectedActor="C";
        public int SelectedTable;
        public string LastResponse { get; private set; }
        public string PrimaryTaskId { get; private set; }
        public string ClearingTaskId { get; private set; }
        public string FinalTaskId { get; private set; }
        public IReadOnlyDictionary<string,GuardianHumanActor> Actors=>actors;
        public IReadOnlyList<GuardianHumanEvent> Events=>events;
        public void Initialize(GuardianSimulator simulator) { sim=simulator; }

        public void Begin(bool guided,bool five)
        {
            CaptureConfiguration();
            Active=false; Live=false; sim.ReserveFleet=five; sim.Settings.Seed=2026;
            sim.SetMode(GuardianSimulationMode.Physics);
            Active=true; Guided=guided; Stage=guided?GuardianHumanStage.InitialRun:GuardianHumanStage.Free;
            sim.AutomaticTasks=false; sim.Settings.RandomFaults=false; sim.Settings.MealSeconds=120;
            sim.Settings.MaximumDepartureDelay=.4f;
            events.Clear(); actors.Clear(); robotSnapshots.Clear(); taskSnapshots.Clear();
            sequence=0; dishCursor=0; PrimaryTaskId=ClearingTaskId=FinalTaskId=null;
            SelectedActor="C"; SelectedTable=0; LastResponse="色の変化と状況を確認し、指示ボタンで判断してください。";
            muting=true;
            foreach(var robot in sim.Robots)
            {
                actors[robot.RobotId]=new GuardianHumanActor{Id=robot.RobotId,Explanation="走行危険の模擬入力なし"};
                sim.SetCondition(robot.RobotId,RobotCondition.Normal);
            }
            muting=false;
            Add("Scenario","fleet",(guided?"固定体験":"自由操作")+"開始 / "+sim.Robots.Count+"台。実機の安全保証ではありません。");
            if(guided) PrimaryTaskId=sim.QueueTask(DeliveryKind.Soup,0,GuardianMenu.Soup,"C").Id;
        }
        public void Restart()
        {
            if(!Live) { Begin(Guided,sim.ReserveFleet); return; }
            EndWithoutReset(); sim.ResetScenario(); BeginLive();
        }
        public void EndWithoutReset()
        {
            Active=false; Live=false;
            foreach(var state in actors.Values) ReleaseHold(state);
            if(saved)
            {
                sim.AutomaticTasks=oldAuto; sim.Settings.RandomFaults=oldFaults; sim.ReserveFleet=oldFive;
                sim.Settings.MealSeconds=oldMeal; sim.Settings.Seed=oldSeed;
                sim.Settings.MaximumDepartureDelay=oldDeparture; Time.timeScale=oldScale; saved=false;
            }
            Stage=GuardianHumanStage.Inactive;
        }
        public void Exit() { EndWithoutReset(); sim.SetMode(GuardianSimulationMode.Physics); }
        public bool Permits(GuardianRobot robot)=>!Active || (actors.TryGetValue(robot.RobotId,out var state) && !state.MobilityUnsafe && !state.AwaitingDecision && !state.HoldStopped);
        public bool Permits(GuardianRobot robot,DeliveryKind kind)=>Permits(robot)
            && !(kind==DeliveryKind.Soup && Active && actors.TryGetValue(robot.RobotId,out var state) && state.LiquidsHeld);
        public void OnConditionEvidence(GuardianRobot robot)
        {
            if(!Active || muting) return;
            if(Live && sim.Settings.KeepServing && actors.TryGetValue(robot.RobotId,out var state) && KeepsWorking(robot,state)) return;
            OpenAlert(robot,"振動指標による状態変化");
        }

        /// <summary>
        /// Live, KeepServing: a lamp that turns yellow takes the liquids away and nothing else, so the robot is not stopped.
        /// Red does not stop it either: GuardianCapabilityPolicy leaves it the clearing of empty dishes, and that goes on.
        /// It keeps the work it can still do (food and clearing) while a soup it held goes back for another robot
        /// (RevokeIneligible). Back to green it keeps working too, with soup still off until a person confirms: the AI
        /// makes things stricter, a person loosens them. Red, unsafe motion, or a robot already waiting: the card as before.
        /// </summary>
        private bool KeepsWorking(GuardianRobot robot,GuardianHumanActor state)
        {
            if(state.MobilityUnsafe || state.AwaitingDecision || state.HoldStopped || robot.EmergencyStopped) return false;
            bool yellow=robot.Condition==RobotCondition.Warning, red=robot.Condition==RobotCondition.Limited;
            if(!yellow && !red && !(robot.Condition==RobotCondition.Normal && state.LiquidsHeld)) return false;
            state.Revision++; state.LiquidsHeld=true;
            DescribeAlert(robot,"振動指標による状態変化");
            state.Explanation=red?"赤色: 料理とスープは他機へ。下げ膳だけは止めずに続けます。"
                :yellow?"黄色: スープは他機へ。料理と下げ膳は止めずに続けます。"
                :"指標は緑。料理と下げ膳は続けます。スープは人が確かめて「作業受付を再開」を選ぶまで止めたままです。";
            state.DecisionFeedback=red?"自動：止めていません。下げ膳だけを続けます（止めるなら「停止を維持」）。"
                :yellow?"自動：止めていません。スープだけを他機へ回しました（止めるなら「停止を維持」）。"
                :"自動：止めていません。スープの再開は人の確認待ちです。";
            Add("Alert",robot.RobotId,(red?"振動指標で赤色 / ":yellow?"振動指標で黄色 / ":"振動指標で緑 / ")+state.Explanation);
            return true;
        }
        internal void OnInputUnavailable(GuardianRobot robot,string reason)
        {if(Active && !muting)OpenAlert(robot,"入力を確認できません: "+reason);}
        public void OnExternalEmergencyStop(GuardianRobot robot)
        {
            if(Active && !mutingStop) PrepareConditionEvidence(robot,robot.Condition);
            if(Active && !mutingStop) { actors[robot.RobotId].MobilityUnsafe=true; OpenAlert(robot,"外部からの停止要求"); }
        }
        private void SetStop(string id,bool stopped)
        {
            bool previous=mutingStop; mutingStop=true;
            try { sim.SetEmergencyStop(id,stopped); } finally { mutingStop=previous; }
        }
        public void RejectShortcut() { Result(false,SelectedActor,"体験モードでは指示ボタンで判断してください。安全停止をショートカットで解除しません。"); }
        public GuardianHumanResult InjectEvidence(string id,RobotCondition condition,bool unsafeMobility)
        {
            if((int)condition<0 || (int)condition>2) return Result(false,id,"未知の状態です。");
            if(!Active || !actors.TryGetValue(id,out var state)) return Result(false,id,"体験中のロボットを選択してください。");
            var robot=sim.Robots.Single(r=>r.RobotId==id);
            PrepareConditionEvidence(robot,robot.Condition);
            // SetCondition can revoke an unpicked assignment synchronously.
            // Preserve and hold it before that existing runtime behavior runs.
            if(robot.CurrentTask!=null)
            { state.HeldTask=robot.CurrentTask; state.HeldTask.OperatorHoldReason="Human evidence: "+id; }
            state.MobilityUnsafe|=unsafeMobility; // A color recovery never clears an unsafe-motion latch.
            muting=true; sim.SetCondition(id,condition); muting=false;
            OpenAlert(robot,"体験用の状態入力");
            return Result(true,id,"状態を更新し、安全停止とアラートを発行しました。");
        }
        private void OpenAlert(GuardianRobot robot,string source)
        {
            var state=actors[robot.RobotId]; state.Revision++; state.AwaitingDecision=true; state.HoldStopped=true;
            DescribeAlert(robot,source);
            if(robot.CurrentTask!=null)
            { state.HeldTask=robot.CurrentTask; state.HeldTask.OperatorHoldReason="Human decision: "+robot.RobotId+" / "+state.Revision; }
            state.Explanation=state.MobilityUnsafe?"走行危険の模擬信号。返答を待たず停止し、点検完了まで走行を拒否します。":
                robot.Condition==RobotCondition.Warning?"黄色: 液体の配膳は不可。状況を確認して担当変更を判断してください。":
                robot.Condition==RobotCondition.Limited?"赤色: 配膳不可。走行が安全な場合だけ下げ膳できます。":"指標は緑。停止解除と作業再開には人の判断が必要です。";
            SelectedActor=robot.RobotId;
            Add("Alert",robot.RobotId,source+" / "+state.Explanation);
            SetStop(robot.RobotId,true); sim.SetDispatchEnabled(robot,false);
            Add("Safety",robot.RobotId,"停止を要求し、新規割当を禁止。積載品と占有経路を保持します。");
        }
        private void ReleaseHold(GuardianHumanActor state,DeliveryTask target=null)
        {
            // Unalerted instructions can target CurrentTask without a HeldTask.
            if(target!=null) { target.OperatorHoldReason=null; target.RequestedRobotId=null; }
            if(state.HeldTask!=null)
            { state.HeldTask.OperatorHoldReason=null; state.HeldTask.RequestedRobotId=null; state.HeldTask=null; }
        }
        public GuardianHumanResult Submit(string id,GuardianHumanCommand command,int expectedRevision=-1)
        {
            if(!Active || !actors.TryGetValue(id,out var state)) return Result(false,id,"体験中のロボットを選択してください。");
            Add("Instruction",id,command.ToString());
            if(expectedRevision>=0 && expectedRevision!=state.Revision) return Result(false,id,"状況が更新されています。最新のアラートを確認してください。");
            var robot=sim.Robots.Single(r=>r.RobotId==id);
            if(robot.GetComponent<GuardianMotionInterlock>() is GuardianMotionInterlock local && local.Held && command!=GuardianHumanCommand.KeepStopped)
                return Result(false,id,"Local motion/perception/input hold is unresolved; safe acknowledgement required.");
            if(command==GuardianHumanCommand.DeploySpare)
            {
                if(!sim.ReserveFleet) return Result(false,id,"予備機の投入は5台モードで利用できます。");
                var operation=sim.Fleet.Deploy();
                return Result(!operation.Terminal,operation.RobotId,operation.Terminal?operation.Reason:"投入を受理。適格な作業が割り当たってから実際に出発します。");
            }
            if(command==GuardianHumanCommand.KeepStopped)
            {
                state.AwaitingDecision=false; state.HoldStopped=true;
                ReleaseHold(state,robot.CurrentTask);
                sim.SetDispatchEnabled(robot,false); SetStop(id,true);
                if(Guided && Stage==GuardianHumanStage.UnsafeDecision && !robot.Carrying) QueueFinalWork();
                return Result(true,id,robot.Carrying?"停止を維持。積載品は保持し、安全な受渡しが可能になるまで再割当を待ちます。":"停止を維持し、未積載の依頼を安全な他機へ渡します。");
            }
            if(command==GuardianHumanCommand.Inspect)
            {
                if(!robot.EmergencyStopped || robot.PhysicsDriver.Body.linearVelocity.sqrMagnitude>.025f || robot.PhysicsDriver.Body.angularVelocity.magnitude>.12f)
                    return Result(false,id,"停止が確認できてから、体験用の点検完了を確定してください。");
                state.MobilityUnsafe=false; state.AwaitingDecision=false; state.HoldStopped=false; state.LiquidsHeld=false; ReleaseHold(state);
                muting=true; sim.SetCondition(id,RobotCondition.Normal); muting=false;
                if(sim.ReserveFleet) sim.Fleet.RecoverToStandby(id);
                else
                { sim.SetDispatchEnabled(robot,false); SetStop(id,false); if(robot.CurrentTask==null) robot.PhysicsDriver.ReturnHome(); }
                return Result(true,id,"点検完了を模擬。積載品があれば実際の受渡地点へ返し、待機場所へ復帰します。実機の安全確認ではありません。");
            }
            if(state.MobilityUnsafe) return Result(false,id,"走行危険が未解消です。配膳・下げ膳・走行再開を拒否し、安全停止を維持します。");
            if(command==GuardianHumanCommand.ResumeWork)
            {
                if(state.AwaitingDecision || state.HoldStopped || robot.EmergencyStopped || robot.Condition!=RobotCondition.Normal)
                    return Result(false,id,"未確認の警告・停止・能力制限があります。停止して点検完了を模擬し、正常な待機状態を確認してください。");
                if(robot.FleetRole!=GuardianFleetRole.Active)
                    return Result(false,id,"5台モードの待機機は予備投入で明示的に出動させてください。");
                if(robot.DispatchEnabled && state.LiquidsHeld)
                { state.LiquidsHeld=false; return Result(true,id,"人が確かめたので、スープの配膳も再開します。"); }
                if(robot.DispatchEnabled) return Result(true,id,"この機体は既に作業を受け付けています。依頼を重複作成しません。");
                if(!robot.IsIdle) return Result(false,id,"荷物の返却と待機場所への帰還完了を待ってから再開してください。");
                state.LiquidsHeld=false; ReleaseHold(state); sim.SetDispatchEnabled(robot,true);
                return Result(true,id,"正常な待機状態を確認し、作業受付を再開しました。適格な既存依頼を再割当します。");
            }
            if(command==GuardianHumanCommand.Transfer)
            {
                if(state.HeldTask==null && robot.CurrentTask==null) return Result(false,id,"引継ぎ対象の作業がありません。");
                var handed=state.HeldTask??robot.CurrentTask;
                state.AwaitingDecision=false; state.HoldStopped=false; ReleaseHold(state,robot.CurrentTask); sim.SetDispatchEnabled(robot,false); SetStop(id,false);
                handed.HandedOverBy=id;  // another robot takes it; this one is not given it back
                if(robot.CurrentTask==null) robot.PhysicsDriver.ReturnHome();
                if(Guided && Stage==GuardianHumanStage.YellowDecision) Stage=GuardianHumanStage.WaitingTransfer;
                // Live, KeepServing: a yellow robot keeps the work it can still do (food, clearing) instead of standing at home.
                if(Live && sim.Settings.KeepServing && robot.Condition==RobotCondition.Warning)
                {
                    state.LiquidsHeld=true; sim.SetDispatchEnabled(robot,true);
                    return Result(true,id,"引継ぎを受理。この依頼は他機へ渡し、黄色のこの機体は料理と下げ膳を続けます。");
                }
                return Result(true,id,"引継ぎを受理。積載品は元の受渡地点へ返してから再割当し、この機体は待機場所へ戻ります。");
            }
            if(robot.FleetRole!=GuardianFleetRole.Active) return Result(false,id,"待機機は先に投入してください。復帰中の機体は物理的な到着を待ちます。");
            if(command==GuardianHumanCommand.ContinueDelivery)
            {
                var task=state.HeldTask??robot.CurrentTask;
                var kind=task==null?DeliveryKind.Food:task.Kind;
                if(!GuardianCapabilityPolicy.Allows(robot.Condition,kind) || robot.Condition==RobotCondition.Limited)
                    return Result(false,id,robot.Condition==RobotCondition.Warning?"黄色は液体を運べません。固形料理・下げ膳か他機への引継ぎを選んでください。":"赤色は配膳できません。安全走行できる場合だけ下げ膳を選べます。");
                if(task==null || task.Kind==DeliveryKind.EmptyDishes) return Result(false,id,"継続対象の配膳がありません。");
                state.AwaitingDecision=false; state.HoldStopped=false; ReleaseHold(state); task.RequestedRobotId=id; task.HandedOverBy=null;
                sim.SetDispatchEnabled(robot,true); SetStop(id,false);
                return Result(true,id,"能力に合う配膳の再開を許可。必要な物理受渡しを経て担当を再計画します。",task.Id);
            }
            if(command==GuardianHumanCommand.ClearDishes)
            {
                if(!GuardianCapabilityPolicy.Allows(robot.Condition,DeliveryKind.EmptyDishes)) return Result(false,id,"下げ膳の能力条件を満たしません。");
                var held=state.HeldTask;
                if(held!=null && held.Kind==DeliveryKind.EmptyDishes && held.Phase!=TaskPhase.Completed)
                {
                    state.AwaitingDecision=false; state.HoldStopped=false; ReleaseHold(state); held.RequestedRobotId=id; held.HandedOverBy=null;
                    sim.SetDispatchEnabled(robot,true); SetStop(id,false);
                    return Result(true,id,"保留中の同じ回収便を再開。積載品は必要な物理受渡しを経て扱います。",held.Id);
                }
                if(!sim.Inventory.AvailableDirty(SelectedTable).Any() || sim.Tasks.Any(t=>t.TableIndex==SelectedTable && t.Kind==DeliveryKind.EmptyDishes && t.Phase!=TaskPhase.Completed))
                    return Result(false,id,"この卓に未予約の空皿がないか、すでに回収予定があります。他の皿を奪いません。");
                var task=sim.QueueTask(DeliveryKind.EmptyDishes,SelectedTable,GuardianMenu.CurryRice,id);
                if(task==null) return Result(false,id,"受付不可: "+sim.Requests.Last().Decision);
                state.AwaitingDecision=false; state.HoldStopped=false; ReleaseHold(state); sim.SetDispatchEnabled(robot,true); SetStop(id,false);
                if(Guided && Stage==GuardianHumanStage.RedDecision) { ClearingTaskId=task.Id; Stage=GuardianHumanStage.WaitingClear; }
                return Result(true,id,"安全走行と下げ膳の能力を確認。選択機への回収依頼を受理しました。",task.Id);
            }
            return Result(false,id,"未対応の指示です。");
        }
        public GuardianHumanResult OrderMeal(bool soup)
        {
            if(!Active || Guided) return Result(false,"fleet","自由操作モードで注文してください。");
            var task=sim.QueueTask(soup?DeliveryKind.Soup:DeliveryKind.Food,SelectedTable,soup?GuardianMenu.Soup:GuardianMenu.CurryRice);
            return task==null?Result(false,"fleet",sim.Requests.Last().Decision):Result(true,"fleet","店舗の注文を受理。能力と経路条件に合う機体へ割り当てます。",task.Id);
        }
        private void QueueFinalWork()
        {
            if(FinalTaskId!=null) return;
            string spare=sim.Robots.FirstOrDefault(r=>(r.RobotId=="D" || r.RobotId=="E") && r.DispatchEnabled
                && r.Condition==RobotCondition.Normal && !r.EmergencyStopped && Permits(r))?.RobotId;
            var task=sim.QueueTask(DeliveryKind.Soup,2,GuardianMenu.Soup,spare);
            if(task!=null) { FinalTaskId=task.Id; Stage=GuardianHumanStage.WaitingFinal; Add("Response","fleet","停止機を動かさず、正常機へ独立した配膳を依頼しました。"); }
        }
        public void BeforePhysicsStep()
        {
            if(!Active) return;
            if(!Live) { sim.AutomaticTasks=false; sim.Settings.RandomFaults=false; }
            foreach(var state in actors.Values)
                if(state.MobilityUnsafe || state.AwaitingDecision || state.HoldStopped)
                {
                    var robot=sim.Robots.Single(r=>r.RobotId==state.Id);
                    if(!robot.EmergencyStopped) SetStop(state.Id,true);
                    if(robot.DispatchEnabled) sim.SetDispatchEnabled(robot,false);
                }
            for(int i=dishCursor;i<sim.Inventory.Events.Count;i++)
            {var e=sim.Inventory.Events[i];Add("Dish",e.ToKey,e.Id+" "+e.From+"/"+e.FromKey+" -> "+e.To+"/"+e.ToKey+" (physical ownership)");}
            dishCursor=sim.Inventory.Events.Count;
            foreach(var robot in sim.Robots)
            {
                var value=new RobotSnapshot{Condition=robot.Condition,Role=robot.FleetRole,Stopped=robot.EmergencyStopped,Task=robot.CurrentTask,Phase=robot.CurrentTask!=null?robot.CurrentTask.Phase:default,Wait=robot.PhysicsDriver.WaitReason};
                if(!robotSnapshots.TryGetValue(robot.RobotId,out var old)||old.Condition!=value.Condition||old.Role!=value.Role||old.Stopped!=value.Stopped||old.Task!=value.Task||old.Phase!=value.Phase||old.Wait!=value.Wait)
                {robotSnapshots[robot.RobotId]=value;Add("Robot",robot.RobotId,robot.Condition+" / "+robot.FleetRole+" / stop="+robot.EmergencyStopped+" / "+(robot.CurrentTask==null?"no task":robot.CurrentTask.Id+" "+robot.CurrentTask.Phase)+" / "+value.Wait);}
            }
            foreach(var task in sim.Tasks)
            {
                var value=new TaskSnapshot{Phase=task.Phase,Assigned=task.AssignedTo};
                if(!taskSnapshots.TryGetValue(task.Id,out var old)||old.Phase!=value.Phase||old.Assigned!=value.Assigned){taskSnapshots[task.Id]=value;Add("Task",task.Id,task.Phase+" / actor="+task.AssignedTo?.RobotId);}
            }
            if(!Guided) return;
            var primary=sim.Tasks.FirstOrDefault(t=>t.Id==PrimaryTaskId);
            var c=sim.Robots.Single(r=>r.RobotId=="C"); var a=sim.Robots.Single(r=>r.RobotId=="A");
            if(Stage==GuardianHumanStage.InitialRun && c.CurrentTask==primary && primary.Phase==TaskPhase.ToPickup
                && c.PhysicsDriver.Body.position.x< -3.4f && !c.PhysicsDriver.OwnsRoute && Vector3.Distance(c.PhysicsDriver.Body.position,primary.Pickup)<1.1f)
            { InjectEvidence("C",RobotCondition.Warning,false); Stage=GuardianHumanStage.YellowDecision; }
            if(Stage==GuardianHumanStage.WaitingTransfer && primary.Phase==TaskPhase.Completed && a.IsIdle)
            { SelectedTable=1; InjectEvidence("A",RobotCondition.Limited,false); Stage=GuardianHumanStage.RedDecision; }
            var clearing=sim.Tasks.FirstOrDefault(t=>t.Id==ClearingTaskId);
            if(Stage==GuardianHumanStage.WaitingClear && clearing!=null && clearing.Phase==TaskPhase.Completed && a.IsIdle)
            { InjectEvidence("A",RobotCondition.Limited,true); Stage=GuardianHumanStage.UnsafeDecision; }
            if(Stage==GuardianHumanStage.WaitingFinal && sim.Tasks.All(t=>t.Phase==TaskPhase.Completed) && sim.Robots.All(r=>r.IsIdle)
                && sim.Traffic.JobClaimCount==0 && sim.Traffic.Reservations.Count==0)
            { Stage=GuardianHumanStage.Complete; Add("Scenario","fleet","正常機の配膳・回収が完了。危険機は人の判断に従って停止を維持しています。"); }
        }
        private GuardianHumanResult ResultCore(bool accepted,string actor,string reason,string task=null)
        { LastResponse=(accepted?"受諾: ":"拒否: ")+reason; if(Active) Add(accepted?"Accepted":"Rejected",actor,reason); return new GuardianHumanResult{Accepted=accepted,Reason=reason,TaskId=task}; }
        private void Add(string kind,string actor,string message)
        {
            var e=new GuardianHumanEvent{Sequence=++sequence,Clock=sim.DemoClock,Kind=kind,Actor=actor,Message=message};
            events.Add(e); if(events.Count>512) events.RemoveAt(0);
            if(sim.Settings.VerboseRuntimeLogs)Debug.Log("[GuardianHuman] "+e.Clock.ToString("F2")+" "+kind+" "+actor+" "+message);
        }
    }
}
