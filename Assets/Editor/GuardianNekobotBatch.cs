using System;
using System.IO;
using Guardian.UnitySim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Guardian.UnitySim.Editor
{
    /// <summary>Headless Unity CLI setup; opens, updates and saves a real 3D scene.</summary>
    public static class GuardianNekobotBatch
    {
        private const string ScenePath = "Assets/Scenes/GuardianRestaurant.unity";

        // Unity.exe -batchmode -quit -projectPath ... -executeMethod
        // Guardian.UnitySim.Editor.GuardianNekobotBatch.Configure
        public static void Configure()
        {
            try
            {
                if (Application.unityVersion != "6000.3.12f1")
                    throw new InvalidOperationException(
                        "Expected Unity 6000.3.12f1; got " + Application.unityVersion);
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("Must run outside Play mode.");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                if (!NekobotVisualFactory.HasLocalModel())
                    throw new FileNotFoundException("No local Nekobot model at " + NekobotVisualFactory.FbxPath);

                Scene scene;
                if (File.Exists(ScenePath))
                {
                    scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                }
                else
                {
                    Directory.CreateDirectory("Assets/Scenes");
                    scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    GameObject root = new GameObject("Guardian Simulator");
                    root.AddComponent<GuardianSimulator>().EnsureWorld();
                }

                GuardianSimulator simulator =
                    UnityEngine.Object.FindFirstObjectByType<GuardianSimulator>();
                if (simulator == null)
                    throw new InvalidOperationException("GuardianSimulator missing in " + ScenePath);
                int applied = GuardianNekobotSetup.ReplaceVisuals(simulator, false,
                    NekobotVisualFactory.DefaultHeight, NekobotVisualFactory.DefaultYaw,
                    NekobotVisualFactory.DefaultFloorOffset, false);
                if (applied != 3)
                    throw new InvalidOperationException("Nekobot attached to only " + applied + "/3 robots");

                foreach (GuardianRobot robot in simulator.GetComponentsInChildren<GuardianRobot>(true))
                {
                    Transform visual = robot.transform.Find(NekobotVisualFactory.VisualRootName);
                    if (visual == null || visual.Find("Nekobot Model") == null)
                        throw new InvalidOperationException("Missing Nekobot visual for Robot " + robot.RobotId);
                }

                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                    throw new IOException("Could not save " + ScenePath);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                AssetDatabase.SaveAssets();
                Debug.Log("[Guardian] GUARDIAN_NEKOBOT_BATCH_OK: three Nekobot visuals saved in " + ScenePath);
            }
            catch (Exception error)
            {
                Debug.LogError("[Guardian] Headless Nekobot import failed: " + error);
                throw;
            }
        }
    }
}
