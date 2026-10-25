using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private GuardianDishItem TableFixture(int table,bool dirty)
        {
            var type=typeof(GuardianDishInventory); const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var item=(GuardianDishItem)type.GetMethod("Create",flags).Invoke(sim.Inventory,new object[]{GuardianMenu.CurryRice});
            item.Dirty=dirty; item.DirtyAt=dirty?0:sim.DemoClock+600;
            type.GetMethod("Place",flags).Invoke(sim.Inventory,new object[]{item,GuardianDishOwner.Table,table.ToString(),null});
            type.GetMethod("RebuildVisual",flags).Invoke(sim.Inventory,new object[]{item}); return item;
        }
        private void ClearTableFixture(int table)
        {
            foreach(var item in sim.Inventory.Items.Values.Where(i=>i.Owner==GuardianDishOwner.Table && i.OwnerKey==table.ToString()).ToArray())
                typeof(GuardianDishInventory).GetMethod("Place",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(sim.Inventory,new object[]{item,GuardianDishOwner.ReturnCounter,"Return",null});
        }
        private void OnlyActorA()
        { foreach(var robot in sim.Robots) robot.DispatchEnabled=robot.RobotId=="A"; }
        private void StartServeCase(string name)
        {
            Reset(2026,true); sim.Settings.MealSeconds=600;
            clearanceCase=name;
            report.Add("SERVE_CASE "+name+" collectOnDelivery=true mealSeconds="+sim.Settings.MealSeconds);
        }
        private bool Drained()=>sim.Tasks.All(t=>t.Phase==TaskPhase.Completed) && sim.Robots.All(r=>r.IsIdle)
            && sim.Traffic.Reservations.Count==0 && sim.Traffic.JobClaimCount==0 && sim.Traffic.QueueLength==0;
        private void SaveServeTrace(string name)
        {
            sim.Inventory.ValidateOwnership();
            Func<string,string> csv=value=>"\""+value.Replace("\"","\"\"")+"\"";
            var lines=sim.Inventory.Events.Select(e=>string.Join(",",new[]{e.Clock.ToString("F4",CultureInfo.InvariantCulture),e.Action,e.Id,
                e.From.ToString(),e.FromKey??"",e.To.ToString(),e.ToKey??"",e.Dirty.ToString(),e.TaskId??"",e.Position.ToString("F4"),
                e.InstanceId.ToString(),e.ParentName,e.Active.ToString(),e.RenderVisible.ToString()}.Select(csv)));
            File.WriteAllLines(Path.Combine(output,name+"-dish-events.csv"),new[]{"sim_seconds,action,dish,from,from_key,to,to_key,dirty,task,world_position,instance,parent,active,render_visible_cpu"}.Concat(lines));
            Check(sim.Inventory.Items.Values.All(i=>i.Owner!=GuardianDishOwner.Robot && string.IsNullOrEmpty(i.ReservedBy)),name+": no carried or reserved dishes after drain");
        }
        private void CheckMealAtTable(DeliveryTask meal)
        {
            var item=sim.Inventory.Items[meal.DishIds.Single()];
            var renderers=item.GetComponentsInChildren<Renderer>();
            Check(item.Owner==GuardianDishOwner.Table && item.OwnerKey==meal.TableIndex.ToString() && item.gameObject.activeInHierarchy
                && renderers.Length>0 && renderers.All(r=>r.enabled && r.sharedMaterial!=null),"served identity has active mesh/materials at its table (CPU check, not pixels)");
            Check(renderers.Min(r=>r.bounds.min.y)>item.transform.parent.position.y+.80f,"served mesh bounds lie above table top (CPU geometry)");
        }
        private Collider CounterBarrier()
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name="Test blocked return counter approach";
            go.transform.position=new Vector3(-5.6f,1,-3.6f); go.transform.localScale=new Vector3(.2f,2,1.4f);
            var collider=go.GetComponent<Collider>(); temporaryObstacles.Add(collider); Physics.SyncTransforms(); return collider;
        }
        private void RemoveCounterBarrier(Collider barrier)
        { temporaryObstacles.Remove(barrier); barrier.enabled=false; Destroy(barrier.gameObject); Physics.SyncTransforms(); }

        private IEnumerator ServeClearRegression()
        {
            StartServeCase("no-empty"); OnlyActorA(); ClearTableFixture(0);
            var meal=sim.QueueTask(DeliveryKind.Soup,0);
            yield return Wait(()=>meal.Phase==TaskPhase.Completed,180,"ordinary meal reaches table with no empty plates");
            if(failed) yield break;
            CheckMealAtTable(meal);
            yield return Wait(Drained,180,"no-empty ordinary serving parks");
            Check(meal.SameVisitReturnId==null && sim.Tasks.Count==1,"no unnecessary return task when table has no dirty dishes"); SaveServeTrace("no-empty");

            StartServeCase("mixed"); OnlyActorA();
            string[] dirty={sim.Inventory.AvailableDirty(0).Single().Id,TableFixture(0,true).Id};
            string[] eating={TableFixture(0,false).Id,TableFixture(0,false).Id};
            meal=sim.QueueTask(DeliveryKind.Soup,0);
            yield return Wait(()=>meal.Phase==TaskPhase.Completed && meal.SameVisitReturnId!=null,180,"delivery transitions into same-visit clearing");
            if(failed) yield break;
            var collection=sim.Tasks.Single(t=>t.Id==meal.SameVisitReturnId);
            Check(collection.DishIds.OrderBy(id=>id).SequenceEqual(dirty.OrderBy(id=>id)) && collection.AssignedTo==meal.AssignedTo,
                "only explicit old empty dishes go to the same serving actor");
            Check(collection.Phase==TaskPhase.ToDropoff && collection.FirstAssignedAt==meal.CompletedAt,"same dock pickup occurs before a homeward leg");
            Check(eating.Concat(meal.DishIds).All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Table && !sim.Inventory.Items[id].Dirty),"eating and newly served meals remain at table");
            CheckMealAtTable(meal);
            foreach(string id in dirty)
            {
                var pickup=sim.Inventory.Events.Single(e=>e.Id==id && e.From==GuardianDishOwner.Table && e.To==GuardianDishOwner.Robot);
                Check(Mathf.Abs(pickup.Clock-meal.CompletedAt)<.021f,"empty pickup occurs at the physical delivery visit "+id);
            }
            yield return Wait(Drained,300,"same-visit batch reaches return counter and parks");
            Check(dirty.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.ReturnCounter),"collected old dishes reach return counter"); SaveServeTrace("mixed");

            StartServeCase("seven-plates"); OnlyActorA();
            for(int i=0;i<6;i++) TableFixture(0,true);
            sim.Settings.MaximumOpenTasks=1;
            dirty=sim.Inventory.AvailableDirty(0).Select(i=>i.Id).ToArray(); meal=sim.QueueTask(DeliveryKind.Soup,0);
            yield return Wait(()=>meal.Phase==TaskPhase.Completed,180,"seven-plate delivery completes"); if(failed) yield break;
            collection=sim.Tasks.Single(t=>t.Id==meal.SameVisitReturnId);
            var overflow=sim.Tasks.Single(t=>t.ParentDeliveryId==meal.Id && t!=collection);
            Check(collection.DishIds.Count==4 && overflow.DishIds.Count==3 && overflow.Phase==TaskPhase.Pending,"tray holds four; three remaining plates have a reserved next-trip task");
            Check(overflow.DishIds.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Table && sim.Inventory.Items[id].ReservedBy==overflow.Id),"overflow remains visible at table and reserved to its own batch");
            Check(sim.QueueTask(DeliveryKind.EmptyDishes,0)==overflow && sim.Requests.Last().Coalesced,"new return request joins the reserved next trip without a duplicate job");
            Check(sim.QueueTask(DeliveryKind.Food,1)==null,"required cleanup keeps its finite batches while new external work respects admission capacity");
            yield return Wait(Drained,700,"both finite batches return all seven plates");
            Check(dirty.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.ReturnCounter),"all seven unique plates returned"); SaveServeTrace("seven-plates");

            StartServeCase("adopt-pending"); OnlyActorA();
            meal=sim.QueueTask(DeliveryKind.Soup,0); var pending=sim.QueueTask(DeliveryKind.EmptyDishes,0);
            Check(pending.Phase==TaskPhase.Pending && pending.DishIds.Count==0,"independent return waits behind delivery dock claim");
            yield return Wait(()=>meal.Phase==TaskPhase.Completed,180,"adoption delivery reaches table"); if(failed) yield break;
            Check(meal.SameVisitReturnId==pending.Id && sim.Tasks.Count==2 && pending.FixedDishBatch,"existing unpicked request is adopted without a duplicate source-empty job");
            yield return Wait(Drained,300,"adopted request physically completes"); SaveServeTrace("adopt-pending");

            foreach(bool sourceReturn in new[]{false,true})
            {
                string label=sourceReturn?"yield-source-return":"counter-busy"; StartServeCase(label);
                var independent=sim.QueueTask(DeliveryKind.EmptyDishes,sourceReturn?0:1); var other=independent.AssignedTo;
                Check(other!=null && other.RobotId!="A","independent return reserved to another actor"); if(failed) yield break;
                string reserved=independent.DishIds.Single();
                dirty=sim.Inventory.AvailableDirty(0).Select(i=>i.Id).Concat(new[]{TableFixture(0,true).Id,TableFixture(0,true).Id}).ToArray(); var eatingItem=TableFixture(0,false);
                if(!sourceReturn) Check(sim.TrySetMealSeconds(.2f),"meal time adjustable for newly served food");
                float configured=sim.Settings.MealSeconds;
                Check(!sim.TrySetMealSeconds(0) && !sim.TrySetMealSeconds(float.NaN) && !sim.TrySetMealSeconds(float.PositiveInfinity)
                    && sim.Settings.MealSeconds==configured,"invalid meal durations leave setting unchanged");
                meal=sim.QueueTask(DeliveryKind.Soup,0);
                yield return Wait(()=>meal.AssignedTo!=null && independent.Phase==TaskPhase.ToDropoff && other.Carrying
                    && !other.PhysicsDriver.OwnsRoute && other.PhysicsDriver.Body.position.x< -4.1f,180,
                    "counter holder clears the full turn envelope after delivery assignment");
                if(failed) yield break;
                sim.SetEmergencyStop(other.RobotId,true);
                yield return Wait(()=>meal.Phase==TaskPhase.Completed && meal.SameVisitReturnId!=null,240,"delivery waits at its table while another return occupies counter");
                if(failed) yield break;
                collection=sim.Tasks.Single(t=>t.Id==meal.SameVisitReturnId);
                Check((sourceReturn?collection.Phase==TaskPhase.Pending:collection.AwaitingServicePickup)
                    && collection.DishIds.All(id=>dirty.Contains(id)) && sim.Inventory.Items[reserved].OwnerKey==other.RobotId,
                    "other actor's reserved cargo is never stolen; local batch waits for counter");
                if(sourceReturn)
                {
                    Check(collection.Phase==TaskPhase.Pending && !collection.AwaitingServicePickup && collection.AssignedTo==null,
                        "table yields safely when counter holder must return cargo to this source");
                    Check(collection.DishIds.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Table && sim.Inventory.Items[id].ReservedBy==collection.Id),"yielded batch keeps its unique promised sources");
                    sim.SetEmergencyStop(other.RobotId,false);
                }
                else
                {
                    sim.SetCondition("A",RobotCondition.Limited); yield return new WaitForSeconds(.5f);
                    Check(sim.Inventory.Items[meal.DishIds.Single()].Dirty && !collection.DishIds.Contains(meal.DishIds.Single()),"newly delivered ID stays excluded even after its short timer expires while waiting");
                    Check(!eatingItem.Dirty && eatingItem.Owner==GuardianDishOwner.Table,"changing duration leaves an existing eating timer unchanged");
                    sim.SetEmergencyStop(other.RobotId,false);
                }
                yield return Wait(Drained,700,"counter/source-return contention drains without a dock cycle");
                Check(dirty.Append(reserved).All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.ReturnCounter),"all promised plates eventually return exactly once per pickup attempt");
                Check(sim.Inventory.Items[meal.DishIds.Single()].Owner==GuardianDishOwner.Table,"served dish is retained throughout this visit and its follow-up"); SaveServeTrace(label);
            }

            StartServeCase("carrying-retirement"); sim.SetReserveFleet(true); sim.Settings.MealSeconds=600; OnlyActorA();
            dirty=sim.Inventory.AvailableDirty(0).Select(i=>i.Id).ToArray(); meal=sim.QueueTask(DeliveryKind.Soup,0);
            yield return Wait(()=>meal.SameVisitReturnId!=null && sim.Robots.Single(r=>r.RobotId=="A").Carrying
                && Vector3.Distance(sim.Robots.Single(r=>r.RobotId=="A").PhysicsDriver.Body.position,meal.Destination)>2,240,"same-visit empty batch physically leaves table before retirement");
            if(failed) yield break;
            collection=sim.Tasks.Single(t=>t.Id==meal.SameVisitReturnId); var a=sim.Robots.Single(r=>r.RobotId=="A");
            sim.SetEmergencyStop("A",true); var retire=sim.Fleet.Retire("A");
            yield return new WaitForSeconds(2);
            Check(retire.State==GuardianCommandState.Blocked && dirty.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Robot && sim.Inventory.Items[id].OwnerKey=="A"),"blocked retiring collector retains physical empty cargo");
            sim.Fleet.RecoverToStandby("A"); sim.Fleet.Deploy("D");
            yield return Wait(()=>collection.Phase==TaskPhase.Completed && sim.Robots.All(r=>r.IsIdle),700,"recovery returns empties to their table before D completes the linked collection");
            if(failed) yield break;
            Check(collection.AssignmentVersion>=2 && collection.AssignedTo.RobotId=="D" && collection.DishIds.SequenceEqual(dirty),"reassignment preserves fixed empty identities");
            Check(meal.DishIds.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Table),"served food never returns to kitchen with the empty batch");
            foreach(string id in dirty)
            {
                var moves=sim.Inventory.Events.Where(e=>e.Id==id && e.Action=="move").ToArray();
                Check(moves.Any(e=>e.From==GuardianDishOwner.Robot && e.FromKey=="A" && e.To==GuardianDishOwner.Table)
                    && moves.Any(e=>e.From==GuardianDishOwner.Table && e.To==GuardianDishOwner.Robot && e.ToKey=="D")
                    && !moves.Any(e=>e.To==GuardianDishOwner.Kitchen),"returned/reassigned empty follows table-A-table-D-counter, never kitchen "+id);
            }
            Check(a.FleetRole==GuardianFleetRole.Standby && Drained(),"retirement ends physically parked with no claims"); SaveServeTrace("carrying-retirement");
            yield return ServeClearWaitingRecovery();
            report.Add("SERVE_CLEAR_OK: operating physics/ownership traces; no rendered pixels or GUI click acceptance claimed");
        }

        private void CheckUnpickedBatch(DeliveryTask collection,string[] ids,string stage)
        {
            sim.Inventory.ValidateOwnership();
            Check(collection.FixedDishBatch && collection.DishIds.OrderBy(id=>id).SequenceEqual(ids.OrderBy(id=>id))
                && ids.All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Table
                    && sim.Inventory.Items[id].ReservedBy==collection.Id
                    && sim.Tasks.Count(t=>t.Phase!=TaskPhase.Completed && t.DishIds.Contains(id))==1),
                stage+": unpicked plates retain exactly one table owner and one promised batch");
        }

        private IEnumerator ServeClearWaitingRecovery()
        {
            foreach(bool stop in new[]{false,true})
            {
                string label=stop?"waiting-stop-retire":"waiting-retire";
                StartServeCase(label); sim.SetReserveFleet(true); sim.Settings.MealSeconds=600;
                var barrier=CounterBarrier();
                // The counter holder's true source differs from A's table, so
                // source-return yielding does not cancel the intended wait.
                var independent=sim.QueueTask(DeliveryKind.EmptyDishes,1);
                var b=independent.AssignedTo; var a=sim.Robots.Single(r=>r.RobotId=="A");
                Check(b!=null && b.RobotId=="B","B reserves independent return from a different source table");
                if(failed) yield break;
                string[] dirty={sim.Inventory.AvailableDirty(0).Single().Id,TableFixture(0,true).Id};
                var meal=sim.QueueTask(DeliveryKind.Soup,0);
                yield return Wait(()=>meal.Phase==TaskPhase.Completed && meal.SameVisitReturnId!=null,240,
                    "A serves and waits unpicked for the occupied counter");
                if(failed) yield break;
                var collection=sim.Tasks.Single(t=>t.Id==meal.SameVisitReturnId);
                Check(collection.AssignedTo==a && collection.AwaitingServicePickup && !a.Carrying
                    && independent.Phase==TaskPhase.ToDropoff && b.Carrying,"counter wait begins before A takes any old plate");
                CheckUnpickedBatch(collection,dirty,"before command");
                yield return Wait(()=>!b.PhysicsDriver.OwnsRoute && b.PhysicsDriver.Body.position.x< -4.1f,60,
                    "counter holder clears the protected central exit before immobilization");
                if(failed) yield break;
                sim.SetEmergencyStop("B",true);
                Check(collection.AwaitingServicePickup && independent.Phase==TaskPhase.ReturningCargo,
                    "different-source stopped counter holder preserves the waiting state");
                float waitStarted=sim.DemoClock;
                yield return Wait(()=>sim.DemoClock>=waitStarted+(stop?125:2),stop?130:7,
                    "bounded observation of unloaded table wait");
                if(failed) yield break;
                report.Add("WAIT_GUARD_SCOPE case="+label+" elapsed="+(sim.DemoClock-waitStarted).ToString("F2")
                    +" busy="+a.PhysicsDriver.Busy+" progressWait="+a.PhysicsDriver.ProgressWait.ToString("F2")
                    +" awaiting="+collection.AwaitingServicePickup+" counterHolderStopped="+b.EmergencyStopped);
                Check(collection.AwaitingServicePickup && !a.Carrying && a.PhysicsDriver.AtServicePoint(collection.Pickup),
                    "unloaded collector physically holds its table until a command");
                if(stop) sim.SetEmergencyStop("A",true);
                var retire=sim.Fleet.Retire("A");
                Check(collection.Phase==TaskPhase.Pending && collection.AssignedTo==null && !collection.AwaitingServicePickup
                    && !a.Carrying,"stop/retire releases the unpicked assignment without moving its plates");
                CheckUnpickedBatch(collection,dirty,"after stop/retire");
                yield return new WaitForSeconds(2);
                Check(!stop || retire.State==GuardianCommandState.Blocked,"stopped retirement reports blocked until recovery");

                // A is stopped/retiring and B immobilized, but healthy C must
                // still physically deliver at another table before either recovers.
                sim.SetCondition("C",RobotCondition.Normal);
                var healthy=sim.QueueTask(DeliveryKind.Soup,2); var c=healthy.AssignedTo;
                Check(c!=null && c.RobotId=="C","healthy C accepts independent work while A retires and B is stopped");
                var deploy=sim.Fleet.Deploy("D");
                yield return Wait(()=>healthy.Phase==TaskPhase.Completed,180,"healthy C continues physical meal delivery before recovery");
                if(failed) yield break;
                Check(healthy.CompletedAt>retire.AcceptedAt && Vector3.Distance(c.PhysicsDriver.Body.position,c.PhysicsDriver.Home)>2
                    && (!stop || a.EmergencyStopped) && b.EmergencyStopped,"normal actor completes real movement while affected actors remain unavailable");
                CheckUnpickedBatch(collection,dirty,"before recovery");
                var recover=sim.Fleet.RecoverToStandby("A");
                sim.Fleet.RecoverToStandby("B"); RemoveCounterBarrier(barrier);
                yield return Wait(Drained,900,"unloaded collection and independent work fully drain after recovery/reassignment");
                if(failed) yield break;
                Check(collection.AssignmentVersion>=2 && collection.AssignedTo!=a && collection.DishIds.OrderBy(id=>id).SequenceEqual(dirty.OrderBy(id=>id)),
                    "same promised collection reassigns to a healthy actor without changing identities");
                foreach(string id in dirty)
                {
                    var pickups=sim.Inventory.Events.Where(e=>e.Id==id && e.Action=="move"
                        && e.From==GuardianDishOwner.Table && e.To==GuardianDishOwner.Robot).ToArray();
                    Check(pickups.Length==1 && pickups[0].ToKey!="A" && sim.Inventory.Items[id].Owner==GuardianDishOwner.ReturnCounter,
                        "unpicked plate collected once by replacement and returned "+id);
                }
                Check(meal.DishIds.Concat(healthy.DishIds).All(id=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Table)
                    && sim.Tasks.Count(t=>t.Id==meal.Id && t.Phase==TaskPhase.Completed)==1,"both meals remain served; no repeated parent delivery");
                Check(retire.State==GuardianCommandState.Completed && recover.State==GuardianCommandState.Completed
                    && a.FleetRole==GuardianFleetRole.Standby && deploy.State==GuardianCommandState.Completed,
                    "retirement/recovery physically parks A and replacement deployment completes");
                SaveServeTrace(label);
            }
            report.Add("SERVE_CLEAR_WAITING_OK: unloaded stop/retire/recover, unique reservations, healthy progress and full drain");
        }

        private IEnumerator ServeClearStress()
        {
            foreach(bool five in new[]{false,true}) foreach(int seed in new[]{7,42,2026})
            {
                Reset(seed,true); sim.ReserveFleet=five;
                sim.Settings.MinimumTaskInterval=12; sim.Settings.MaximumTaskInterval=24;
                sim.Settings.MinimumFaultInterval=30; sim.Settings.MaximumFaultInterval=50; sim.ResetScenario();
                if(five) { sim.Fleet.Deploy("D"); sim.Fleet.Deploy("E"); }
                sim.AutomaticTasks=true; sim.Settings.RandomFaults=true;
                yield return Wait(()=>sim.DemoClock>=180,190,"compound random fault window reaches 180s fleet="+sim.Robots.Count+" seed="+seed);
                if(failed) yield break;
                sim.AutomaticTasks=false; sim.Settings.RandomFaults=false;
                foreach(var robot in sim.Robots) sim.SetCondition(robot.RobotId,RobotCondition.Normal);
                int pending=sim.Tasks.Count(t=>t.Phase!=TaskPhase.Completed);
                yield return Wait(Drained,Mathf.Max(600,pending*120),"all parent meals and linked/independent returns drain fleet="+sim.Robots.Count+" seed="+seed);
                if(failed) yield break;
                Check(sim.Tasks.Any(t=>t.ParentDeliveryId!=null),"random operation creates linked clearing work");
                Check(sim.Tasks.Where(t=>t.FixedDishBatch).All(t=>t.DishIds.Count<=GuardianDishInventory.TrayCapacity
                    && !t.DishIds.Any(id=>t.ExcludedDishIds.Contains(id))),"every automatic batch honors capacity and fresh-ID exclusions");
                report.Add("SERVE_STRESS fleet="+sim.Robots.Count+" seed="+seed+" time="+sim.DemoClock.ToString("F2")+" jobs="+sim.Tasks.Count
                    +" linked="+sim.Tasks.Count(t=>t.ParentDeliveryId!=null)+" completed="+sim.Tasks.Count(t=>t.Phase==TaskPhase.Completed));
                SaveServeTrace("stress-"+(five?5:3)+"-"+seed);
            }
        }
    }
}
