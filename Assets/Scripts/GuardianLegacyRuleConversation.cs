using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuardianConversationKind
    {
        Invalid,Help,FleetStatus,RobotStatus,PrepareStop,PrepareRecover,Confirm,Cancel
    }

    public struct GuardianConversationIntent
    {
        public GuardianConversationKind Kind;
        public string RobotId;
        public string Error;
    }

    /// <summary>Deterministic Japanese command grammar. It performs no speech or network processing.</summary>
    public static class GuardianConversationParser
    {
        public const int MaximumInputLength=120;

        public static GuardianConversationIntent Parse(string input,IEnumerable<string> robotIds)
        {
            if (string.IsNullOrWhiteSpace(input)) return Invalid("入力が空です。");
            if (input.Length>MaximumInputLength) return Invalid("入力が長すぎます。");
            if (input.Any(char.IsControl)) return Invalid("改行や制御文字は使用できません。");
            string text=input.Normalize(NormalizationForm.FormKC).ToUpperInvariant()
                .Replace(" ","").Replace("　","").Trim('。','！','!','?','？');
            if (text=="ヘルプ" || text=="使い方" || text=="HELP") return Of(GuardianConversationKind.Help);
            if (text=="状況" || text=="状態" || text=="STATUS") return Of(GuardianConversationKind.FleetStatus);
            if (text=="実行" || text=="はい") return Of(GuardianConversationKind.Confirm);
            if (text=="キャンセル" || text=="やめる" || text=="いいえ") return Of(GuardianConversationKind.Cancel);

            text=text.Replace("ロボット","").Replace("ROBOT","");
            MatchCollection matches=Regex.Matches(text,"[A-Z]");
            if (matches.Count!=1) return Invalid(matches.Count==0?"ロボットを1台指定してください。":"ロボットは1台だけ指定してください。");
            string id=matches[0].Value;
            var valid=new HashSet<string>(robotIds??Array.Empty<string>(),StringComparer.Ordinal);
            if (!valid.Contains(id)) return Invalid("指定したロボットは現在の編成にいません。");
            string action=text.Remove(matches[0].Index,matches[0].Length).Trim('を','の','に','は',':','：');
            switch (action)
            {
                case "状況": case "状態": return Of(GuardianConversationKind.RobotStatus,id);
                case "停止": case "止めて": case "緊急停止": return Of(GuardianConversationKind.PrepareStop,id);
                case "復帰": case "再開": case "解除": case "停止解除": return Of(GuardianConversationKind.PrepareRecover,id);
                default: return Invalid("対応していない指示です。状況・停止・復帰から選んでください。");
            }
        }

        private static GuardianConversationIntent Of(GuardianConversationKind kind,string id=null)=>
            new GuardianConversationIntent{Kind=kind,RobotId=id};
        private static GuardianConversationIntent Invalid(string error)=>
            new GuardianConversationIntent{Kind=GuardianConversationKind.Invalid,Error=error};
    }

    public sealed class GuardianLegacyRuleConversation
    {
        private sealed class Pending
        {
            public string RobotId;
            public bool Stop,ExpectedEmergency,ExpectedOperator;
            public bool ExpectedPedestrianHold;
            public RobotCondition ExpectedCondition;
            public int ScenarioRevision;
            public double CreatedAt;
        }

        private readonly GuardianSimulator sim;
        private Pending pending;
        public bool HasPending=>pending!=null;
        public int ExecutedCommands { get; private set; }

        public GuardianLegacyRuleConversation(GuardianSimulator simulator)
        {
            sim=simulator??throw new ArgumentNullException(nameof(simulator));
        }

        public string Submit(string input)
        {
            GuardianConversationIntent intent=GuardianConversationParser.Parse(input,sim.Robots.Select(r=>r.RobotId));
            if (intent.Kind!=GuardianConversationKind.Confirm && intent.Kind!=GuardianConversationKind.Cancel) pending=null;
            switch (intent.Kind)
            {
                case GuardianConversationKind.Invalid: return intent.Error+" 操作は実行していません。";
                case GuardianConversationKind.Help: return "例：『状況』『A 状況』『A 停止』『A 復帰』。変更操作は確認後に『実行』と入力します。";
                case GuardianConversationKind.FleetStatus: return DescribeFleet();
                case GuardianConversationKind.RobotStatus: return DescribeRobot(Find(intent.RobotId));
                case GuardianConversationKind.PrepareStop: return Prepare(intent.RobotId,true);
                case GuardianConversationKind.PrepareRecover: return Prepare(intent.RobotId,false);
                case GuardianConversationKind.Confirm: return Confirm();
                case GuardianConversationKind.Cancel: pending=null; return "操作を取り消しました。ロボットの状態は変更していません。";
                default: return "指示を解釈できません。操作は実行していません。";
            }
        }

        public string DescribeFleet()
        {
            if (sim.Mode!=GuardianSimulationMode.Physics) return "日本語操作パネルはPhysicsモード専用です。";
            return string.Join("\n",sim.Robots.Select(DescribeRobot));
        }

        private string Prepare(string id,bool stop)
        {
            GuardianRobot robot=Find(id);
            if (robot==null) return "対象ロボットが見つかりません。操作は実行していません。";
            if (!stop && robot.LinkedStopped)
                return "センサー由来の保持中です。中継の KEY2 または通信復旧の確認を先に行ってください。";
            if (robot.OperatorStopped==stop)
                return stop?"ロボット "+id+" はすでに緊急停止中です。":"ロボット "+id+" は緊急停止していません。";
            if (!stop && robot.PhysicsDriver.PedestrianSafetyHold)
                return "ロボット "+id+" は歩行者安全停止中です。安全保持中は復帰操作を受け付けません。";
            pending=new Pending{RobotId=id,Stop=stop,ExpectedEmergency=robot.EmergencyStopped,
                ExpectedOperator=robot.OperatorStopped,
                ExpectedPedestrianHold=robot.PhysicsDriver.PedestrianSafetyHold,ExpectedCondition=robot.Condition,
                ScenarioRevision=sim.ScenarioRevision,CreatedAt=Time.realtimeSinceStartupAsDouble};
            return "確認：ロボット "+id+(stop?" を緊急停止します。":" の緊急停止を解除します。")+
                "実行する場合は『実行』、取り消す場合は『キャンセル』と入力してください。";
        }

        private string Confirm()
        {
            Pending command=pending; pending=null;
            if (command==null) return "確認待ちの操作はありません。";
            double now=Time.realtimeSinceStartupAsDouble;
            if (double.IsNaN(now) || double.IsInfinity(now) || now<command.CreatedAt || now-command.CreatedAt>15
                || command.ScenarioRevision!=sim.ScenarioRevision)
                return "確認の有効期限が切れたか、シナリオが更新されました。操作は実行していません。";
            GuardianRobot robot=Find(command.RobotId);
            if (robot==null || robot.EmergencyStopped!=command.ExpectedEmergency || robot.OperatorStopped!=command.ExpectedOperator || robot.Condition!=command.ExpectedCondition
                || robot.PhysicsDriver.PedestrianSafetyHold!=command.ExpectedPedestrianHold)
                return "確認後に対象の状態が変化しました。操作は実行していません。";
            if (!command.Stop && robot.PhysicsDriver.PedestrianSafetyHold)
                return "歩行者安全停止が継続中です。復帰操作は実行していません。";
            if (!command.Stop && robot.LinkedStopped)
                return "センサー由来の保持が継続中です。復帰操作は実行していません。";
            sim.SetEmergencyStop(command.RobotId,command.Stop); ExecutedCommands++;
            return "実行しました。"+DescribeRobot(robot);
        }

        private GuardianRobot Find(string id)=>sim.Robots.FirstOrDefault(r=>r.RobotId==id);

        private static string DescribeRobot(GuardianRobot robot)
        {
            if (robot==null) return "対象ロボットが見つかりません。";
            string state=robot.Condition==RobotCondition.Normal?"正常":robot.Condition==RobotCondition.Warning?"注意":"能力制限";
            string mobility=robot.EmergencyStopped?"緊急停止中":robot.PhysicsDriver.PedestrianSafetyHold?"歩行者安全停止中":"走行可能";
            string work=robot.CurrentTask==null?"担当なし":robot.CurrentTask.Id+" "+Kind(robot.CurrentTask.Kind)+" "+Phase(robot.CurrentTask.Phase);
            string allowed=robot.EmergencyStopped?"作業不可":robot.Condition==RobotCondition.Normal?"スープ・料理・皿回収":
                robot.Condition==RobotCondition.Warning?"料理・皿回収":"皿回収のみ";
            return "ロボット "+robot.RobotId+"："+state+"、"+mobility+"、担当 "+work+"、可能作業 "+allowed+"。";
        }

        private static string Kind(DeliveryKind kind)=>kind==DeliveryKind.Soup?"スープ":kind==DeliveryKind.Food?"料理":"皿回収";
        private static string Phase(TaskPhase phase)=>phase==TaskPhase.Pending?"待機":phase==TaskPhase.ToPickup?"受取へ移動":
            phase==TaskPhase.ToDropoff?"目的地へ移動":phase==TaskPhase.ReturningCargo?"返却中":"完了";
    }
}
