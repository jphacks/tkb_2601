using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianInterventionExperience
    {
        private void CaptureConfiguration()
        {
            if(saved) return;
            saved=true; oldAuto=sim.AutomaticTasks; oldFaults=sim.Settings.RandomFaults;
            oldFive=sim.ReserveFleet; oldMeal=sim.Settings.MealSeconds; oldSeed=sim.Settings.Seed;
            oldDeparture=sim.Settings.MaximumDepartureDelay; oldScale=Time.timeScale;
        }
        public void BeginLive()
        {
            if(Active) return;
            CaptureConfiguration(); Active=true; Live=true; Guided=false; Stage=GuardianHumanStage.Free;
            events.Clear(); actors.Clear(); robotSnapshots.Clear(); taskSnapshots.Clear();
            sequence=0; dishCursor=sim.Inventory.Events.Count; PrimaryTaskId=ClearingTaskId=FinalTaskId=null;
            foreach(var robot in sim.Robots)
                actors[robot.RobotId]=new GuardianHumanActor{Id=robot.RobotId,PreviousCondition=robot.Condition,
                    AlertCondition=robot.Condition,Explanation="状態変化を待っています。",AlertCause="まだアラートはありません。"};
            SelectedActor=sim.Robots.First().RobotId;
            Add("Scenario","fleet","稼働中の判断画面を開始。時計・注文・料理・位置は引き継ぎます。");
        }
        public void PrepareConditionEvidence(GuardianRobot robot,RobotCondition previous)
        { if(Active && actors.TryGetValue(robot.RobotId,out var actor)) actor.PreviousCondition=previous; }
        private void DescribeAlert(GuardianRobot robot,string source)
        {
            var actor=actors[robot.RobotId]; actor.AlertCondition=robot.Condition;
            actor.AlertClock=sim.DemoClock; actor.AlertVibration=robot.Vibration;
            actor.AlertCause=source+" / "+StateCaption(actor.PreviousCondition)+" → "+StateCaption(robot.Condition)
                +" / 振動指標 "+robot.Vibration.ToString("F2")+" (黄 "+sim.Settings.WarningThreshold.ToString("F2")
                +", 赤 "+sim.Settings.LimitedThreshold.ToString("F2")+")";
            actor.DecisionFeedback="未回答：対象機を停止し、料理と皿の担当を保留しています。";
        }
        public static string StateCaption(RobotCondition condition)=>condition==RobotCondition.Normal?"正常・緑":
            condition==RobotCondition.Warning?"注意・黄":"能力制限・赤";
        public string Choices(string id)
        {
            var robot=sim.Robots.Single(r=>r.RobotId==id); var actor=actors[id];
            if(actor.MobilityUnsafe) return "走行危険：配膳・下げ膳・走行再開は不可。停止維持、健康な予備機の投入、停止確認後の模擬点検を選べます。";
            if(robot.Condition==RobotCondition.Warning) return "液体は運べません。固形料理の継続、安全な下げ膳、他機への引継ぎ、停止維持を検討できます。";
            if(robot.Condition==RobotCondition.Limited) return "料理の配膳はできません。走行が安全な場合だけ下げ膳、他機への引継ぎ、停止維持を検討できます。";
            if(actor.LiquidsHeld && !robot.EmergencyStopped) return "料理と下げ膳は続けています。スープは止めたままです。確かめたら「点検・帰還後に作業受付を再開」でスープも戻します。";
            return "配膳・下げ膳が可能です。保留中の作業を継続するか、他機へ引き継ぐかを判断してください。点検後は受付再開を選べます。";
        }
        public string RobotResponse(string id)
        {
            var robot=sim.Robots.Single(r=>r.RobotId==id); var actor=actors[id]; var task=robot.CurrentTask;
            if(robot.EmergencyStopped) return "ロボット応答：停止中。"+(robot.Carrying?"荷物はこの機体が保持。":"皿は受渡元に保持。")
                +(actor.AwaitingDecision?"人の返答を待っています。":"再開指示を待っています。");
            if(task!=null && task.Phase==TaskPhase.ReturningCargo) return "ロボット応答：同じ荷物を元の受渡地点へ返しています。返却後に担当変更します。";
            if(task!=null && task.AwaitingServicePickup) return "ロボット応答：卓で回収カウンターの空きを待機。まだ皿を持ち上げていません。";
            if(robot.Carrying) return "ロボット応答："+task.Id+" の荷物を搬送中。完了は実際の受渡後です。";
            if(task!=null) return "ロボット応答："+task.Id+" の受取地点へ移動中。";
            return robot.IsIdle?"ロボット応答：待機位置に到着。"+(robot.DispatchEnabled?"作業受付中。":"新しい作業の受付は停止中。"):
                "ロボット応答：待機位置へ帰還中。";
        }
        private GuardianHumanResult Result(bool accepted,string actor,string reason,string task=null)
        {
            var result=ResultCore(accepted,actor,reason,task);
            if(actors.TryGetValue(actor,out var state)) state.DecisionFeedback=(accepted?"指示を受付：":"指示を拒否：")+reason;
            return result;
        }
        public static string CommandCaption(GuardianHumanCommand command)
        {
            switch(command)
            {
                case GuardianHumanCommand.ContinueDelivery:return "配膳を継続";
                case GuardianHumanCommand.ClearDishes:return "下げ膳を指示 / 保留回収を再開";
                case GuardianHumanCommand.Transfer:return "同じ依頼を他機へ引継ぎ";
                case GuardianHumanCommand.KeepStopped:return "停止を維持・健康機へ";
                case GuardianHumanCommand.DeploySpare:return "健康な予備機を投入（5台）";
                case GuardianHumanCommand.Inspect:return "停止確認後の点検完了（模擬）";
                default:return "点検・帰還後に作業受付を再開";
            }
        }
    }
}
