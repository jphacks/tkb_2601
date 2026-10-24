using System;
using UnityEditor;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    /// <summary>Runs a batch check with one consistent success marker and process exit code.</summary>
    public static class GuardianBatchRunner
    {
        public static void Execute(string suite, Action checks)
        {
            try
            {
                checks();
                Debug.Log("[Guardian] GUARDIAN_BATCH_OK: " + suite);
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                Debug.LogError("[Guardian] GUARDIAN_BATCH_FAILED: " + suite);
                EditorApplication.Exit(1);
            }
        }
    }
}
