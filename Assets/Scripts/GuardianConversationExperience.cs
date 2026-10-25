using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Rule-conversation front end. Audio requires an explicit, separate opt-in OS helper.</summary>
    public sealed class GuardianConversationExperience : MonoBehaviour
    {
        private GuardianSimulator sim;
        private string alertSignature="";
        private double nextPoll;
        public GuardianRuleConversation Session {get;private set;}
        public IGuardianSpeechRecognizer Recognizer {get;set;}
        public IGuardianSpeechOutput Output {get;set;}
        public void Initialize(GuardianSimulator simulator)
        {
            sim=simulator;Session=new GuardianRuleConversation(sim);
            string[] args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-guardianSpeechInbox");
            string root=at>=0 && at+1<args.Length?args[at+1]:Path.Combine(Application.dataPath,"../Recordings/voice-inbox");
            var bridge=new GuardianLocalSpeechBridge(root);Recognizer=bridge;Output=bridge;
        }
        private void Publish(string text)
        {sim.DemoInterface.ConversationNotice=text;try{Output?.Say(text);}catch(IOException){sim.DemoInterface.ConversationNotice="応答ファイルへ書き込めません。操作結果は画面とログで確認してください。";}catch(UnauthorizedAccessException){sim.DemoInterface.ConversationNotice="応答先への書き込み権限がありません。";}Debug.Log("[GuardianConversation] "+text);}
        public void Poll()
        {
            if(sim==null)return;
            string signature=string.Join(";",sim.Robots.Select(r=>r.RobotId+"/"+r.Condition+"/"+r.EmergencyStopped+"/"+r.PhysicsDriver.SafetyHeld+"/"+
                (sim.HumanExperienceActive && sim.HumanExperience.Actors.TryGetValue(r.RobotId,out var a)?a.Revision:0)));
            if(signature!=alertSignature){alertSignature=signature;if(!Session.HasPending)Publish(Session.DescribeAlerts());}
            for(int i=0;i<4 && Recognizer!=null && Recognizer.TryRead(out var message);i++)Publish(Session.Process(message,Time.realtimeSinceStartupAsDouble));
            sim.DemoInterface.TrackOperation();
        }
        private void Update()
        {double now=Time.realtimeSinceStartupAsDouble;if(now<nextPoll)return;nextPoll=now+.2;Poll();}
    }
}
