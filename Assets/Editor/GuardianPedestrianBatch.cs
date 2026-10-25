using System;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    public static class GuardianPedestrianBatch
    {
        public static void Verify()
        {
            GuardianBatchRunner.Execute("pedestrian", RunChecks);
        }

        public static void RunChecks()
        {
            GuardianLegacyPedestrianRisk crossing=GuardianLegacyPedestrianSystem.Predict(
                Vector3.zero,Vector3.forward*2,new Vector3(-1,0,2),Vector3.right,1.1f,6,.02f);
            if (!crossing.Hazard) throw new InvalidOperationException("Crossing hazard was missed");

            GuardianLegacyPedestrianRisk parallel=GuardianLegacyPedestrianSystem.Predict(
                Vector3.zero,Vector3.forward,new Vector3(4,0,0),Vector3.forward,1.1f,6,.02f);
            if (parallel.Hazard) throw new InvalidOperationException("Separated parallel motion was marked hazardous");

            GuardianLegacyPedestrianRisk overlap=GuardianLegacyPedestrianSystem.Predict(
                Vector3.zero,Vector3.zero,new Vector3(.2f,0,0),Vector3.zero,1.1f,6,.02f);
            if (!overlap.Hazard) throw new InvalidOperationException("Initial overlap was missed");

            GuardianLegacyPedestrianRisk unsupported=GuardianLegacyPedestrianSystem.Predict(
                new Vector3(float.NaN,0,0),Vector3.zero,Vector3.zero,Vector3.zero,1.1f,6,.02f);
            if (!unsupported.Hazard) throw new InvalidOperationException("Unsupported input did not fail closed");

            new GuardianSimulationSettings().Validate();
            Debug.Log("Guardian pedestrian batch verification passed");
        }
    }
}
