using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    /// <summary>Licensed Unity batch entry point for non-destructive scene-generation verification.</summary>
    public static class GuardianSceneBatch
    {
        private const string VerificationPath = "Assets/Scenes/GuardianSceneVerification.unity";

        public static void Verify()
        {
            GuardianBatchRunner.Execute("scene", RunChecks);
        }

        public static void RunChecks()
        {
            EditorBuildSettingsScene[] originalBuildScenes = EditorBuildSettings.scenes;
            try
            {
                if (Application.unityVersion != "6000.3.12f1")
                    throw new InvalidOperationException("Unity 6000.3.12f1 required; got " + Application.unityVersion);
                if (File.Exists(VerificationPath) && !AssetDatabase.DeleteAsset(VerificationPath))
                    throw new IOException("Could not clear previous verification scene");

                GuardianSceneSetup.GenerateScene(VerificationPath, false);
                if (!File.Exists(VerificationPath)) throw new FileNotFoundException("Verification scene was not saved", VerificationPath);
                if (!EditorBuildSettings.scenes.Any(scene => scene.path == VerificationPath && scene.enabled))
                    throw new InvalidOperationException("Generated scene was not enabled in Build Settings");
                foreach (EditorBuildSettingsScene scene in originalBuildScenes)
                    if (!EditorBuildSettings.scenes.Any(current => current.path == scene.path && current.enabled == scene.enabled))
                        throw new InvalidOperationException("Generation replaced an existing Build Settings entry: " + scene.path);

                Debug.Log("[Guardian] GUARDIAN_SCENE_BATCH_OK: generated and validated an isolated public demo scene");
            }
            finally
            {
                if (File.Exists(VerificationPath)) AssetDatabase.DeleteAsset(VerificationPath);
                EditorBuildSettings.scenes = originalBuildScenes;
                AssetDatabase.SaveAssets();
            }
        }
    }
}
