using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianSimulator
    {
        public bool SensorLinked=>GetComponent<GuardianNetworkBridge>()?.HasSnapshot==true;

        internal bool SetLinkedCondition(string id,RobotCondition condition)
        {
            float value=condition==RobotCondition.Normal?Settings.WarningThreshold*.4f:
                condition==RobotCondition.Warning?(Settings.WarningThreshold+Settings.LimitedThreshold)*.5f:
                Mathf.Lerp(Settings.LimitedThreshold,1,.5f);
            NoteLinkedEvidence(id);
            return SubmitCalibratedEvidence(id,value,Settings.WarningThreshold,Settings.LimitedThreshold,false);
        }

        internal void SetLinkedEmergencyStop(string id,bool stopped)
        {
            GuardianRobot robot=_robots.Find(r=>r.RobotId==id);
            if (robot==null || robot.LinkedStopped==stopped) return;
            robot.SetLinkedStop(stopped);
            Log(id+(stopped?" sensor hold":" sensor hold released")+(robot.OperatorStopped?"; operator STOP remains":""));
            RevokeIneligible(robot);
            Replan();
        }
    }
}
