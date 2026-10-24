using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    public static class GuardianSettingsBatch
    {
        public static void Verify()
        {
            try
            {
                string root = Directory.GetCurrentDirectory();
                string publicPath = Path.Combine(root, GuardianSettingsLoader.PublicRelativePath);
                GuardianSettingsDocument document = GuardianSettingsLoader.LoadDocument(publicPath);
                if (document.Profile != "public-demo") throw new InvalidDataException("Unexpected public profile");

                GuardianSimulationSettings current = new GuardianSimulationSettings();
                GuardianSimulationSettings unchanged = GuardianSettingsLoader.LoadOverrideOrKeep(
                    current, Array.Empty<string>(), Path.Combine(root, "no-local-private-settings"));
                if (!ReferenceEquals(current, unchanged)) throw new InvalidOperationException("Default settings changed without an override");

                GuardianSimulationSettings selected = GuardianSettingsLoader.LoadOverrideOrKeep(
                    current, new[] { GuardianSettingsLoader.PublicArgument }, root);
                if (JsonUtility.ToJson(selected) != JsonUtility.ToJson(document.Settings))
                    throw new InvalidDataException("Public settings selection did not load the public document");
                if (JsonUtility.ToJson(current) != JsonUtility.ToJson(selected))
                    throw new InvalidDataException("Public sample no longer preserves the existing demo defaults");

                bool rejectedInvalid = false;
                try
                {
                    new GuardianSimulationSettings
                    {
                        WarningThreshold = .8f,
                        LimitedThreshold = .7f
                    }.Validate();
                }
                catch (ArgumentException) { rejectedInvalid = true; }
                if (!rejectedInvalid) throw new InvalidOperationException("Invalid threshold ordering was accepted");

                bool rejectedMissing = false;
                try
                {
                    GuardianSettingsLoader.LoadOverrideOrKeep(
                        current, new[] { GuardianSettingsLoader.PathArgument, Path.Combine(root, "missing-private-settings.json") }, root);
                }
                catch (FileNotFoundException) { rejectedMissing = true; }
                if (!rejectedMissing) throw new InvalidOperationException("An explicitly selected missing file was accepted");

                Debug.Log("Guardian settings batch verification passed");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }
    }
}
