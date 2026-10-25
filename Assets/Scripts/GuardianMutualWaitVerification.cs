using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed class GuardianMutualWaitVerification : MonoBehaviour
    {
        [Serializable]private sealed class Report
        {
            public string status="FAIL",error="",scope="Actual route/cargo, operator KeepStopped, swept NPC retreat and ACK; no teleport or reset after initial BeginDemo; no GUI/hardware";
            public string dishId,taskId;public int dishInstance,phase;public float clock,stoppedZ,retreatDistance,maxPenetration,minPhysicalGap=float.MaxValue;
            public bool unsafeAckRefused,backwardObstacleStoppedRetreat,sameTaskAndDishAtResume,ackAccepted,originalRobotCompleted;public double realSeconds;
        }
        public bool Done {get;private set;}public bool Passed=>report.status=="PASS";public object ExportReport=>report;
        private readonly Report report=new Report();private GuardianInteractiveExperience experience;private GuardianSimulator sim;private GuardianRobot a;private DeliveryTask task;
        private float at,blockedX;private GameObject backWall;private double began;
        private IEnumerator Start()
        {
            began=Time.realtimeSinceStartupAsDouble;experience=GetComponent<GuardianInteractiveExperience>();while(!experience.Ready)yield return null;
            sim=experience.Sim;experience.BeginDemo();a=sim.Robots.Single(r=>r.RobotId=="A");task=sim.Tasks.Single(t=>t.RequestedRobotId=="A");
            report.taskId=task.Id;report.dishId=task.DishIds.Single();report.dishInstance=sim.Inventory.Items[report.dishId].GetInstanceID();Time.timeScale=8;sim.PhysicsStepBeginning+=Tick;
        }
        private void Check(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);}
        private void Tick(float clock)
        {
            if(Done)return;try
            {
                report.clock=clock;var person=experience.Person;var motor=a.PhysicsDriver;var hold=a.GetComponent<GuardianMotionInterlock>();var dish=sim.Inventory.Items[report.dishId];
                foreach(var r in sim.Robots)
                {
                    var d=r.PhysicsDriver;if(Physics.ComputePenetration(d.Shape,d.Body.position,d.Body.rotation,person.Shape,person.Body.position,person.Body.rotation,out var axis,out float depth))report.maxPenetration=Mathf.Max(report.maxPenetration,depth);
                    Vector3 p=person.Body.position+person.Shape.center,v=d.Shape.ClosestPoint(p)-p;v.y=0;report.minPhysicalGap=Mathf.Min(report.minPhysicalGap,v.magnitude-person.Shape.radius);
                }
                Check(report.maxPenetration<.001f,"NPC/robot penetration during mutual-wait recovery");Check(dish.GetInstanceID()==report.dishInstance,"Original dish identity lost");
                if(report.phase==0 && a.Carrying && Mathf.Abs(motor.Body.position.x+1)<.15f && motor.PlanarVelocity.z<-.6f)
                {
                    float v=-motor.PlanarVelocity.z,travel=v*motor.Dynamics.Assumptions.DriverResponseDelay+v*v/(2*motor.Dynamics.BrakeAcceleration);
                    if(motor.Body.position.z<=-1.9f+travel+.03f)
                    {experience.Select(a.GetComponent<GuardianInteractionTarget>());Check(experience.Command(GuardianHumanCommand.KeepStopped,experience.SelectionVersion),"Operator KeepStopped refused");report.phase=1;at=clock;}
                }
                else if(report.phase==1 && motor.PlanarVelocity.magnitude<.005f && motor.Body.angularVelocity.magnitude<.005f)
                {report.stoppedZ=motor.Body.position.z;Check(Mathf.Abs(report.stoppedZ+1.9f)<.5f,"Natural bounded stop missed crossing fixture");person.Walk();report.phase=2;at=clock;}
                else if(report.phase==2 && person.WaitingForClearance && (hold.Current&GuardianMotionHold.Pedestrian)!=0)
                {
                    Check(a.CurrentTask==task && dish.OwnerKey=="A" && a.Carrying,"Mutual wait changed original cargo/task");
                    report.unsafeAckRefused=!experience.ResumeSafety(a);Check(report.unsafeAckRefused,"Mutual-wait ACK accepted before clearance");blockedX=person.Body.position.x;
                    backWall=GameObject.CreatePrimitive(PrimitiveType.Cube);backWall.name="Temporary blocked retreat test fixture";backWall.transform.position=person.Body.position+new Vector3(-.55f,1,0);backWall.transform.localScale=new Vector3(.1f,2,.8f);Physics.SyncTransforms();
                    person.Retreat();report.phase=3;at=clock;
                }
                else if(report.phase==3 && clock-at>=.4f)
                {
                    report.backwardObstacleStoppedRetreat=person.WaitingForClearance && Mathf.Abs(person.Body.position.x-blockedX)<.001f;
                    Check(report.backwardObstacleStoppedRetreat,"NPC retreated into occupied backward space");
                    Check(!Physics.ComputePenetration(person.Shape,person.Body.position,person.Body.rotation,backWall.GetComponent<BoxCollider>(),backWall.transform.position,backWall.transform.rotation,out var axis,out float depth),"Backward obstacle physically penetrated");
                    backWall.SetActive(false);Destroy(backWall);report.phase=4;at=clock;
                }
                else if(report.phase==4 && !person.Crossing && person.Body.position.x<=person.StartPoint.x+.02f && hold.CanRelease(clock))
                {
                    report.retreatDistance=blockedX-person.Body.position.x;report.ackAccepted=experience.ResumeSafety(a);Check(report.ackAccepted,"Clear retreat did not permit guarded ACK");
                    report.sameTaskAndDishAtResume=a.CurrentTask==task && dish.OwnerKey=="A" && a.Carrying;Check(report.sameTaskAndDishAtResume,"ACK did not preserve original task/cargo");report.phase=5;at=clock;
                }
                else if(report.phase==5 && task.Phase==TaskPhase.Completed)
                {report.originalRobotCompleted=task.AssignedTo==a && dish.Owner==GuardianDishOwner.Table;Check(report.originalRobotCompleted,"Original delivery failed after normal recovery");Finish(null);}
                if(clock-at>100 || clock>200)throw new TimeoutException("Mutual wait phase "+report.phase+"; "+motor.MotionState);
            }catch(Exception ex){Finish(ex);}
        }
        private void Finish(Exception ex){if(Done)return;report.status=ex==null?"PASS":"FAIL";report.error=ex?.ToString()??"";report.realSeconds=Time.realtimeSinceStartupAsDouble-began;Done=true;sim.PhysicsStepBeginning-=Tick;Time.timeScale=1;if(ex!=null)Debug.LogException(ex);Debug.Log("GUARDIAN_MUTUAL_WAIT "+report.status);}
        private void OnDestroy(){if(sim!=null)sim.PhysicsStepBeginning-=Tick;}
    }
}
