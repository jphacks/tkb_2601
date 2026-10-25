using System;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    /// <summary>One licensed-Unity entry point covering the public demo's cross-feature contract.</summary>
    public static class GuardianIntegrationBatch
    {
        public static void Verify()
        {
            GuardianBatchRunner.Execute("integration", RunChecks);
        }

        public static void RunChecks()
        {
            GuardianSettingsBatch.RunChecks();
            VerifyCapabilityPolicy();
            GuardianPedestrianBatch.RunChecks();
            GuardianInteractionBatch.RunChecks();
            GuardianNetworkBatch.RunChecks();
            GuardianSceneBatch.RunChecks();
            Debug.Log("[Guardian] GUARDIAN_INTEGRATION_OK: settings, capability, pedestrian, interaction, sensor link, scene");
        }

        private static void VerifyCapabilityPolicy()
        {
            var settings = new GuardianSimulationSettings();
            if (GuardianCapabilityPolicy.FromVibration(settings.WarningThreshold - .001f, settings) != RobotCondition.Normal ||
                GuardianCapabilityPolicy.FromVibration(settings.WarningThreshold, settings) != RobotCondition.Warning ||
                GuardianCapabilityPolicy.FromVibration(settings.LimitedThreshold, settings) != RobotCondition.Limited)
                throw new InvalidOperationException("Capability thresholds changed at a boundary");

            DeliveryKind[] kinds = { DeliveryKind.Soup, DeliveryKind.Food, DeliveryKind.EmptyDishes };
            bool[,] expected = { { true, true, true }, { false, true, true }, { false, false, true } };
            for (int condition = 0; condition < 3; condition++)
            for (int kind = 0; kind < kinds.Length; kind++)
                if (GuardianCapabilityPolicy.Allows((RobotCondition)condition, kinds[kind]) != expected[condition, kind])
                    throw new InvalidOperationException("Capability matrix changed at " + condition + "/" + kind);

            if (GuardianCapabilityPolicy.Allows(RobotCondition.Normal, DeliveryKind.EmptyDishes, true) ||
                GuardianCapabilityPolicy.Allows((RobotCondition)99, DeliveryKind.Food))
                throw new InvalidOperationException("Capability policy did not fail closed");
            Debug.Log("Guardian capability policy verification passed");
        }
    }
}
