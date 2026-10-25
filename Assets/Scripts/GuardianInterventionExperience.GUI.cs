using System;
using System.Linq;
using UnityEngine;
namespace Guardian.UnitySim
{
    public sealed partial class GuardianInterventionExperience
    {
        private Vector2 scroll;
        private GUISkin humanSkin;
        private Font humanFont;
        public string Guidance
        {
            get
            {
                if(Live) return "ランプが変化した機体の状況を引き継ぎました。原因を確認し、その場で対応を選んでください。";
                switch(Stage)
                {
                    case GuardianHumanStage.InitialRun:return "Cがスープを受け取りに向かいます。ランプの変化を観察してください。";
                    case GuardianHumanStage.YellowDecision:return "黄のCは液体不可。継続の拒否理由を確認し、他機への引継ぎを選んでください。";
                    case GuardianHumanStage.WaitingTransfer:return "同じ料理を受渡地点で引き継ぎ、健康な機体が配膳しています。";
                    case GuardianHumanStage.RedDecision:return "赤のAは配膳不可ですが走行は安全。卓2の下げ膳を指示してください。";
                    case GuardianHumanStage.WaitingClear:return "Aが選んだ皿だけを回収し、カウンターへ返却しています。";
                    case GuardianHumanStage.UnsafeDecision:return "走行危険のAは下げ膳も不可。5台なら予備機を投入し、停止維持を選んでください。";
                    case GuardianHumanStage.WaitingFinal:return "Aの停止を維持し、健康な機体が配膳・回収を進めています。";
                    case GuardianHumanStage.Complete:return "体験完了。指示の受付と、実際の移動・受渡・完了の履歴を確認できます。";
                    default:return "注文と状態変化を入力し、状況を見ながら対応を選べます。";
                }
            }
        }
        public void DrawPanel(int width)
        {
            if(humanSkin==null)
            {
                humanSkin=Instantiate(GUI.skin); humanFont=GuardianFonts.Create(14);
                if(humanFont!=null) humanSkin.font=humanFont;
                humanSkin.label.wordWrap=true; humanSkin.button.wordWrap=true;
            }
            var previous=GUI.skin; GUI.skin=humanSkin; Action action=null;
            try
            {
                GUILayout.BeginArea(new Rect(Screen.width-width-10,10,width,Screen.height-20),GUI.skin.box);
                scroll=GUILayout.BeginScrollView(scroll);
                GUILayout.Label(Live?"PHYSICAL AI GUARDIAN / その場で判断":"PHYSICAL AI GUARDIAN / 人間介入体験",GUI.skin.box);
                GUILayout.Label("シミュレーション上の判断体験です。実機の安全保証ではありません。");
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("案内デモへリセット")) action=()=>Begin(true,sim.ReserveFleet);
                if(GUILayout.Button("自由デモへリセット")) action=()=>Begin(false,sim.ReserveFleet);
                if(GUILayout.Button("再開（リセット）")) action=Restart;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("3台にリセット")) action=()=>Begin(Guided,false);
                if(GUILayout.Button("5台にリセット")) action=()=>Begin(Guided,true);
                if(GUILayout.Button("終了して通常リセット")) action=Exit;
                GUILayout.EndHorizontal();
                GUILayout.Label(sim.DemoClock.ToString("F1")+"s / "+sim.Robots.Count+"台 / 判断待ち "+actors.Values.Count(s=>s.AwaitingDecision)+"台");
                sim.MotionControlsGUI();
                if(Live)
                {
                    sim.AutomaticTasks=GUILayout.Toggle(sim.AutomaticTasks,"自動注文を続ける");
                    sim.Settings.RandomFaults=GUILayout.Toggle(sim.Settings.RandomFaults,"状態変化のデモを続ける");
                }
                GUILayout.Label(Guidance,GUI.skin.box);
                GUILayout.BeginHorizontal();
                foreach(var candidate in sim.Robots)
                {
                    var old=GUI.backgroundColor;
                    GUI.backgroundColor=candidate.Condition==RobotCondition.Normal?new Color(.3f,.8f,.4f):candidate.Condition==RobotCondition.Warning?new Color(1,.8f,.2f):new Color(1,.35f,.3f);
                    if(GUILayout.Button(candidate.RobotId+" "+StateCaption(candidate.Condition)+(candidate.EmergencyStopped?" STOP":""))) SelectedActor=candidate.RobotId;
                    GUI.backgroundColor=old;
                }
                GUILayout.EndHorizontal();
                var robot=sim.Robots.Single(r=>r.RobotId==SelectedActor); var state=actors[SelectedActor];
                string selected=SelectedActor; int revision=state.Revision;
                GUILayout.Label("選択："+selected+" / "+StateCaption(robot.Condition)+" / "+robot.FleetRole,GUI.skin.box);
                GUILayout.Label("原因 #"+revision+" / "+state.AlertClock.ToString("F1")+"s\n"+(state.AlertCause??"まだアラートはありません。"));
                GUILayout.Label(state.MobilityUnsafe?"走行危険：停止維持。緑に戻っても自動解除しません。":
                    "走行危険信号なし / "+(state.AwaitingDecision?"人の返答待ち":"指示を反映済み"));
                GUILayout.Label(Choices(selected),GUI.skin.box);
                var task=robot.CurrentTask??state.HeldTask;
                GUILayout.Label(task==null?"担当作業なし":task.Id+" / "+task.Kind+" / "+task.Phase+" / 皿ID "+string.Join(",",task.DishIds));
                GUILayout.Label(state.DecisionFeedback??"まだ指示はありません。",GUI.skin.box);
                GUILayout.Label(RobotResponse(selected),GUI.skin.box);
                GUILayout.Label("対応を選ぶ（受付後も移動・受渡の完了を確認してください）");
                foreach(GuardianHumanCommand command in Enum.GetValues(typeof(GuardianHumanCommand)))
                {
                    var choice=command;
                    if(GUILayout.Button(CommandCaption(choice))) action=()=>Submit(selected,choice,revision);
                }
                if(!Guided)
                {
                    GUILayout.Label("体験用の状態入力");
                    GUILayout.BeginHorizontal();
                    if(GUILayout.Button("黄・走行可")) action=()=>InjectEvidence(selected,RobotCondition.Warning,false);
                    if(GUILayout.Button("赤・走行可")) action=()=>InjectEvidence(selected,RobotCondition.Limited,false);
                    if(GUILayout.Button("走行危険")) action=()=>InjectEvidence(selected,RobotCondition.Limited,true);
                    if(GUILayout.Button("指標を緑へ")) action=()=>InjectEvidence(selected,RobotCondition.Normal,false);
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    for(int table=0;table<3;table++) if(GUILayout.Button((SelectedTable==table?"* ":"")+"卓 "+(table+1))) SelectedTable=table;
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if(GUILayout.Button("スープを注文")) action=()=>OrderMeal(true);
                    if(GUILayout.Button("固形料理を注文")) action=()=>OrderMeal(false);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Label("履歴（アラート → 人の指示 → ロボット応答 → 実際の受渡）");
                foreach(var e in events.AsEnumerable().Reverse().Take(24)) GUILayout.Label(e.Clock.ToString("F1")+"s "+e.Kind+" "+e.Actor+" / "+e.Message);
                GUILayout.EndScrollView(); GUILayout.EndArea();
            }
            finally { GUI.skin=previous; }
            action?.Invoke();
        }
        private void OnDestroy()
        { if(humanSkin!=null) Destroy(humanSkin); GuardianFonts.Release(humanFont); }
    }
}