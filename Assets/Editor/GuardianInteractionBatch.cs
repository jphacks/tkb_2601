using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    public static class GuardianInteractionBatch
    {
        private const string ExpectedFontSha256="dff723ba59d57d136764a04b9b2d03205544f7cd785a711442d6d2d085ac5073";

        public static void Verify()
        {
            GuardianBatchRunner.Execute("interaction", RunChecks);
        }

        public static void RunChecks()
        {
            string[] robots={"A","B","C"};
            Check(GuardianConversationParser.Parse(" ロボットａを停止。",robots),GuardianConversationKind.PrepareStop,"A");
            Check(GuardianConversationParser.Parse("B 停止解除",robots),GuardianConversationKind.PrepareRecover,"B");
            Check(GuardianConversationParser.Parse("状況",robots),GuardianConversationKind.FleetStatus,null);
            Check(GuardianConversationParser.Parse("実行",robots),GuardianConversationKind.Confirm,null);
            Reject(GuardianConversationParser.Parse("AとBを停止",robots),"multiple targets");
            Reject(GuardianConversationParser.Parse("D 状況",robots),"unknown target");
            Reject(GuardianConversationParser.Parse("A\n停止",robots),"control character");
            Reject(GuardianConversationParser.Parse(new string('あ',GuardianConversationParser.MaximumInputLength+1),robots),"oversized input");

            string root=Directory.GetParent(Application.dataPath).FullName;
            string fontPath=Path.Combine(root,"Assets/Art/Fonts/Resources/NotoSansJP-Regular.otf");
            string licensePath=Path.Combine(root,"Assets/Art/Fonts/Resources/NotoSansJP-OFL.txt");
            if (!File.Exists(fontPath) || new FileInfo(fontPath).Length!=4533028) throw new InvalidDataException("Bundled font missing or unexpected size");
            string license=File.ReadAllText(licensePath);
            if (!license.Contains("SIL OPEN FONT LICENSE Version 1.1")) throw new InvalidDataException("OFL-1.1 notice missing");
            using (SHA256 sha=SHA256.Create())
            {
                string actual=string.Concat(sha.ComputeHash(File.ReadAllBytes(fontPath)).Select(value=>value.ToString("x2")));
                if (actual!=ExpectedFontSha256) throw new InvalidDataException("Bundled font hash changed");
            }
            Debug.Log("Guardian interaction batch verification passed");
        }

        private static void Check(GuardianConversationIntent intent,GuardianConversationKind kind,string robot)
        {
            if (intent.Kind!=kind || intent.RobotId!=robot) throw new InvalidOperationException("Unexpected conversation parse result");
        }

        private static void Reject(GuardianConversationIntent intent,string label)
        {
            if (intent.Kind!=GuardianConversationKind.Invalid || string.IsNullOrWhiteSpace(intent.Error))
                throw new InvalidOperationException("Conversation input was not rejected: "+label);
        }
    }
}
