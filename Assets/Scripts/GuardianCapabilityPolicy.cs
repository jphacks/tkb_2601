using System;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuardianSimulationMode { LegacyStory, Physics }

    [Serializable]
    public sealed class GuardianSimulationSettings
    {
        public int Seed=2026;
        [Min(.1f)] public float MinimumTaskInterval=18;
        [Min(.1f)] public float MaximumTaskInterval=30;
        [Min(0)] public float MaximumDepartureDelay=3;
        [Min(1)] public int MaximumOpenTasks=24;
        public bool RandomFaults=true;
        [Min(1)] public float MinimumFaultInterval=35;
        [Min(1)] public float MaximumFaultInterval=60;
        [Range(.0001f,.9999f)] public float WarningThreshold=.30f;
        [Range(0,1)] public float LimitedThreshold=.70f;
        [Min(.2f)] public float MealSeconds=8;
        public bool CollectEmptyOnDelivery=true;
        [Min(.2f)] public float MotorAcceleration=6;
        [Min(.1f)] public float MaximumSpeed=1.9f;
        public void Validate()
        {
            if (MinimumTaskInterval<=0 || MaximumTaskInterval<MinimumTaskInterval || MaximumDepartureDelay<0
                || MaximumOpenTasks<1 || MinimumFaultInterval<=0 || MaximumFaultInterval<MinimumFaultInterval
                || WarningThreshold<=0 || LimitedThreshold>1 || LimitedThreshold<=WarningThreshold
                || MealSeconds<.2f || float.IsNaN(MealSeconds) || float.IsInfinity(MealSeconds) || MotorAcceleration<=0 || MaximumSpeed<=0)
                throw new ArgumentException("Invalid Guardian simulation settings");
        }
    }

    public static class GuardianCapabilityPolicy
    {
        // Rows: Normal, Warning, Limited. Columns: Soup, Food, EmptyDishes.
        private static readonly bool[,] Matrix={{true,true,true},{false,true,true},{false,false,true}};
        public static bool Allows(RobotCondition condition,DeliveryKind kind,bool emergencyStopped=false)
        {
            return !emergencyStopped && (int)condition>=0 && (int)condition<3 && (int)kind>=0 && (int)kind<3
                && Matrix[(int)condition,(int)kind];
        }
        public static RobotCondition FromVibration(float value,GuardianSimulationSettings settings)
        { return value>=settings.LimitedThreshold?RobotCondition.Limited:value>=settings.WarningThreshold?RobotCondition.Warning:RobotCondition.Normal; }
        public static string AllowedText(RobotCondition condition,bool stopped=false)
        { return stopped?"STOP / NO TASKS":condition==RobotCondition.Normal?"SOUP / FOOD / RETURN":condition==RobotCondition.Warning?"FOOD / RETURN":"RETURN ONLY"; }
    }
}
