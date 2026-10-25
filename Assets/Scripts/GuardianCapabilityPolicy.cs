using System;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuardianSimulationMode { LegacyStory, Physics }
    /// <summary>DirectConcurrent: smooth obstacle-aware routes, robots move together and yield per
    /// floor cell. SingleTrackAisle: the original x=-1 aisle lease (kept for regression suites).</summary>
    public enum GuardianTrafficStyle { DirectConcurrent, SingleTrackAisle }

    [Serializable]
    public sealed class GuardianSimulationSettings
    {
        public int Seed=2026;
        [Min(.1f)] public float MinimumTaskInterval=18;
        [Min(.1f)] public float MaximumTaskInterval=30;
        [Min(0)] public float MaximumDepartureDelay=3;
        [Min(1)] public int MaximumOpenTasks=24;
        public bool RandomFaults=true;
        public bool VerboseRuntimeLogs=true;
        [Min(1)] public float MinimumFaultInterval=35;
        [Min(1)] public float MaximumFaultInterval=60;
        [Range(.0001f,.9999f)] public float WarningThreshold=.30f;
        [Range(0,1)] public float LimitedThreshold=.70f;
        [Min(.2f)] public float MealSeconds=8;
        public bool CollectEmptyOnDelivery=true;
        [Min(.2f)] public float MotorAcceleration=6;
        [Min(.1f)] public float MaximumSpeed=1.9f;
        [Range(1,2)] public int MotionSpeedMultiplier=2;
        public GuardianTrafficStyle Traffic=GuardianTrafficStyle.DirectConcurrent;
        /// <summary>Demo pacing: seeded orders arrive at 35% of the configured interval so several robots work at once.</summary>
        public bool BusyOrders=true;
        // Guests who walk loops round the shop forever; off for the demo, where diners come and go instead.
        public bool WalkingGuests=false;
        /// <summary>Seated diners who eat, pay at the self-checkout, leave and are replaced (GuardianDinerFlow).</summary>
        public bool Diners=true;
        /// <summary>
        /// Keep serving while lamps change (live supervision): a yellow lamp only takes liquids away, so the robot is not
        /// stopped and keeps carrying food and clearing; back to green, soup stays off until a person confirms. Random
        /// condition events make only one robot worse at a time. Off in recorded verifications (the decision card's
        /// stop-and-ask flow is what they check).
        /// </summary>
        public bool KeepServing=true;
        public bool PedestrianDemoEnabled=false;
        public float PedestrianSpeed=.8f,PedestrianFirstCrossing=6,PedestrianCrossingInterval=24,PedestrianSafetyMargin=.25f,PedestrianClearSeconds=.6f;
        public float OrderIntervalScale=>BusyOrders?.35f:1f;
        public GuardianPhysicalSettings Physical=new GuardianPhysicalSettings();
        public void Validate()
        {
            foreach(float value in new[]{MinimumTaskInterval,MaximumTaskInterval,MaximumDepartureDelay,MinimumFaultInterval,MaximumFaultInterval,WarningThreshold,LimitedThreshold,MealSeconds,MotorAcceleration,MaximumSpeed,PedestrianSpeed,PedestrianFirstCrossing,PedestrianCrossingInterval,PedestrianSafetyMargin,PedestrianClearSeconds})
                if(!float.IsFinite(value))throw new ArgumentException("Non-finite Guardian settings");
            if(PedestrianSpeed<=0 || PedestrianFirstCrossing<0 || PedestrianCrossingInterval<1 || PedestrianSafetyMargin<0 || PedestrianClearSeconds<0)throw new ArgumentException("Invalid pedestrian settings");
            if(Physical==null)Physical=new GuardianPhysicalSettings();Physical.Validate();
            if (MinimumTaskInterval<=0 || MaximumTaskInterval<MinimumTaskInterval || MaximumDepartureDelay<0
                || MaximumOpenTasks<1 || MinimumFaultInterval<=0 || MaximumFaultInterval<MinimumFaultInterval
                || WarningThreshold<=0 || LimitedThreshold>1 || LimitedThreshold<=WarningThreshold
                || MealSeconds<.2f || float.IsNaN(MealSeconds) || float.IsInfinity(MealSeconds) || MotorAcceleration<=0 || MaximumSpeed<=0
                || (MotionSpeedMultiplier!=1 && MotionSpeedMultiplier!=2))
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
