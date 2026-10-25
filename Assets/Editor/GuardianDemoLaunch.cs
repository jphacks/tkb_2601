using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    // Explicit executeMethod only; no startup callback or automatic launch loop.
    public static class GuardianDemoLaunch
    {
        public static void Open()
        {
            if(Array.IndexOf(Environment.GetCommandLineArgs(),"-guardianLaunchRequested")<0)throw new InvalidOperationException("Explicit launch required");
            if(Application.unityVersion!="6000.3.12f1")throw new InvalidOperationException("Unity 6000.3.12f1 required");
            if(EditorSceneManager.GetActiveScene().isDirty)throw new InvalidOperationException("Unsaved scene; refusing to replace it");
            const string scene="Assets/Scenes/GuardianRestaurant.unity";
            if(File.Exists(scene))EditorSceneManager.OpenScene(scene);
            else {EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);new GameObject("Guardian explicit demo").AddComponent<GuardianSimulator>().EnsureWorld();}
            EditorApplication.isPlaying=true;
        }
    }
}
