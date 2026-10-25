using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    [InitializeOnLoad] public static class GuardianDynamicsEvidence
    {
        private const string Key="Guardian.Dynamics.Matrix";
        static GuardianDynamicsEvidence(){EditorApplication.update+=Tick;}
        public static void Headless()
        {
            if(Application.unityVersion!="6000.3.12f1" || !Application.isBatchMode || SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)throw new InvalidOperationException("Exact Unity, isolated batchmode/nographics required");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);new GameObject("Guardian physical matrix").AddComponent<GuardianDynamicsVerification>();
            Directory.CreateDirectory("docs/evidence/physical-load-model");SessionState.SetBool(Key,true);SessionState.SetBool(Key+"Done",false);EditorApplication.isPlaying=true;
        }
        private static void Tick()
        {
            if(!SessionState.GetBool(Key,false))return;
            if(SessionState.GetBool(Key+"Done",false) && !EditorApplication.isPlaying){SessionState.SetBool(Key,false);EditorApplication.Exit(SessionState.GetInt(Key+"Exit",1));return;}
            if(!EditorApplication.isPlaying)return;var test=UnityEngine.Object.FindFirstObjectByType<GuardianDynamicsVerification>();
            if(test!=null && test.Done){File.WriteAllText("docs/evidence/physical-load-model/matrix.json",JsonUtility.ToJson(test.ExportReport,true)+"\n");SessionState.SetInt(Key+"Exit",test.Passed?0:1);SessionState.SetBool(Key+"Done",true);EditorApplication.isPlaying=false;}
        }
    }
}
