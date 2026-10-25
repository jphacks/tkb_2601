using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Guardian.UnitySim
{
    public enum GuardianInteractionMode { Click, RuleVoice }
    public struct GuardianSpeechMessage {public string Id,Text;public float Confidence;public double ReceivedAt;}
    public interface IGuardianSpeechRecognizer {bool TryRead(out GuardianSpeechMessage message);}
    public interface IGuardianSpeechOutput {void Say(string text);}

    /// <summary>Deterministic Japanese grammar. Every action goes through the click session's safety/version checks.</summary>
    public sealed class GuardianRuleConversation
    {
        private readonly GuardianSimulator sim;
        private readonly GuardianDemoInterface operations;
        private readonly HashSet<string> seen=new HashSet<string>();
        private readonly Queue<string> order=new Queue<string>();
        private sealed class Pending {public string Id;public int Selection,State;public GuardianDemoChoice Choice;}
        private Pending pending;
        public bool HasPending=>pending!=null;
        public string Reply {get;private set;}="";
        public GuardianRuleConversation(GuardianSimulator simulator){sim=simulator;operations=sim.DemoInterface;}
        public string DescribeAlerts()
        {
            var alerts=sim.Robots.Where(r=>GuardianDemoInterface.HasAlert(sim,r)).ToArray();
            if(alerts.Length==0)return "現在、対応が必要なロボットはいません。";
            return string.Join(" ",alerts.Select(r=>
            {
                bool unsafeMotion=r.PhysicsDriver.SafetyHeld || (sim.HumanExperienceActive && sim.HumanExperience.Actors.TryGetValue(r.RobotId,out var a) && a.MobilityUnsafe);
                string allowed=unsafeMotion?"安全確認が済むまで停止を続けます。":
                    "能力上は"+string.Join("・",new[]{DeliveryKind.Soup,DeliveryKind.Food,DeliveryKind.EmptyDishes}.Where(k=>GuardianCapabilityPolicy.Allows(r.Condition,k)).Select(k=>k==DeliveryKind.Soup?"スープ配膳":k==DeliveryKind.Food?"料理配膳":"皿回収"))+"が可能です。";
                return "ロボット "+r.RobotId+" は"+(r.EmergencyStopped?"停止中。":r.Condition==RobotCondition.Warning?"注意状態。":"能力制限があります。")+allowed;
            }))+" 対象と、続ける・引き継ぐ・停止のどれかを指定してください。";
        }
        private string Answer(string text){Reply=text;return text;}
        private string Clarify(string text){pending=null;return Answer(text+" 実行していません。");}
        public string Process(GuardianSpeechMessage message,double now)
        {
            sim.RefreshDemoEvidence();
            if(string.IsNullOrWhiteSpace(message.Id) || !seen.Add(message.Id))return Clarify("同じ入力または識別できない入力です。");
            order.Enqueue(message.Id);if(order.Count>128)seen.Remove(order.Dequeue());
            if(!double.IsFinite(now) || !double.IsFinite(message.ReceivedAt) || now<message.ReceivedAt || now-message.ReceivedAt>10
                || !float.IsFinite(message.Confidence) || message.Confidence<.75f || message.Confidence>1 || string.IsNullOrWhiteSpace(message.Text) || message.Text.Length>200)return Clarify("聞き取りを確定できません。対象と操作をもう一度指定してください。");
            string text=message.Text.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Replace(" ","").Replace("　","").Trim('。','！','!','?','？');
            if(text=="キャンセル" || text=="やめる"){pending=null;return Answer("操作を取り消しました。停止状態は変えません。");}
            if(text=="状況" || text=="どうしたらいい" || text=="状態")return Answer(DescribeAlerts());
            if(text=="実行" || text=="はい")
            {
                var command=pending;pending=null;
                if(command==null)return Answer("確認待ちの操作がありません。対象と操作を指定してください。");
                var result=operations.Choose(command.Id,command.Selection,command.State,command.Choice);
                return Answer((result.Accepted?"受理しました。":"拒否しました。")+"ロボット "+command.Id+"。"+result.Reason);
            }
            pending=null;
            text=text.Replace("ロボット","").Replace("ROBOT","").Replace("エー","A").Replace("ビー","B").Replace("シー","C").Replace("ディー","D").Replace("イー","E");
            var ids=Regex.Matches(text,"[A-Z]");string id=null;
            if(ids.Count>1)return Clarify("対象が複数または曖昧です。ロボットを1台指定してください。");
            if(ids.Count==1){id=ids[0].Value;if(!sim.Robots.Any(r=>r.RobotId==id))return Clarify("そのロボットは見つかりません。");text=text.Remove(ids[0].Index,1).TrimStart('を','の','に','は',':','：');}
            else
            {
                var alerts=sim.Robots.Where(r=>GuardianDemoInterface.HasAlert(sim,r)).ToArray();
                if(alerts.Length!=1)return Clarify("対象を1台指定してください。");id=alerts[0].RobotId;
            }
            GuardianDemoChoice choice;
            switch(text)
            {
                case "続けて":case "続ける":case "続行":case "できる仕事を続けて":case "仕事を続けて":choice=GuardianDemoChoice.ContinueCapable;break;
                case "引き継いで":case "引き継ぐ":case "引継ぎ":case "別のロボットへ引き継いで":choice=GuardianDemoChoice.Transfer;break;
                case "止めて":case "停止":case "停止を続けて":case "停止を続ける":choice=GuardianDemoChoice.KeepStopped;break;
                default:return Clarify("その指示には対応していません。対象と、続ける・引き継ぐ・停止を指定してください。");
            }
            operations.SelectForCommand(id);operations.RefreshState();
            if(!operations.NeedsDecision)return Clarify("その機体には現在、対応が必要な異常がありません。");
            pending=new Pending{Id=id,Selection=operations.SelectionVersion,State=operations.StateVersion,Choice=choice};
            return Answer("確認：ロボット "+id+" に「"+(choice==GuardianDemoChoice.ContinueCapable?"できる仕事を続ける":choice==GuardianDemoChoice.Transfer?"引き継ぐ":"停止を続ける")+"」。実行する場合は「実行」、取り消す場合は「キャンセル」と言ってください。");
        }
    }
}
