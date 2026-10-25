using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace Guardian.UnitySim.Editor
{
    [InitializeOnLoad]public static class GuardianRotationEvidence
    {
        private const string Key="Guardian.Rotation.Tests";static GuardianRotationEvidence(){EditorApplication.update+=Tick;}
        public static void Headless()
        {
            if(!Application.isBatchMode || SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)throw new InvalidOperationException("Use isolated batchmode/nographics");
            GuardianInteractiveSetup.Build();UnityEngine.Object.FindFirstObjectByType<GuardianSimulator>().gameObject.AddComponent<GuardianRotationVerification>();EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());Directory.CreateDirectory("docs/evidence/rotation-sweep");SessionState.SetBool(Key,true);SessionState.SetBool(Key+"Done",false);EditorApplication.isPlaying=true;
        }
        private static void Tick()
        {
            if(!SessionState.GetBool(Key,false))return;if(SessionState.GetBool(Key+"Done",false)&&!EditorApplication.isPlaying){SessionState.SetBool(Key,false);EditorApplication.Exit(SessionState.GetInt(Key+"Exit",1));return;}if(!EditorApplication.isPlaying)return;var test=UnityEngine.Object.FindFirstObjectByType<GuardianRotationVerification>();if(test!=null&&test.Done){File.WriteAllText("docs/evidence/rotation-sweep/acceptance.json",JsonUtility.ToJson(test.ExportReport,true)+"\n");SessionState.SetInt(Key+"Exit",test.Passed?0:1);SessionState.SetBool(Key+"Done",true);EditorApplication.isPlaying=false;}
        }
    }
}
