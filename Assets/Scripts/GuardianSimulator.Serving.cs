using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianSimulator
    {
        private bool progressingService;
        private string mealSecondsText,mealSettingMessage;

        /// <summary>
        /// The guest who ordered this meal left before it was picked up: take the order back, so no robot puts it in front of
        /// an empty chair. Only while the meal is still on the kitchen pass, and never an order a person has a say in
        /// (held, handed over, or sent to a named robot). Off in recorded verifications (KeepServing).
        /// </summary>
        public bool WithdrawOrder(DeliveryTask task,string reason)
        {
            if(!Settings.KeepServing || task==null || task.Kind==DeliveryKind.EmptyDishes || task.Withdrawn) return false;
            if(task.Phase!=TaskPhase.Pending && task.Phase!=TaskPhase.ToPickup) return false;   // already on a tray
            if(task.AwaitingServicePickup || !string.IsNullOrEmpty(task.OperatorHoldReason) || !string.IsNullOrEmpty(task.RequestedRobotId)
                || !string.IsNullOrEmpty(task.HandedOverBy) || Inventory==null || !Inventory.StillInKitchen(task)) return false;
            var robot=task.AssignedTo;
            if(robot!=null)
            {
                if(robot.CurrentTask!=task || robot.Carrying) return false;
                robot.ReleaseTask();
                if(_mode==GuardianSimulationMode.Physics) _traffic.ReleaseJob(task);
            }
            Inventory.Discard(task);
            task.AssignedTo=null; task.Withdrawn=true; task.PendingReason=reason;
            task.Phase=TaskPhase.Completed; task.CompletedAt=DemoClock;
            Log(task.Id+" withdrawn: "+reason);
            Replan();
            return true;
        }

        private bool StartSameVisitReturn(GuardianRobot robot,DeliveryTask delivery)
        {
            if(_mode!=GuardianSimulationMode.Physics || Inventory==null || !Settings.CollectEmptyOnDelivery
                || delivery.Kind==DeliveryKind.EmptyDishes || delivery.SameVisitReturnId!=null || !robot.DispatchEnabled
                || !AllowsWork(robot,DeliveryKind.EmptyDishes)) return false;
            if(!robot.PhysicsDriver.AtServicePoint(delivery.Destination)) return false;
            // Live demo (KeepServing): with both places at the return counter taken, the robot does not stay at the table holding its
            // service point (it blocks the aisle the robot at the counter needs); it leaves, and the dishes are sent for
            // by the ordinary clearing job once the counter is free.
            Vector3 returns=ReturnStation+Vector3.right*.2f;
            if(Settings.KeepServing && !FreeReturnPoint(out returns)) return false;
            var available=Inventory.AvailableDirty(delivery.TableIndex,delivery.DishIds).Select(i=>i.Id).ToArray();
            if(available.Length==0) return false;

            // Existing, not-yet-assigned return requests can be fulfilled by this
            // visit. Reuse their task IDs rather than leaving source-empty jobs.
            var waiting=_tasks.Where(t=>t.Kind==DeliveryKind.EmptyDishes && t.TableIndex==delivery.TableIndex
                && t.Phase==TaskPhase.Pending && t.AssignedTo==null && t.DishIds.Count==0 && !t.FixedDishBatch)
                .OrderBy(t=>t.CreatedAt).ToArray();
            // Do not discard a protected promise and create a new batch from
            // the same source instead. Only the human may release its hold/pin.
            if(waiting.Any(t=>!OperatorAllowsAssignment(t,robot))) return false;
            int batchCount=(available.Length+GuardianDishInventory.TrayCapacity-1)/GuardianDishInventory.TrayCapacity;
            if(waiting.Length>batchCount) return false; // Leave earlier promises with the ordinary dispatcher.
            DeliveryTask first=null;
            for(int offset=0,batchIndex=0;offset<available.Length;offset+=GuardianDishInventory.TrayCapacity,batchIndex++)
            {
                bool adopted=batchIndex<waiting.Length;
                var collection=adopted?waiting[batchIndex]:new DeliveryTask("R-"+(++_generated).ToString("D4"),DeliveryKind.EmptyDishes,
                    delivery.Destination,returns){TableIndex=delivery.TableIndex,CreatedAt=DemoClock};
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
            if(!OperatorAllowsAssignment(first,robot)) { first.PendingReason=OperatorWaitReason(first); return false; }
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
                    if(!OperatorAllowsAssignment(task,robot))
                    { task.PendingReason=OperatorWaitReason(task); continue; }
                    if(ReturningSourceBlocks(task,robot))
                    {
                        _traffic.ReleaseJob(task); robot.ReleaseTask(); task.AssignedTo=null; task.Phase=TaskPhase.Pending;
                        task.PendingReason="Collection queued; yielding table to a returning cargo owner";
                        Log(task.Id+": "+task.PendingReason); continue;
                    }
                    if(!robot.DispatchEnabled || !AllowsWork(robot,DeliveryKind.EmptyDishes))
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

        // ---- patrol (live demo) ----
        // With nothing waiting to be done, one healthy robot drives slowly round the dining room instead of standing at its
        // bay, so it is already out on the floor when the next job comes. It goes there as its way "home", so it stays free to
        // be sent (DirectHomeward), and it goes back to its own bay once it has a job.
        private static readonly Vector3[] PatrolPoints = { new Vector3(1.6f, .34f, -1.7f), new Vector3(1.6f, .34f, 1.7f), new Vector3(-1.2f, .34f, 0f) };
        private readonly Dictionary<GuardianRobot, Vector3> _bays = new Dictionary<GuardianRobot, Vector3>();
        private GuardianRobot _patroller;
        private float _nextPatrol;
        private int _patrolIndex;
        private void Patrol()
        {
            if (_mode != GuardianSimulationMode.Physics || !Settings.KeepServing || ReserveFleet || Diners == null || !Diners.Running || !_traffic.DirectActive) return;
            if (DemoClock < _nextPatrol) return;
            _nextPatrol = DemoClock + 4;
            if (_patroller != null && !_robots.Contains(_patroller)) { _patroller = null; _bays.Clear(); }  // the fleet was reset
            if (_patroller != null && (_patroller.CurrentTask != null || _patroller.EmergencyStopped || _patroller.Condition != RobotCondition.Normal))
            { _patroller.PhysicsDriver.SetHome(_bays[_patroller]); _patroller = null; }
            if (_patroller != null && !_patroller.IsIdle) return;  // still on its way
            if (_tasks.Any(t => t.Phase == TaskPhase.Pending)) return;  // work is waiting: no patrol
            var robot = _patroller ?? _robots.FirstOrDefault(r => r.IsIdle && r.DispatchEnabled && !r.EmergencyStopped && r.Condition == RobotCondition.Normal);
            if (robot == null) return;
            if (!_bays.ContainsKey(robot)) _bays[robot] = robot.PhysicsDriver.Home;
            _patroller = robot;
            robot.PhysicsDriver.SetHome(PatrolPoints[_patrolIndex++ % PatrolPoints.Length]);
            robot.PhysicsDriver.ReturnHome();
        }

        /// <summary>A place at the return counter that no job holds (the first, then the second), for the live demo.</summary>
        private bool FreeReturnPoint(out Vector3 point)
        {
            foreach(Vector3 place in new[]{ReturnStation,ReturnStationSecond})
            { point=place+Vector3.right*.2f; if(_traffic.ServicePointFree(point)) return true; }
            point=ReturnStation+Vector3.right*.2f; return false;
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
