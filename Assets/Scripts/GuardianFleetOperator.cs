using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuardianFleetRole { Active, Retiring, Standby }
    public enum GuardianCommandState { Accepted, Executing, Completed, Blocked }
    [Serializable]
    public sealed class GuardianOperatorCommand
    {
        public int Id;
        public string Action,RobotId,Reason;
        public GuardianCommandState State;
        public float AcceptedAt,CompletedAt;
        public bool Terminal;
    }

    /// <summary>Human fleet commands preserve physical position, cargo and assignment identity.</summary>
    public sealed class GuardianFleetOperator : MonoBehaviour
    {
        private GuardianSimulator sim;
        private readonly List<GuardianOperatorCommand> commands=new List<GuardianOperatorCommand>();
        public IReadOnlyList<GuardianOperatorCommand> Commands=>commands;
        public void ResetOperator(GuardianSimulator simulator)
        {
            sim=simulator; commands.Clear();
            foreach (var robot in sim.Robots)
            {
                robot.FleetRole=robot.RobotId=="D"||robot.RobotId=="E"?GuardianFleetRole.Standby:GuardianFleetRole.Active;
                robot.DispatchEnabled=robot.FleetRole==GuardianFleetRole.Active;
            }
        }
        private GuardianOperatorCommand New(string action,string id)
        {
            var c=new GuardianOperatorCommand{Id=commands.Count+1,Action=action,RobotId=id,State=GuardianCommandState.Accepted,AcceptedAt=sim.DemoClock,Reason="Accepted"};
            commands.Add(c); Debug.Log("[Guardian] Operator #"+c.Id+" accepted "+action+" "+id); return c;
        }
        private static GuardianOperatorCommand Reject(GuardianOperatorCommand c,string why)
        { c.State=GuardianCommandState.Blocked; c.Reason=why; c.Terminal=true; Debug.Log("[Guardian] Operator #"+c.Id+" BLOCKED "+c.Action+" "+c.RobotId+": "+why); return c; }
        public GuardianOperatorCommand Retire(string id)
        {
            var c=New("Retire",id); var robot=sim.Robots.FirstOrDefault(r=>r.RobotId==id);
            if (!sim.ReserveFleet || robot==null) return Reject(c,"Select the optional 3+2 fleet and a valid actor");
            if (robot.FleetRole==GuardianFleetRole.Retiring) return Reject(c,"Retirement already in progress; original command retained");
            if (robot.FleetRole==GuardianFleetRole.Standby && robot.IsIdle) return Reject(c,"Already in standby at its physical bay");
            robot.FleetRole=GuardianFleetRole.Retiring; sim.SetDispatchEnabled(robot,false);
            if (robot.CurrentTask==null) robot.PhysicsDriver.ReturnHome();
            return c;
        }
        public GuardianOperatorCommand Deploy(string id=null)
        {
            var c=New("Deploy",id??"next spare");
            if (!sim.ReserveFleet) return Reject(c,"Select the optional 3+2 fleet first");
            var robot=id==null?sim.Robots.FirstOrDefault(r=>r.FleetRole==GuardianFleetRole.Standby && r.Condition==RobotCondition.Normal && !r.EmergencyStopped):sim.Robots.FirstOrDefault(r=>r.RobotId==id);
            if (robot==null) return Reject(c,"No healthy standby robot available");
            c.RobotId=robot.RobotId;
            if (robot.FleetRole!=GuardianFleetRole.Standby) return Reject(c,"Robot is already active or retiring");
            if (robot.EmergencyStopped || robot.Condition!=RobotCondition.Normal) return Reject(c,"Standby robot needs physical-stop recovery and Normal capability");
            if (!robot.IsIdle || Vector3.Distance(robot.PhysicsDriver.Body.position,robot.PhysicsDriver.Home)>.1f) return Reject(c,"Standby robot has not physically reached its bay (idle="+robot.IsIdle+", distance="+Vector3.Distance(robot.PhysicsDriver.Body.position,robot.PhysicsDriver.Home).ToString("F4")+")");
            robot.FleetRole=GuardianFleetRole.Active; sim.SetDispatchEnabled(robot,true); return c;
        }
        public GuardianOperatorCommand RecoverToStandby(string id)
        {
            var c=New("Recover to standby",id); var robot=sim.Robots.FirstOrDefault(r=>r.RobotId==id);
            if (!sim.ReserveFleet || robot==null) return Reject(c,"Select the optional 3+2 fleet and a valid actor");
            // Explicit human recovery clears the independent immobilization flag.
            // It never re-enables dispatch; the body must return before standby.
            sim.SetDispatchEnabled(robot,false); robot.FleetRole=GuardianFleetRole.Retiring;
            sim.SetCondition(id,RobotCondition.Normal); sim.SetEmergencyStop(id,false);
            if (robot.CurrentTask==null) robot.PhysicsDriver.ReturnHome();
            return c;
        }
        public void Tick()
        {
            if (!sim.ReserveFleet) return;
            foreach (var c in commands.Where(c=>!c.Terminal))
            {
                var robot=sim.Robots.First(r=>r.RobotId==c.RobotId);
                if (c.Action=="Deploy")
                {
                    if (robot.FleetRole!=GuardianFleetRole.Active) { Reject(c,"Deployment superseded by a retirement command"); continue; }
                    c.State=GuardianCommandState.Executing; c.Reason=robot.CurrentTask==null?"Active; waiting for eligible work / free service point":"Physically departing standby bay";
                    if (Vector3.Distance(robot.PhysicsDriver.Body.position,robot.PhysicsDriver.Home)>.75f) Complete(c,"Physically departed; dispatch active");
                }
                else
                {
                    if (robot.EmergencyStopped) { c.State=GuardianCommandState.Blocked; c.Reason="Immobilized: recover mobility before physical cargo return / retirement"; continue; }
                    c.State=GuardianCommandState.Executing;
                    c.Reason=robot.Carrying?"Returning owned dish to its actual source":robot.CurrentTask!=null?"Releasing assignment":"Moving to own standby bay";
                    if (robot.CurrentTask==null && robot.IsIdle && Vector3.Distance(robot.PhysicsDriver.Body.position,robot.PhysicsDriver.Home)<.1f)
                    { robot.FleetRole=GuardianFleetRole.Standby; Complete(c,"Physically parked; dispatch disabled; standby"); }
                }
            }
        }
        private void Complete(GuardianOperatorCommand c,string why)
        { c.State=GuardianCommandState.Completed; c.Reason=why; c.Terminal=true; c.CompletedAt=sim.DemoClock; Debug.Log("[Guardian] Operator #"+c.Id+" completed "+c.Action+" "+c.RobotId); }
    }
}
