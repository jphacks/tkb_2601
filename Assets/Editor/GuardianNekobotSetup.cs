using Guardian.UnitySim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Guardian.UnitySim.Editor
{
    /// <summary>Replace visuals only, preserving task logic, routes and edited restaurant scenery.</summary>
    public sealed class GuardianNekobotSetup : EditorWindow
    {
        private bool _preferPrefab;
        private float _height = NekobotVisualFactory.DefaultHeight;
        private float _yaw = NekobotVisualFactory.DefaultYaw;
        private float _floorOffset = NekobotVisualFactory.DefaultFloorOffset;

        [MenuItem("Guardian/Replace Robot Visuals With Local Nekobot")]
        private static void ApplyDefaults()
        {
            Apply(false, NekobotVisualFactory.DefaultHeight,
                NekobotVisualFactory.DefaultYaw, NekobotVisualFactory.DefaultFloorOffset);
        }

        [MenuItem("Guardian/Nekobot Model Settings...")]
        private static void ShowSettings()
        {
            GetWindow<GuardianNekobotSetup>("Nekobot Visual Setup").Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Nekobot — Local Import Only", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Import Nekobot.unitypackage from BOOTH locally first. The FBX is preferred because the VRChat avatar prefab may need external SDK components. No third-party model data is committed to Git.",
                MessageType.Info);
            EditorGUILayout.LabelField("FBX", NekobotVisualFactory.FbxPath);
            EditorGUILayout.LabelField("Avatar Prefab", NekobotVisualFactory.PrefabPath);
            _preferPrefab = EditorGUILayout.Toggle("Prefer VRChat Prefab", _preferPrefab);
            _height = EditorGUILayout.Slider("Visual Height (meters)", _height, 0.4f, 4f);
            _yaw = EditorGUILayout.Slider("Visual Yaw (degrees)", _yaw, -180f, 180f);
            _floorOffset = EditorGUILayout.FloatField("Floor relative to robot Y", _floorOffset);
            GUILayout.Space(10f);
            if (GUILayout.Button("Replace visuals of Robot A / B / C"))
                Apply(_preferPrefab, _height, _yaw, _floorOffset);
            EditorGUILayout.HelpBox(
                "This operation changes only the Robot Visual child. It does not regenerate the restaurant scene. Save the scene after reviewing the three models (Ctrl+S).",
                MessageType.None);
        }

        private static void Apply(bool preferPrefab, float height, float yaw, float floorOffset)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Guardian", "Exit Play Mode before changing model visuals.", "OK");
                return;
            }
            if (!NekobotVisualFactory.HasLocalModel(preferPrefab))
            {
                EditorUtility.DisplayDialog("Guardian: Nekobot not imported",
                    "Import Nekobot.unitypackage first: Assets > Import Package > Custom Package...\n\nExpected:\n"
                    + NekobotVisualFactory.FbxPath + "\nor\n" + NekobotVisualFactory.PrefabPath,
                    "OK");
                return;
            }

            GuardianSimulator simulator = Object.FindFirstObjectByType<GuardianSimulator>();
            if (simulator == null)
            {
                EditorUtility.DisplayDialog("Guardian: Scene not open",
                    "Open Assets/Scenes/GuardianRestaurant.unity, or create the scene via Guardian > Create or Rebuild 3D Simulator Scene.",
                    "OK");
                return;
            }

            GuardianRobot[] robots = simulator.GetComponentsInChildren<GuardianRobot>(true);
            if (robots.Length != 3)
            {
                EditorUtility.DisplayDialog("Guardian: Unexpected robot count",
                    "Expected 3 robots; found " + robots.Length + ". Visuals were not changed.", "OK");
                return;
            }

            int applied = ReplaceVisuals(simulator, preferPrefab, height, yaw, floorOffset, true);
            Debug.Log("[Guardian] Replaced " + applied + "/3 visuals; save with Ctrl+S.");
            EditorUtility.DisplayDialog("Guardian Nekobot Import",
                "Applied to " + applied + " of 3 robots. Check appearance, then save (Ctrl+S).", "OK");
        }

        /// <summary>
        /// Replaces only visual children. Batch callers pass recordUndo=false,
        /// preserving robot scripts, task assignments and restaurant furniture.
        /// </summary>
        public static int ReplaceVisuals(GuardianSimulator simulator, bool preferPrefab,
            float height, float yaw, float floorOffset, bool recordUndo)
        {
            if (simulator == null) throw new System.ArgumentNullException(nameof(simulator));
            if (!NekobotVisualFactory.HasLocalModel(preferPrefab))
                throw new System.InvalidOperationException("Local Nekobot model missing");
            GuardianRobot[] robots = simulator.GetComponentsInChildren<GuardianRobot>(true);
            if (robots.Length != 3)
                throw new System.InvalidOperationException("Expected 3 robots, found " + robots.Length);
            int group = recordUndo ? Undo.GetCurrentGroup() : -1;
            if (recordUndo) Undo.SetCurrentGroupName("Replace Guardian robot visuals");
            int applied = 0;
            foreach (GuardianRobot robot in robots)
            {
                GameObject visualObject = new GameObject("Robot Visual (New)");
                if (recordUndo) Undo.RegisterCreatedObjectUndo(visualObject, "Create Nekobot visual");
                visualObject.transform.SetParent(robot.transform, false);
                if (!NekobotVisualFactory.TryAttachLocalModel(
                    visualObject.transform, robot.transform, preferPrefab, height, yaw, floorOffset))
                {
                    if (recordUndo) Undo.DestroyObjectImmediate(visualObject);
                    else Object.DestroyImmediate(visualObject);
                    Debug.LogError("[Guardian] Model application failed: " + robot.RobotId);
                    continue;
                }
                Transform previous = robot.transform.Find(NekobotVisualFactory.VisualRootName);
                if (previous != null)
                {
                    if (recordUndo) Undo.DestroyObjectImmediate(previous.gameObject);
                    else Object.DestroyImmediate(previous.gameObject);
                }
                string[] oldParts = { "Wheel Base", "Body", "Cat Face", "Left Ear", "Right Ear",
                    "Delivery Tray", "Screen Left Eye", "Screen Right Eye",
                    "Screen Mouth", "Nekobot Model" };
                foreach (string name in oldParts)
                {
                    Transform part = robot.transform.Find(name);
                    if (part == null) continue;
                    if (recordUndo) Undo.DestroyObjectImmediate(part.gameObject);
                    else Object.DestroyImmediate(part.gameObject);
                }
                visualObject.name = NekobotVisualFactory.VisualRootName;
                EditorUtility.SetDirty(robot.gameObject);
                applied++;
            }
            if (recordUndo) Undo.CollapseUndoOperations(group);
            if (applied > 0) EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return applied;
        }
    }
}
