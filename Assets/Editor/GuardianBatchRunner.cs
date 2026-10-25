using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Guardian.UnitySim.Editor
{
    /// <summary>Runs a batch check with one consistent success marker and process exit code.</summary>
    public static class GuardianBatchRunner
    {
        public static void Execute(string suite, Action checks)
        {
            string baseline=null;
            try
            {
                // A fresh batch project starts with an empty untitled scene. Unity refuses additive
                // scene creation until it has a path; never save or replace a user's populated scene.
                Scene active=SceneManager.GetActiveScene();
                if (Application.isBatchMode && SceneManager.sceneCount==1 && active.IsValid() &&
                    string.IsNullOrEmpty(active.path) && active.rootCount==0)
                {
                    Directory.CreateDirectory("Assets/Scenes");
                    baseline="Assets/Scenes/GuardianBatch-"+Guid.NewGuid().ToString("N")+".unity";
                    if (!EditorSceneManager.SaveScene(active,baseline)) throw new IOException("Could not prepare the batch scene");
                }
                checks();
                if (baseline!=null) AssetDatabase.DeleteAsset(baseline);
                Debug.Log("[Guardian] GUARDIAN_BATCH_OK: " + suite);
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                if (baseline!=null) AssetDatabase.DeleteAsset(baseline);
                Debug.LogException(error);
                Debug.LogError("[Guardian] GUARDIAN_BATCH_FAILED: " + suite);
                EditorApplication.Exit(1);
            }
        }
    }
}
