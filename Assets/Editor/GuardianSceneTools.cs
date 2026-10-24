using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    /// <summary>Single, reviewable entry point for public demo scene generation and validation.</summary>
    public sealed class GuardianSceneTools : EditorWindow
    {
        public static void ShowWindow()
        {
            GetWindow<GuardianSceneTools>("Guardian Scene Tools").Show();
        }

        private void OnGUI()
        {
            bool exists = File.Exists(GuardianSceneSetup.ScenePath);
            EditorGUILayout.LabelField("Public Demo Scene", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Path", GuardianSceneSetup.ScenePath);
            EditorGUILayout.LabelField("Status", exists ? "Saved scene found" : "Scene not generated");
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Generation uses only the public code and repository-owned assets. It does not import private models, audio, " +
                "operational settings, or rights-unconfirmed material. The generated demo behavior remains the code defaults.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "Rebuild asks for confirmation, keeps other Build Settings entries, and writes a recoverable copy of the old scene under " +
                GuardianSceneSetup.BackupDirectory + ".", MessageType.None);

            using (new EditorGUI.DisabledScope(!exists))
            {
                if (GUILayout.Button("Open Saved Scene"))
                {
                    if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                        EditorSceneManager.OpenScene(GuardianSceneSetup.ScenePath, OpenSceneMode.Single);
                }
                if (GUILayout.Button("Validate Saved Scene")) RunSafely(GuardianSceneSetup.ValidateSavedSceneMenu);
            }
            if (GUILayout.Button(exists ? "Back Up and Rebuild Scene..." : "Generate Scene"))
                RunSafely(GuardianSceneSetup.RebuildScene);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Optional local visuals", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Third-party robot visuals are optional and local-only. Review their license before import; scene generation never requires them.",
                MessageType.Warning);
            if (GUILayout.Button("Open Nekobot Visual Settings..."))
                EditorApplication.ExecuteMenuItem("Guardian/Nekobot Model Settings...");
        }

        private static void RunSafely(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorUtility.DisplayDialog("Guardian Scene Tools", error.Message, "OK");
            }
        }
    }
}
