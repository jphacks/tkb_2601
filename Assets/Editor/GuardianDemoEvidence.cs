using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    [InitializeOnLoad]
    public static class GuardianDemoEvidence
    {
        private const string Key="Guardian.Demo.ExplicitHeadless";
        private static double deadline;
        static GuardianDemoEvidence(){EditorApplication.update+=Tick;}
        public static void Headless()
        {
            if(!Application.isBatchMode || SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("This entry point requires -batchmode -nographics; it never opens a GUI.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("Isolated demo acceptance");var sim=root.AddComponent<GuardianSimulator>();sim.EnsureWorld();
            sim.AutomaticTasks=false;sim.Settings.RandomFaults=false;sim.Settings.VerboseRuntimeLogs=false;
            root.AddComponent<GuardianDemoVerification>();SessionState.SetBool(Key,true);deadline=EditorApplication.timeSinceStartup+180;EditorApplication.isPlaying=true;
        }
        private static void Tick()
        {
            if(!SessionState.GetBool(Key,false))return;
            var test=UnityEngine.Object.FindFirstObjectByType<GuardianDemoVerification>();
            if(test!=null && test.Done){string output=Array.IndexOf(Environment.GetCommandLineArgs(),"-guardianConversationOnly")>=0?"docs/evidence/rule-conversation":"docs/evidence/contextual-demo";Directory.CreateDirectory(output);File.WriteAllText(output+"/acceptance.json",JsonUtility.ToJson(test.ExportReport,true)+"\n");SessionState.SetBool(Key,false);EditorApplication.Exit(test.Passed?0:1);}
            else if(deadline>0 && EditorApplication.timeSinceStartup>deadline){SessionState.SetBool(Key,false);EditorApplication.Exit(124);}
        }
    }
}
