using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    [InitializeOnLoad]
    public static class GuardianInteractiveSetup
    {
        private const string Key="Guardian.InteractiveMvp.Test";private static double began;
        static GuardianInteractiveSetup() {EditorApplication.update+=Tick;}
        [MenuItem("Guardian/Interactive MVP/Create original public scene")]
        public static void Build() {BuildScene(false);}
        private static void BuildScene(bool test)
        {
            if(Application.unityVersion!="6000.3.12f1")throw new InvalidOperationException("Unity 6000.3.12f1 required");
            RestaurantAssetPipeline.EnsureAssets();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("Guardian Interactive MVP");var sim=root.AddComponent<GuardianSimulator>();sim.EnsureWorld();
            if(root.transform.Find("Generated 3D World").GetComponentsInChildren<Transform>().Length==0)throw new InvalidOperationException("Missing world");
            foreach(var t in root.GetComponentsInChildren<Transform>())if(t.name=="Nekobot Model")throw new InvalidOperationException("Public MVP evidence refuses third-party robot models");
            var experience=root.AddComponent<GuardianInteractiveExperience>();experience.AdultPrefab=AssetDatabase.LoadAssetAtPath<GameObject>(RiggedGuestPipeline.Root+"/Prefabs/GuestAdult01.prefab");
            if(test)root.AddComponent<GuardianInteractiveVerification>();
            Directory.CreateDirectory("Assets/Scenes");EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(),"Assets/Scenes/GuardianInteractive_PublicEvidence.unity");
        }
        public static void Headless()
        {
            if(!Application.isBatchMode || SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)throw new InvalidOperationException("Use isolated batchmode/nographics");
            Directory.CreateDirectory("docs/evidence/interactive-mvp");BuildScene(true);SessionState.SetBool(Key,true);SessionState.SetBool(Key+"Done",false);began=EditorApplication.timeSinceStartup;EditorApplication.isPlaying=true;
        }
        public static void HeadlessRegression()
        {
            if(!Application.isBatchMode || SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)throw new InvalidOperationException("Use isolated batchmode/nographics");
            BuildScene(false);var sim=UnityEngine.Object.FindFirstObjectByType<GuardianSimulator>();
            UnityEngine.Object.DestroyImmediate(sim.GetComponent<GuardianInteractiveExperience>());
            if(Array.IndexOf(Environment.GetCommandLineArgs(),"-guardianForceModel")>=0)sim.Settings.Physical.Enabled=true;
            const string scene="Assets/Scenes/GuardianPhysics_PublicEvidence.unity";
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(),scene);
            foreach(string dependency in AssetDatabase.GetDependencies(scene,true))
                if(dependency.StartsWith("Assets/Nekobot/",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Third-party dependency in public regression scene");
            // The evidence runner lives in Assets/Private, which only the patent repository carries.
            var evidence=Type.GetType("Guardian.UnitySim.Editor.GuardianPhysicsEvidence, Assembly-CSharp-Editor");
            if(evidence==null)throw new InvalidOperationException("GuardianPhysicsEvidence is not in this project");
            evidence.GetMethod("Headless").Invoke(null,null);
        }
        private static void Tick()
        {
            if(!SessionState.GetBool(Key,false))return;
            if(SessionState.GetBool(Key+"Done",false) && !EditorApplication.isPlaying)
            {SessionState.SetBool(Key,false);EditorApplication.Exit(SessionState.GetInt(Key+"Exit",1));return;}
            if(!EditorApplication.isPlaying)return;
            var test=UnityEngine.Object.FindFirstObjectByType<GuardianInteractiveVerification>();
            if(test!=null && test.Done)
            {File.WriteAllText("docs/evidence/interactive-mvp/acceptance.json",JsonUtility.ToJson(test.ExportReport,true)+"\n");SessionState.SetInt(Key+"Exit",test.Passed?0:1);SessionState.SetBool(Key+"Done",true);EditorApplication.isPlaying=false;}
        }
    }
}
