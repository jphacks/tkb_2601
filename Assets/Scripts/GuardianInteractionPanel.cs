using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Opt-in Japanese text panel. No microphone, audio file or network service is used.</summary>
    public sealed class GuardianInteractionPanel : MonoBehaviour
    {
        private readonly List<string> history=new List<string>();
        private GuardianSimulator sim;
        private GuardianLegacyRuleConversation conversation;
        private GUISkin skin;
        private Font font;
        private string input="状況",selected="A";
        private Vector2 scroll;

        public GuardianLegacyRuleConversation Conversation=>conversation;
        public IReadOnlyList<string> History=>history;

        public void Initialize(GuardianSimulator simulator)
        {
            sim=simulator??throw new ArgumentNullException(nameof(simulator));
            conversation=new GuardianLegacyRuleConversation(sim);
            Record("案内","ルールベースの文字操作です。音声認識・外部API・ネットワーク通信は使用しません。");
        }

        private void OnGUI()
        {
            if (!Application.isPlaying || sim==null || sim.Mode!=GuardianSimulationMode.Physics) return;
            EnsureSkin();
            var previous=GUI.skin; GUI.skin=skin;
            try
            {
                int width=Mathf.Clamp(Mathf.RoundToInt(Screen.width*.36f),420,620);
                int height=Mathf.Clamp(Mathf.RoundToInt(Screen.height*.42f),300,430);
                GUILayout.BeginArea(new Rect(10,Screen.height-height-10,width,height),GUI.skin.box);
                GUILayout.Label("日本語操作・会話デモ",GUI.skin.box);
                GUILayout.Label("公開ルールベース / 音声なし / 外部通信なし。停止・復帰は確認後に実行します。");
                GUILayout.BeginHorizontal();
                foreach (GuardianRobot robot in sim.Robots)
                    if (GUILayout.Button((selected==robot.RobotId?"● ":"")+robot.RobotId)) selected=robot.RobotId;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("全体状況")) Submit("状況");
                if (GUILayout.Button(selected+" 状況")) Submit(selected+" 状況");
                if (GUILayout.Button(selected+" 停止")) Submit(selected+" 停止");
                if (GUILayout.Button(selected+" 復帰")) Submit(selected+" 復帰");
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                input=GUILayout.TextField(input,GuardianConversationParser.MaximumInputLength);
                bool enter=Event.current.type==EventType.KeyDown && Event.current.keyCode==KeyCode.Return;
                if (GUILayout.Button("送信",GUILayout.Width(70)) || enter) { Submit(input); input=""; }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("実行")) Submit("実行");
                if (GUILayout.Button("キャンセル")) Submit("キャンセル");
                if (GUILayout.Button("使い方")) Submit("ヘルプ");
                GUILayout.EndHorizontal();
                scroll=GUILayout.BeginScrollView(scroll);
                foreach (string line in history.AsEnumerable().Reverse().Take(10)) GUILayout.Label(line);
                GUILayout.EndScrollView();
                GUILayout.EndArea();
            }
            finally { GUI.skin=previous; }
        }

        private void Submit(string text)
        {
            string reply=conversation.Submit(text);
            Record("入力",text); Record("応答",reply);
            Debug.Log("[GuardianInteraction] "+reply);
        }

        private void Record(string kind,string text)
        {
            history.Add(kind+"："+text);
            if (history.Count>24) history.RemoveAt(0);
            scroll.y=float.MaxValue;
        }

        private void EnsureSkin()
        {
            if (skin!=null) return;
            skin=Instantiate(GUI.skin); font=GuardianFonts.Create(14);
            if (font!=null) skin.font=font;
            skin.label.wordWrap=true; skin.button.wordWrap=true; skin.textField.fontSize=14;
        }

        private void OnDestroy()
        {
            if (skin!=null) Destroy(skin);
            GuardianFonts.Release(font);
        }
    }
}
