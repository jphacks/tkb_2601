using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace Guardian.UnitySim.Editor
{
    [InitializeOnLoad]public static class GuardianEnduranceEvidence
    {
        private const string Key="Guardian.Endurance";private static double nextWrite;static GuardianEnduranceEvidence(){EditorApplication.update+=Tick;}
        public static void Headless()
        {
            if(!Application.isBatchMode||SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)throw new InvalidOperationException("Use isolated batchmode/nographics");
            GuardianInteractiveSetup.Build();UnityEngine.Object.FindFirstObjectByType<GuardianSimulator>().gameObject.AddComponent<GuardianEnduranceVerification>();EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());SessionState.SetBool(Key,true);SessionState.SetBool(Key+"Done",false);EditorApplication.isPlaying=true;
        }
        private static void Tick()
        {
            if(!SessionState.GetBool(Key,false))return;if(SessionState.GetBool(Key+"Done",false)&&!EditorApplication.isPlaying){SessionState.SetBool(Key,false);EditorApplication.Exit(SessionState.GetInt(Key+"Exit",1));return;}if(!EditorApplication.isPlaying)return;
            var test=UnityEngine.Object.FindFirstObjectByType<GuardianEnduranceVerification>();if(test==null||test.OutputRoot==null)return;
            if(EditorApplication.timeSinceStartup>=nextWrite||test.Done){nextWrite=EditorApplication.timeSinceStartup+5;File.WriteAllText(Path.Combine(test.OutputRoot,test.Done?"acceptance.json":"running.json"),JsonUtility.ToJson(test.Data,true)+"\n");}
            if(test.Done){SessionState.SetInt(Key+"Exit",test.Passed?0:1);SessionState.SetBool(Key+"Done",true);EditorApplication.isPlaying=false;}
        }
    }
}
