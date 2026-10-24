using System.IO;
using Guardian.UnitySim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Guardian.UnitySim.Editor
{
    /// <summary>Creates a saved three-dimensional Unity scene on first editor open.</summary>
    [InitializeOnLoad]
    public static class GuardianSceneSetup
    {
        private const string ScenePath = "Assets/Scenes/GuardianRestaurant.unity";
        static GuardianSceneSetup()
        {
            EditorApplication.delayCall += CreateMissingScene;
        }

        private static void CreateMissingScene()
        {
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (File.Exists(ScenePath)) return;
            CreateScene();
        }

        [MenuItem("Guardian/Create or Rebuild 3D Simulator Scene")]
        public static void RebuildScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog("Rebuild Guardian Scene",
                    "Rebuild GuardianRestaurant.unity? Existing scene edits will be overwritten.",
                    "Rebuild", "Cancel")) return;
            CreateScene();
        }

        private static void CreateScene()
        {
            RestaurantAssetPipeline.EnsureAssets();
            if (!Directory.Exists("Assets/Scenes")) Directory.CreateDirectory("Assets/Scenes");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject root = new GameObject("Guardian Simulator");
            GuardianSimulator simulator = root.AddComponent<GuardianSimulator>();
            simulator.EnsureWorld();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("[Guardian] Scene could not be saved: " + ScenePath);
                return;
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Selection.activeGameObject = root;
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
            Debug.Log("[Guardian] Created 3D scene: " + ScenePath);
        }
    }
}
