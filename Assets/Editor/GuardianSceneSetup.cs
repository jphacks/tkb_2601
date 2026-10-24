using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Guardian.UnitySim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Guardian.UnitySim.Editor
{
    /// <summary>Creates and validates the public demo scene without replacing the editor workspace.</summary>
    [InitializeOnLoad]
    public static class GuardianSceneSetup
    {
        public const string ScenePath = "Assets/Scenes/GuardianRestaurant.unity";
        public const string BackupDirectory = "Library/GuardianSceneBackups";

        static GuardianSceneSetup()
        {
            EditorApplication.delayCall += CreateMissingScene;
        }

        private static void CreateMissingScene()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || File.Exists(ScenePath)) return;
            try
            {
                GenerateScene(ScenePath, false);
                Debug.Log("[Guardian] Created missing public demo scene without changing the open editor scene: " + ScenePath);
            }
            catch (Exception error)
            {
                Debug.LogError("[Guardian] Automatic scene creation failed. Use Guardian > Scene Tools... after resolving the error.\n" + error);
            }
        }

        [MenuItem("Guardian/Scene Tools...", priority = 0)]
        public static void ShowTools()
        {
            GuardianSceneTools.ShowWindow();
        }

        [MenuItem("Guardian/Create or Rebuild 3D Simulator Scene", priority = 1)]
        public static void RebuildScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Guardian", "Exit Play Mode before rebuilding the scene.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string backup = null;
            if (File.Exists(ScenePath))
            {
                if (!EditorUtility.DisplayDialog("Rebuild Guardian Scene",
                    "Rebuild GuardianRestaurant.unity?\n\nThe existing file will first be copied to Library/GuardianSceneBackups. " +
                    "The public demo actors and environment will then be regenerated from the current code and public assets.",
                    "Back Up and Rebuild", "Cancel")) return;
                backup = BackupExistingScene();
            }

            CloseLoadedTargetScene();
            try
            {
                // Save over the existing asset so its Unity .meta GUID remains stable.
                GenerateScene(ScenePath, true, true);
            }
            catch
            {
                if (backup != null && File.Exists(backup))
                {
                    string directory = Path.GetDirectoryName(ScenePath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.Copy(backup, ScenePath, true);
                    AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceSynchronousImport);
                    Debug.LogWarning("[Guardian] Scene generation failed; restored the previous scene from " + backup);
                }
                throw;
            }
            string message = "Created and validated " + ScenePath + ".";
            if (backup != null) message += "\n\nPrevious scene backup:\n" + backup;
            EditorUtility.DisplayDialog("Guardian Scene Ready", message, "OK");
        }

        [MenuItem("Guardian/Validate Saved 3D Simulator Scene", priority = 2)]
        public static void ValidateSavedSceneMenu()
        {
            IReadOnlyList<string> report = ValidateSavedScene();
            Debug.Log("[Guardian] GUARDIAN_SCENE_OK\n" + string.Join("\n", report));
            EditorUtility.DisplayDialog("Guardian Scene Valid",
                "The saved public demo scene passed " + report.Count + " validation checks. See Console for details.", "OK");
        }

        public static IReadOnlyList<string> ValidateSavedScene()
        {
            if (!File.Exists(ScenePath)) throw new FileNotFoundException("Generate the public demo scene first", ScenePath);
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool openedForValidation = !scene.IsValid() || !scene.isLoaded;
            if (openedForValidation) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            else if (scene.isDirty)
                throw new InvalidOperationException("Save or discard the open Guardian scene changes before validating the saved file");
            try
            {
                GuardianSimulator simulator = FindSimulator(scene);
                return ValidateGeneratedScene(simulator);
            }
            finally
            {
                if (openedForValidation && scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        public static void GenerateScene(string path, bool keepOpen, bool allowOverwrite = false)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Scene path must be an Assets/*.unity path", nameof(path));
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before generating a scene");
            bool existedBefore = File.Exists(path);
            if (existedBefore && !allowOverwrite)
                throw new IOException("Refusing to overwrite an existing scene without the rebuild confirmation: " + path);

            RestaurantAssetPipeline.EnsureAssets();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            Scene previous = SceneManager.GetActiveScene();
            Scene generated = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            bool saved = false;
            EditorBuildSettingsScene[] originalBuildScenes = EditorBuildSettings.scenes;
            try
            {
                SceneManager.SetActiveScene(generated);
                GameObject root = new GameObject("Guardian Simulator");
                GuardianSimulator simulator = root.AddComponent<GuardianSimulator>();
                simulator.EnsureWorld();
                IReadOnlyList<string> report = ValidateGeneratedScene(simulator);
                EditorSceneManager.MarkSceneDirty(generated);
                if (!EditorSceneManager.SaveScene(generated, path))
                    throw new IOException("Scene could not be saved: " + path);
                saved = true;
                EnsureBuildSettingsScene(path);
                AssetDatabase.SaveAssets();
                Debug.Log("[Guardian] GUARDIAN_SCENE_GENERATED_OK: " + report.Count + " checks; " + path);
                if (keepOpen)
                {
                    Selection.activeGameObject = root;
                    if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
                }
            }
            catch
            {
                if (generated.IsValid() && generated.isLoaded) EditorSceneManager.CloseScene(generated, true);
                if (saved && !existedBefore && File.Exists(path)) AssetDatabase.DeleteAsset(path);
                EditorBuildSettings.scenes = originalBuildScenes;
                throw;
            }
            finally
            {
                if (!keepOpen && generated.IsValid() && generated.isLoaded)
                    EditorSceneManager.CloseScene(generated, true);
                if ((!keepOpen || !generated.IsValid() || !generated.isLoaded) && previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
            }
        }

        public static string BackupExistingScene()
        {
            if (!File.Exists(ScenePath)) return null;
            Directory.CreateDirectory(BackupDirectory);
            string file = "GuardianRestaurant-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".unity";
            string backup = Path.Combine(BackupDirectory, file);
            File.Copy(ScenePath, backup, false);
            return backup.Replace('\\', '/');
        }

        private static void CloseLoadedTargetScene()
        {
            Scene loaded = SceneManager.GetSceneByPath(ScenePath);
            if (loaded.IsValid() && loaded.isLoaded) EditorSceneManager.CloseScene(loaded, true);
        }

        private static GuardianSimulator FindSimulator(Scene scene)
        {
            GuardianSimulator[] simulators = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<GuardianSimulator>(true)).ToArray();
            if (simulators.Length != 1)
                throw new InvalidOperationException("Expected exactly one GuardianSimulator in " + scene.path + "; found " + simulators.Length);
            return simulators[0];
        }

        private static IReadOnlyList<string> ValidateGeneratedScene(GuardianSimulator simulator)
        {
            var report = new List<string>();
            GuardianRobot[] robots = simulator.GetComponentsInChildren<GuardianRobot>(true);
            if (robots.Length != 3) throw new InvalidOperationException("Expected three robots; found " + robots.Length);
            if (robots.Select(robot => robot.RobotId).Distinct().Count() != 3)
                throw new InvalidOperationException("Robot IDs must be unique");
            report.Add("PASS: one simulator and three uniquely identified robots");

            Transform environment = simulator.transform.Find("Generated 3D World/" + RestaurantVisuals.RootName);
            if (environment == null) throw new InvalidOperationException("Generated restaurant environment missing");
            report.AddRange(RestaurantEnvironmentEditor.Validate(environment, simulator, false));
            return report;
        }

        public static void EnsureBuildSettingsScene(string path)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            int index = scenes.FindIndex(item => string.Equals(item.path, path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) scenes[index] = new EditorBuildSettingsScene(path, true);
            else scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
