using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianSimulator
    {
        private GuardianInterventionExperience humanExperience;
        public bool OperatorSupervisionEnabled=true;
        public GuardianInterventionExperience HumanExperience=>humanExperience;
        public bool HumanExperienceActive=>humanExperience!=null && humanExperience.Active;
        internal void NotifyDemoInputUnavailable(GuardianRobot robot,string reason)
        {
            EnsureLiveSupervision();
            if(HumanExperienceActive)humanExperience.OnInputUnavailable(robot,reason);
        }
        private void EnsureLiveSupervision()
        {
            if(_mode!=GuardianSimulationMode.Physics || !OperatorSupervisionEnabled || HumanExperienceActive) return;
            if(humanExperience==null)
            { humanExperience=GetComponent<GuardianInterventionExperience>()??gameObject.AddComponent<GuardianInterventionExperience>(); humanExperience.Initialize(this); }
            humanExperience.BeginLive(); _showTelemetry=true;
        }
        public GuardianInterventionExperience EnterHumanExperience(bool guided,bool five)
        {
            if(humanExperience==null)
            { humanExperience=GetComponent<GuardianInterventionExperience>()??gameObject.AddComponent<GuardianInterventionExperience>(); humanExperience.Initialize(this); }
            _showTelemetry=true; humanExperience.Begin(guided,five); return humanExperience;
        }
        internal bool AllowsWork(GuardianRobot robot,DeliveryKind kind)=>GuardianCapabilityPolicy.Allows(robot.Condition,kind,robot.EmergencyStopped)
            && (!HumanExperienceActive || humanExperience.Permits(robot,kind));
        internal static bool OperatorAllowsAssignment(DeliveryTask task,GuardianRobot robot)=>string.IsNullOrEmpty(task.OperatorHoldReason)
            && (task.RequestedRobotId==null || task.RequestedRobotId==robot.RobotId) && task.HandedOverBy!=robot.RobotId;
        internal static string OperatorWaitReason(DeliveryTask task)=>!string.IsNullOrEmpty(task.OperatorHoldReason)
            ?task.OperatorHoldReason:task.RequestedRobotId!=null?"Waiting for requested robot "+task.RequestedRobotId
            :"Handed over by robot "+task.HandedOverBy+"; waiting for another robot";
        private bool ExperienceInput()
        {
            if(!HumanExperienceActive) return false;
            if(Input.GetKeyDown(KeyCode.R)) { humanExperience.Restart(); return true; }
            for(int i=0;i<_robots.Count;i++) if(Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1+i))) humanExperience.SelectedActor=_robots[i].RobotId;
            if(Input.GetKeyDown(KeyCode.E)||Input.GetKeyDown(KeyCode.N)||Input.GetKeyDown(KeyCode.W)||Input.GetKeyDown(KeyCode.L)) humanExperience.RejectShortcut();
            return true;
        }
    }
}
