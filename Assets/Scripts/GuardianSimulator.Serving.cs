using System;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianSimulator
    {
        private bool progressingService;
        private string mealSecondsText,mealSettingMessage;

        private bool StartSameVisitReturn(GuardianRobot robot,DeliveryTask delivery)
        {
            if(_mode!=GuardianSimulationMode.Physics || Inventory==null || !Settings.CollectEmptyOnDelivery
                || delivery.Kind==DeliveryKind.EmptyDishes || delivery.SameVisitReturnId!=null || !robot.DispatchEnabled
                || !GuardianCapabilityPolicy.Allows(robot.Condition,DeliveryKind.EmptyDishes,robot.EmergencyStopped)) return false;
            if(!robot.PhysicsDriver.AtServicePoint(delivery.Destination)) return false;
            var available=Inventory.AvailableDirty(delivery.TableIndex,delivery.DishIds).Select(i=>i.Id).ToArray();
            if(available.Length==0) return false;

            // Existing, not-yet-assigned return requests can be fulfilled by this
            // visit. Reuse their task IDs rather than leaving source-empty jobs.
            var waiting=_tasks.Where(t=>t.Kind==DeliveryKind.EmptyDishes && t.TableIndex==delivery.TableIndex
                && t.Phase==TaskPhase.Pending && t.AssignedTo==null && t.DishIds.Count==0 && !t.FixedDishBatch)
                .OrderBy(t=>t.CreatedAt).ToArray();
            int batchCount=(available.Length+GuardianDishInventory.TrayCapacity-1)/GuardianDishInventory.TrayCapacity;
            if(waiting.Length>batchCount) return false; // Leave earlier promises with the ordinary dispatcher.
            DeliveryTask first=null;
            for(int offset=0,batchIndex=0;offset<available.Length;offset+=GuardianDishInventory.TrayCapacity,batchIndex++)
            {
                bool adopted=batchIndex<waiting.Length;
                var collection=adopted?waiting[batchIndex]:new DeliveryTask("R-"+(++_generated).ToString("D4"),DeliveryKind.EmptyDishes,
                    delivery.Destination,ReturnStation+Vector3.right*.2f){TableIndex=delivery.TableIndex,CreatedAt=DemoClock};
                collection.ParentDeliveryId=delivery.Id;
                collection.ExcludedDishIds.AddRange(delivery.DishIds);
                string[] ids=available.Skip(offset).Take(GuardianDishInventory.TrayCapacity).ToArray();
                if(!Inventory.ReserveFixedBatch(collection,ids)) throw new InvalidOperationException("Same-visit batch changed during atomic reservation");
                if(!adopted)
                {
                    // These finite batches finish work already materialized at a
                    // table, even when external admission is full. New external
                    // work still obeys MaximumOpenTasks and counts these jobs.
                    _tasks.Add(collection);
                    _requests.Add(new GuardianTaskRequest{Id=_requests.Count+1,Kind=DeliveryKind.EmptyDishes,TableIndex=delivery.TableIndex,
                        ReceivedAt=DemoClock,Task=collection,ParentDeliveryId=delivery.Id});
                }
                foreach(var request in _requests.Where(r=>r.Task==collection))
                {
                    request.ParentDeliveryId=delivery.Id;
                    request.Decision=(batchIndex==0?"Same-visit collection":"Remaining-plate next trip")+" after "+delivery.Id+"; "+ids.Length+" plate(s)";
                }
                collection.PendingReason=batchIndex==0?"At table; waiting for return-counter slot":"Reserved remaining plates for next trip";
                if(first==null) first=collection;
                Log("Linked "+collection.Id+" after "+delivery.Id+"; "+(adopted?"existing request; ":"")+string.Join("/",ids));
            }
            delivery.SameVisitReturnId=first.Id;
            if(ReturningSourceBlocks(first,robot))
            {
                first.PendingReason="Collection queued; yielding table to a returning cargo owner";
                Log(first.PendingReason+" after "+delivery.Id); return false;
            }
            // Keep the occupied local dock; do not enqueue a homeward leg and
            // then make the robot revisit the same table to collect its plates.
            robot.ReleaseTask(false);
            _traffic.ContinueServiceAtDestination(delivery,first);
            robot.AssignAtServicePoint(first);
            Log("Same visit "+robot.RobotId+": delivered "+delivery.Id+", collecting "+first.Id);
            return true;
        }

        private void ProgressSameVisitPickups()
        {
            if(progressingService || _mode!=GuardianSimulationMode.Physics || Inventory==null) return;
            progressingService=true;
            try
            {
                foreach(var robot in _robots.Where(r=>r.CurrentTask!=null && r.CurrentTask.AwaitingServicePickup)
                    .OrderBy(r=>r.CurrentTask.CreatedAt).ThenBy(r=>r.RobotId).ToArray())
                {
                    var task=robot.CurrentTask;
                    if(ReturningSourceBlocks(task,robot))
                    {
                        _traffic.ReleaseJob(task); robot.ReleaseTask(); task.AssignedTo=null; task.Phase=TaskPhase.Pending;
                        task.PendingReason="Collection queued; yielding table to a returning cargo owner";
                        Log(task.Id+": "+task.PendingReason); continue;
                    }
                    if(!robot.DispatchEnabled || !GuardianCapabilityPolicy.Allows(robot.Condition,DeliveryKind.EmptyDishes,robot.EmergencyStopped))
                    { RevokeIneligible(robot); continue; }
                    if(!robot.PhysicsDriver.AtServicePoint(task.Pickup))
                    {
                        if(task.PendingReason!="Waiting at the actual table service point") Log(task.Id+" holding service point: "+robot.PhysicsDriver.MotionState);
                        task.PendingReason="Waiting at the actual table service point"; continue;
                    }
                    if(!_traffic.CanAssign(task))
                    { task.PendingReason="At table; waiting for return-counter slot"; continue; }
                    _traffic.ClaimJob(task); task.PendingReason=null;
                    robot.PickUp();
                    if(robot.Carrying) Log("Same-visit pickup "+task.Id+" by "+robot.RobotId+"; table -> robot -> return counter");
                }
            }
            finally { progressingService=false; }
        }

        private bool ReturningSourceBlocks(DeliveryTask task,GuardianRobot except=null)
        {
            return _robots.Any(r=>r!=except && r.CurrentTask!=null && r.CurrentTask!=task && r.CurrentTask.Phase==TaskPhase.ReturningCargo
                && new[]{task.Pickup,task.Destination}.Any(p=>new Vector2(p.x-r.CurrentTask.Pickup.x,p.z-r.CurrentTask.Pickup.z).sqrMagnitude<.01f));
        }

        public bool TrySetMealSeconds(float seconds)
        {
            if(float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds<.2f) return false;
            Settings.MealSeconds=seconds; mealSecondsText=seconds.ToString("0.##",CultureInfo.InvariantCulture);
            mealSettingMessage="Applies to newly served meals; existing meal timers stay unchanged.";
            Log("Meal duration "+mealSecondsText+"s for new deliveries"); return true;
        }
        private void MealControlsGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Meal time (s)",GUILayout.Width(100));
            if(mealSecondsText==null) mealSecondsText=Settings.MealSeconds.ToString("0.##",CultureInfo.InvariantCulture);
            mealSecondsText=GUILayout.TextField(mealSecondsText,GUILayout.Width(70));
            if(ControlButton("meal-apply","Apply for new meals"))
            {
                if(!float.TryParse(mealSecondsText,NumberStyles.Float,CultureInfo.InvariantCulture,out float value) || !TrySetMealSeconds(value))
                    mealSettingMessage="Use a meal time of at least 0.2 seconds.";
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(mealSettingMessage??("Food becomes an empty plate after "+Settings.MealSeconds.ToString("0.##")+"s. Tray capacity: "+GuardianDishInventory.TrayCapacity+" plates."));
        }
    }
}
