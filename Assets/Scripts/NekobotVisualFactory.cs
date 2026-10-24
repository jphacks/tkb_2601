using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Rendering;
#endif

namespace Guardian.UnitySim
{
    /// <summary>
    /// Optional, locally imported BOOTH Nekobot visual. The movement/Guardian logic
    /// stays on the parent GuardianRobot object; no third-party model is required.
    /// This script contains no BOOTH assets and may be redistributed independently.
    /// </summary>
    public static class NekobotVisualFactory
    {
        public const string VisualRootName = "Robot Visual";
        public const string FbxPath = "Assets/Nekobot/nekobot.fbx";
        public const string PrefabPath = "Assets/Nekobot/nekobot.prefab";
        public const float DefaultHeight = 1.9f;
        public const float DefaultYaw = 0f;
        // Robot movement origins are at y=0.34; restaurant floor is near y=0.
        public const float DefaultFloorOffset = -0.34f;

        public static bool HasLocalModel(bool preferPrefab = false)
        {
#if UNITY_EDITOR
            return GetModelAsset(preferPrefab) != null;
#else
            return false;
#endif
        }

        /// <summary>
        /// Instantiates an FBX/Prefab *under* a visual-only child, normalizes the
        /// renderer bounds and anchors its visible bottom to the restaurant floor.
        /// Does not change the robot movement root, routes, tasks or sensor state.
        /// </summary>
        public static bool TryAttachLocalModel(
            Transform visualRoot, Transform robotRoot, bool preferPrefab = false,
            float targetHeight = DefaultHeight, float yawDegrees = DefaultYaw,
            float floorOffset = DefaultFloorOffset)
        {
#if UNITY_EDITOR
            if (visualRoot == null || robotRoot == null || Application.isPlaying)
                return false;

            GameObject asset = GetModelAsset(preferPrefab);
            if (asset == null) return false;

            GameObject instance = PrefabUtility.InstantiatePrefab(asset, visualRoot) as GameObject;
            if (instance == null)
            {
                Debug.LogWarning("[Guardian] Failed to instantiate local Nekobot model: " + AssetDatabase.GetAssetPath(asset));
                return false;
            }

            instance.name = "Nekobot Model";
            Transform model = instance.transform;
            model.localPosition = Vector3.zero;
            model.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);
            // Preserve the imported model root scale (FBX file units can differ).

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (!TryBounds(renderers, out Bounds bounds) || bounds.size.y < 0.00001f)
            {
                Object.DestroyImmediate(instance);
                Debug.LogWarning("[Guardian] Imported Nekobot has no usable visible renderers. Check the FBX import.");
                return false;
            }

            model.localScale *= Mathf.Clamp(targetHeight, 0.4f, 4f) / bounds.size.y;
            if (!TryBounds(renderers, out bounds))
            {
                Object.DestroyImmediate(instance);
                return false;
            }

            // Compute corrections after scaling. Bounds are in world coordinates;
            // do NOT multiply bounds.min.y by scale again (the original bug).
            Vector3 correction = new Vector3(
                robotRoot.position.x - bounds.center.x,
                robotRoot.position.y + floorOffset - bounds.min.y,
                robotRoot.position.z - bounds.center.z);
            model.position += correction;

            ApplyCompatibleLocalMaterials(renderers);
            // FBX may have an Animator but should not apply root motion while the
            // parent GuardianRobot drives the kinematic simulator movement.
            foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
                animator.applyRootMotion = false;

            // The simulator moves the parent kinematically. Avatar or FBX
            // colliders are not used as the robot navigation body.
            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            Debug.Log("[Guardian] Local Nekobot attached: " + AssetDatabase.GetAssetPath(asset));
            return true;
#else
            return false;
#endif
        }

#if UNITY_EDITOR
        private static GameObject GetModelAsset(bool preferPrefab)
        {
            // The supplied archive contains both. Prefer FBX because its VRChat
            // avatar prefab contains components referring to an optional SDK.
            string first = preferPrefab ? PrefabPath : FbxPath;
            string second = preferPrefab ? FbxPath : PrefabPath;
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(first);
            return asset != null ? asset : AssetDatabase.LoadAssetAtPath<GameObject>(second);
        }

        private static bool TryBounds(Renderer[] renderers, out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return found;
        }

        private static void ApplyCompatibleLocalMaterials(Renderer[] renderers)
        {
            Shader shader = GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Lit")
                : Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogWarning("[Guardian] No matching built-in/URP shader found. Nekobot materials may render magenta.");
                return;
            }

            const string materialFolder = "Assets/Nekobot/_GuardianLocalMaterials";
            if (!AssetDatabase.IsValidFolder(materialFolder))
                AssetDatabase.CreateFolder("Assets/Nekobot", "_GuardianLocalMaterials");

            bool urp = GraphicsSettings.currentRenderPipeline != null;
            Texture2D baseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Nekobot/tex.png");
            foreach (Renderer renderer in renderers)
            {
                Material[] original = renderer.sharedMaterials;
                if (original == null || original.Length == 0) continue;
                Material[] compatible = new Material[original.Length];
                for (int i = 0; i < original.Length; i++)
                {
                    Material source = original[i];
                    if (source == null) continue;
                    string label = source.name.ToLowerInvariant().Replace(" (instance)", "");
                    bool blue = label.Contains("blue");
                    // The two materials in Nekobot.unitypackage are base and blue.
                    // Local generated copies preserve the source assets untouched.
                    string kind = blue ? "blue" : "base";
                    string suffix = urp ? "URP" : "Builtin";
                    string path = materialFolder + "/Guardian_" + kind + "_" + suffix + ".mat";
                    Material replacement = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (replacement == null)
                    {
                        replacement = new Material(shader);
                        replacement.name = "Guardian_" + kind + "_" + suffix;
                        replacement.color = blue ? new Color(0.032f, 0.359f, 0.8f) : Color.white;
                        if (!blue && baseTexture != null)
                        {
                            string textureProp = urp ? "_BaseMap" : "_MainTex";
                            if (replacement.HasProperty(textureProp))
                                replacement.SetTexture(textureProp, baseTexture);
                        }
                        AssetDatabase.CreateAsset(replacement, path);
                    }
                    compatible[i] = replacement;
                }
                renderer.sharedMaterials = compatible;
            }
            AssetDatabase.SaveAssets();
        }
#endif
    }
}
