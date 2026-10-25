using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace Guardian.UnitySim.Editor
{
    [InitializeOnLoad]public static class GuardianSafetyBoundaryEvidence
    {
        private const string Key="Guardian.Safety.Boundary";static GuardianSafetyBoundaryEvidence(){EditorApplication.update+=Tick;}
        public static void Headless()
        {
            if(Application.unityVersion!="6000.3.12f1"||!Application.isBatchMode||SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)throw new InvalidOperationException("Exact Unity isolated batchmode/nographics required");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);new GameObject("Safety boundary tests").AddComponent<GuardianSafetyBoundaryVerification>();Directory.CreateDirectory("docs/evidence/safety-boundary");SessionState.SetBool(Key,true);SessionState.SetBool(Key+"Done",false);EditorApplication.isPlaying=true;
        }
        private static void Tick()
        {
            if(!SessionState.GetBool(Key,false))return;if(SessionState.GetBool(Key+"Done",false)&&!EditorApplication.isPlaying){SessionState.SetBool(Key,false);EditorApplication.Exit(SessionState.GetInt(Key+"Exit",1));return;}if(!EditorApplication.isPlaying)return;var test=UnityEngine.Object.FindFirstObjectByType<GuardianSafetyBoundaryVerification>();if(test!=null&&test.Done){File.WriteAllText("docs/evidence/safety-boundary/acceptance.json",JsonUtility.ToJson(test.ExportReport,true)+"\n");SessionState.SetInt(Key+"Exit",test.Passed?0:1);SessionState.SetBool(Key+"Done",true);EditorApplication.isPlaying=false;}
        }
    }
}
