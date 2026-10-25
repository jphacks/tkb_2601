using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuardianMenu { Soup, CurryRice, Noodles, Hamburg }
    public enum GuardianDishOwner { Kitchen, Robot, Table, ReturnCounter }
    public sealed class GuardianDishEvent
    {
        public float Clock;
        public string CarrierId;
        public float CarrierYaw,ServiceHeadingError,ServicePositionError,ServiceSpeed;
        public string Action,Id,FromKey,ToKey,TaskId;
        public GuardianDishOwner From,To;
        public Vector3 Position;
        public bool Dirty;
        public int InstanceId;
        public string ParentName;
        public bool Active,RenderVisible;
    }
    public sealed class GuardianDishItem : MonoBehaviour
    {
        public string Id;
        public GuardianMenu Menu;
        public GuardianDishOwner Owner;
        public string OwnerKey;
        public bool Dirty;
        public string ReservedBy;
        public float DirtyAt;
        public int TraySlot=-1;
        // Own spot on the kitchen pass or a table until the dish leaves; -1 elsewhere.
        public int SurfaceSlot=-1;
        // The guest's place this meal is for (0-3, in front of a chair), or -1: set when it is served.
        public int SeatHint=-1;
        // A 33.6cm dinner plate fits both racks. Keep this size at every owner;
        // never resize an individual physical dish during a handoff.
        public const float PhysicalScale=.82f;
    }
    /// <summary>One persistent identity per dish. Reservations never move/hide an item.</summary>
    public sealed class GuardianDishInventory : MonoBehaviour
    {
        private readonly Dictionary<string,GuardianDishItem> items=new Dictionary<string,GuardianDishItem>();
        private readonly List<GameObject> hiddenDecor=new List<GameObject>();
        // Places at a table that belong to a seated guest (table, place 0-3): other dishes keep off them.
        private readonly HashSet<(int,int)> guestPlaces=new HashSet<(int,int)>();
        private GuardianSimulator sim;
        private RestaurantCatalog catalog;
        private Transform kitchen,returns;
        private Transform[] tables;
        private int sequence,returned;
        public const int TrayCapacity=4;
        // Food Pickup frame: four along the pass front, four on the pass shelf, three under it.
        private static readonly Vector3[] KitchenSpots={
            new Vector3(.27f,1.115f,-.185f),new Vector3(.27f,1.115f,.185f),new Vector3(.27f,1.115f,-.555f),new Vector3(.27f,1.115f,.555f),
            new Vector3(-.13f,1.68f,-.185f),new Vector3(-.13f,1.68f,.185f),new Vector3(-.13f,1.68f,-.555f),new Vector3(-.13f,1.68f,.555f),
            new Vector3(-.24f,1.115f,0),new Vector3(-.24f,1.115f,-.36f),new Vector3(-.24f,1.115f,.36f)};
        // Table frame: in front of the four seats first, then inwards, clear of the menu and condiments.
        private static readonly Vector3[] TableSpots={
            new Vector3(-.72f,.825f,-.38f),new Vector3(.72f,.825f,-.38f),new Vector3(-.72f,.825f,.38f),new Vector3(.72f,.825f,.38f),
            new Vector3(-.36f,.825f,-.38f),new Vector3(.36f,.825f,-.38f),new Vector3(-.36f,.825f,.38f),new Vector3(.36f,.825f,.38f),
            new Vector3(-.72f,.825f,0),new Vector3(.72f,.825f,0),new Vector3(-.36f,.825f,0),new Vector3(.36f,.825f,0),new Vector3(0,.825f,0)};
        private readonly List<GuardianDishEvent> events=new List<GuardianDishEvent>();
        public IReadOnlyList<GuardianDishEvent> Events=>events;
        public IReadOnlyDictionary<string,GuardianDishItem> Items=>items;
        public int Transfers { get; private set; }
        public static DeliveryKind KindOf(GuardianMenu menu)=>menu==GuardianMenu.Soup?DeliveryKind.Soup:DeliveryKind.Food;
        public static string AssetOf(GuardianMenu menu)=>"Food"+menu;
        public void ClearInventory()
        {
            foreach (var item in items.Values) if (item!=null) { item.gameObject.SetActive(false); Destroy(item.gameObject); }
            items.Clear(); events.Clear(); sequence=0; returned=0; Transfers=0; guestPlaces.Clear();
            foreach (GameObject decor in hiddenDecor) if (decor!=null) decor.SetActive(true);
            hiddenDecor.Clear();
        }
        public void ResetInventory(GuardianSimulator simulator)
        {
            ClearInventory(); sim=simulator;
            catalog=Resources.Load<RestaurantCatalog>("RestaurantCatalog");
            Transform root=sim.transform.Find("Generated 3D World/"+RestaurantVisuals.RootName);
            kitchen=root.Find("Food Pickup"); returns=root.Find("Dish Return");
            tables=Enumerable.Range(1,3).Select(i=>root.Find("Dining Table "+i)).ToArray();
            // Existing cosmetic place settings become actual collectible identities.
            foreach (Transform table in tables)
                foreach (RestaurantFixture fixture in table.GetComponentsInChildren<RestaurantFixture>())
                    if (fixture.transform!=table && (fixture.AssetId=="Plate" || fixture.AssetId=="Bowl" || fixture.AssetId=="Cup"))
                    { hiddenDecor.Add(fixture.gameObject); fixture.gameObject.SetActive(false); }
            for (int i=0;i<3;i++)
            {
                var item=Create(GuardianMenu.CurryRice); item.Dirty=true;
                Place(item,GuardianDishOwner.Table,i.ToString(),null); RebuildVisual(item);
            }
        }
        private GuardianDishItem Create(GuardianMenu menu)
        {
            var go=new GameObject("Dish "+(++sequence).ToString("D4"));
            var item=go.AddComponent<GuardianDishItem>(); item.Id="DISH-"+sequence.ToString("D4"); item.Menu=menu;
            items.Add(item.Id,item); return item;
        }
        public void Prepare(DeliveryTask task)
        {
            if (task.Kind==DeliveryKind.EmptyDishes || task.DishIds.Count>0) return;
            var item=Create(task.Menu); task.DishIds.Add(item.Id);
            Place(item,GuardianDishOwner.Kitchen,"Kitchen",null); RebuildVisual(item);
        }
        public bool Reserve(DeliveryTask task)
        {
            Prepare(task);
            if (task.Kind==DeliveryKind.EmptyDishes && task.DishIds.Count==0)
                task.DishIds.AddRange(AvailableDirty(task.TableIndex,task.ExcludedDishIds).Take(TrayCapacity).Select(i=>i.Id));
            if (task.DishIds.Count==0) { task.PendingReason="No empty dishes at table "+(task.TableIndex+1); return false; }
            if(task.DishIds.Count>TrayCapacity || task.DishIds.Distinct().Count()!=task.DishIds.Count)
                throw new InvalidOperationException("Invalid dish batch capacity or duplicate identity "+task.Id);
            foreach (string id in task.DishIds)
            {
                GuardianDishItem item=items[id];
                bool source=task.Kind==DeliveryKind.EmptyDishes?item.Owner==GuardianDishOwner.Table && item.Dirty
                    && item.OwnerKey==task.TableIndex.ToString() && !task.ExcludedDishIds.Contains(id):item.Owner==GuardianDishOwner.Kitchen;
                if (!source || (!string.IsNullOrEmpty(item.ReservedBy) && item.ReservedBy!=task.Id))
                { task.PendingReason="Dish source reserved or unavailable"; return false; }
            }
            foreach (string id in task.DishIds) items[id].ReservedBy=task.Id;
            return true;
        }
        public void ReleaseReservation(DeliveryTask task)
        {
            // Fixed batches and human-held/targeted requests promise their
            // current source identities. Unassignment must not expose those
            // plates to another batch before the operator releases the promise.
            if(RetainsSourceReservation(task)) return;
            foreach (string id in task.DishIds)
                if (items.TryGetValue(id,out var item) && item.ReservedBy==task.Id) item.ReservedBy=null;
            if (task.Kind==DeliveryKind.EmptyDishes) task.DishIds.Clear();
        }
        public bool PickUp(DeliveryTask task,GuardianRobot robot)
        {
            if(!GuardianSimulator.OperatorAllowsAssignment(task,robot))
            { task.PendingReason=GuardianSimulator.OperatorWaitReason(task); return false; }
            if (!Reserve(task)) return false;
            if(items.Values.Count(i=>i.Owner==GuardianDishOwner.Robot && i.OwnerKey==robot.RobotId)+task.DishIds.Count>TrayCapacity)
                throw new InvalidOperationException("Robot tray capacity exceeded "+robot.RobotId);
            foreach (string id in task.DishIds)
            {
                var item=items[id];
                if (item.Owner==GuardianDishOwner.Robot) throw new InvalidOperationException("Dish picked up twice "+id);
                Place(item,GuardianDishOwner.Robot,robot.RobotId,robot);
            }
            return true;
        }
        public void Complete(DeliveryTask task)
        {
            foreach (string id in task.DishIds)
            {
                var item=items[id];
                if (item.Owner!=GuardianDishOwner.Robot || item.OwnerKey!=task.AssignedTo.RobotId)
                    throw new InvalidOperationException("Completion without dish ownership "+id);
                if (task.Kind==DeliveryKind.EmptyDishes) Place(item,GuardianDishOwner.ReturnCounter,"Return",null);
                else
                {
                    item.Dirty=false; item.SeatHint=task.Seat;
                    Place(item,GuardianDishOwner.Table,task.TableIndex.ToString(),null);
                    // A seated guest eats it and empties it when they finish (FinishMeals); otherwise it empties on the clock.
                    item.DirtyAt=task.Seat>=0 && guestPlaces.Contains((task.TableIndex,task.Seat))?float.MaxValue:sim.DemoClock+sim.Settings.MealSeconds;
                }
                item.ReservedBy=null;
            }
        }
        public void ReturnToSource(DeliveryTask task)
        {
            bool retain=RetainsSourceReservation(task);
            foreach (string id in task.DishIds)
            {
                var item=items[id];
                if (item.Owner!=GuardianDishOwner.Robot) throw new InvalidOperationException("Handoff without carried dish "+id);
                Place(item,task.Kind==DeliveryKind.EmptyDishes?GuardianDishOwner.Table:GuardianDishOwner.Kitchen,
                    task.Kind==DeliveryKind.EmptyDishes?task.TableIndex.ToString():"Kitchen",null);
                item.ReservedBy=retain?task.Id:null;
            }
            if (task.Kind==DeliveryKind.EmptyDishes && !retain) task.DishIds.Clear();
        }
        private static bool RetainsSourceReservation(DeliveryTask task)=>task.FixedDishBatch
            || !string.IsNullOrEmpty(task.OperatorHoldReason) || !string.IsNullOrEmpty(task.RequestedRobotId);
        public IEnumerable<GuardianDishItem> AvailableDirty(int table,ICollection<string> excluded=null)
        {
            return items.Values.Where(i=>i.Dirty && i.Owner==GuardianDishOwner.Table && i.OwnerKey==table.ToString()
                && string.IsNullOrEmpty(i.ReservedBy) && (excluded==null || !excluded.Contains(i.Id))).OrderBy(i=>i.Id);
        }
        public bool ReserveFixedBatch(DeliveryTask task,IEnumerable<string> ids)
        {
            string[] batch=ids.ToArray();
            if(task.Kind!=DeliveryKind.EmptyDishes || task.DishIds.Count!=0 || batch.Length==0 || batch.Length>TrayCapacity || batch.Distinct().Count()!=batch.Length)
                throw new InvalidOperationException("Invalid fixed return batch");
            if(batch.Any(id=>!items.TryGetValue(id,out var item) || item.Owner!=GuardianDishOwner.Table || !item.Dirty
                || item.OwnerKey!=task.TableIndex.ToString() || !string.IsNullOrEmpty(item.ReservedBy) || task.ExcludedDishIds.Contains(id))) return false;
            task.FixedDishBatch=true; task.DishIds.AddRange(batch);
            foreach(string id in batch) items[id].ReservedBy=task.Id;
            return true;
        }
        public void Tick()
        {
            foreach (var item in items.Values)
                if (!item.Dirty && item.Owner==GuardianDishOwner.Table && item.DirtyAt>0 && sim.DemoClock>=item.DirtyAt)
                { item.Dirty=true; RebuildVisual(item); Record(item,"ready",item.Owner,item.OwnerKey); }
        }
        private void Place(GuardianDishItem item,GuardianDishOwner owner,string key,GuardianRobot robot)
        {
            var previous=item.Owner; string previousKey=item.OwnerKey;
            var carrier=owner==GuardianDishOwner.Robot?robot:previous==GuardianDishOwner.Robot?sim.Robots.Single(r=>r.RobotId==previousKey):null;
            if(carrier!=null)
            {
                var task=carrier.CurrentTask;
                Vector3 endpoint=task.Phase==TaskPhase.ToDropoff?task.Destination:task.Pickup;
                if(!carrier.PhysicsDriver.AtServicePoint(endpoint)) throw new InvalidOperationException("Dish handoff requires stopped, rear-facing physical service arrival");
            }
            item.Owner=owner; item.OwnerKey=key; item.SurfaceSlot=-1; Transfers++;
            Transform parent; Vector3 local;
            if (owner==GuardianDishOwner.Robot)
            {
                var used=new HashSet<int>(items.Values.Where(i=>i!=item && i.Owner==owner && i.OwnerKey==key).Select(i=>i.TraySlot));
                // Meals use an accessible upper shelf; returned plates fill the
                // rack from the bottom. Each physical shelf holds one identity.
                var order=Enumerable.Range(0,TrayCapacity);
                item.TraySlot=(item.Dirty?order:order.Reverse()).First(slot=>!used.Contains(slot));
                robot.GetComponent<GuardianCargoTray>().Mount(item.transform,item.TraySlot,GuardianDishItem.PhysicalScale);
                item.gameObject.SetActive(true); Record(item,"move",previous,previousKey); return;
            }
            else if (owner==GuardianDishOwner.Table)
            { parent=tables[int.Parse(key)]; local=Spot(item,TableSpots); }
            else if (owner==GuardianDishOwner.Kitchen)
            { parent=kitchen; local=Spot(item,KitchenSpots); }
            else
            { parent=returns; local=new Vector3(0,1.07f+(returned%8)*.024f,.1f); returned++; }
            item.TraySlot=-1; item.transform.SetParent(parent,false); item.transform.localPosition=local; item.transform.localRotation=Quaternion.identity; item.transform.localScale=Vector3.one*GuardianDishItem.PhysicalScale;
            // Returned items keep their identity/ownership but leave the visible
            // simulation. Only this dish is hidden; table siblings are untouched.
            item.gameObject.SetActive(owner!=GuardianDishOwner.ReturnCounter);
            Record(item,"move",previous,previousKey);
        }
        private Vector3 Spot(GuardianDishItem item,Vector3[] spots)
        {
            var taken=new HashSet<int>(items.Values.Where(i=>i!=item && i.Owner==item.Owner && i.OwnerKey==item.OwnerKey).Select(i=>i.SurfaceSlot));
            int slot=-1, hint=item.SeatHint; item.SeatHint=-1;
            if(item.Owner==GuardianDishOwner.Table)
            {
                int table=int.Parse(item.OwnerKey);
                // A guest's meal goes in front of them: the place by the chair, then the one just inside it.
                if(hint>=0) slot=!taken.Contains(hint)?hint:!taken.Contains(hint+4)?hint+4:-1;
                // The last guest's empty dishes still there (the seat was offered before they were cleared): push one
                // aside, so the meal still lands in front of the guest who ordered it (KeepServing; off in verifications).
                if(hint>=0 && slot<0 && !item.Dirty && sim.Settings.KeepServing) slot=PushAside(table,hint,spots,taken);
                // Still no room in front of the guest: the middle of the table, never in front of an empty chair (KeepServing).
                if(hint>=0 && slot<0 && !item.Dirty && sim.Settings.KeepServing)
                    for(int s=8;s<spots.Length && slot<0;s++) if(!taken.Contains(s)) slot=s;
                // Any other dish keeps off the places of seated guests while there is room elsewhere.
                if(slot<0) for(int s=0;s<spots.Length;s++)
                    if(!taken.Contains(s) && !(s<8 && guestPlaces.Contains((table,s%4)))) { slot=s; break; }
            }
            if(slot<0) { slot=0; while(taken.Contains(slot)) slot++; }
            item.SurfaceSlot=slot;
            // Only when more dishes wait than there are spots does one rest above another.
            return spots[slot%spots.Length]+Vector3.up*(slot/spots.Length)*.13f;
        }
        /// <summary>
        /// Moves an empty dish off a guest's place to a free spot away from every seated guest; returns the slot it freed,
        /// or -1 when there is nothing empty there or no room elsewhere. The dish keeps its identity and any reservation.
        /// </summary>
        private int PushAside(int table,int place,Vector3[] spots,HashSet<int> taken)
        {
            var dirty=items.Values.FirstOrDefault(i=>i.Dirty && i.Owner==GuardianDishOwner.Table && i.OwnerKey==table.ToString()
                && (i.SurfaceSlot==place || i.SurfaceSlot==place+4));
            if(dirty==null) return -1;
            int free=-1;
            for(int s=spots.Length-1;s>=0 && free<0;s--)   // the middle of the table first, then inwards, then the corners
                if(!taken.Contains(s) && !(s<8 && guestPlaces.Contains((table,s%4)))) free=s;
            if(free<0) return -1;
            int freed=dirty.SurfaceSlot;
            taken.Remove(freed); taken.Add(free);
            dirty.SurfaceSlot=free;
            dirty.transform.localPosition=spots[free];
            Record(dirty,"aside",GuardianDishOwner.Table,table.ToString());
            return freed;
        }
        private void Record(GuardianDishItem item,string action,GuardianDishOwner previous,string previousKey)
        {
            var entry=new GuardianDishEvent{Clock=sim.DemoClock,Action=action,Id=item.Id,From=previous,FromKey=previousKey,
                To=item.Owner,ToKey=item.OwnerKey,Position=item.transform.position,Dirty=item.Dirty,TaskId=item.ReservedBy,
                InstanceId=item.GetInstanceID(),ParentName=item.transform.parent.name,Active=item.gameObject.activeInHierarchy,
                RenderVisible=item.GetComponentsInChildren<Renderer>(true).Any(r=>r.enabled && r.gameObject.activeInHierarchy)};
            var carrier=item.Owner==GuardianDishOwner.Robot?sim.Robots.Single(r=>r.RobotId==item.OwnerKey):previous==GuardianDishOwner.Robot?sim.Robots.Single(r=>r.RobotId==previousKey):null;
            if(carrier!=null)
            {
                var task=carrier.CurrentTask; Vector3 endpoint=task.Phase==TaskPhase.ToDropoff?task.Destination:task.Pickup;
                Vector3 error=carrier.PhysicsDriver.Body.position-endpoint; error.y=0;
                entry.CarrierId=carrier.RobotId; entry.CarrierYaw=carrier.PhysicsDriver.Body.rotation.eulerAngles.y;
                entry.ServiceHeadingError=Mathf.Abs(Mathf.DeltaAngle(entry.CarrierYaw,GuardianPhysicsDriver.ServiceYaw(endpoint)));
                entry.ServicePositionError=error.magnitude; entry.ServiceSpeed=carrier.PhysicsDriver.Body.linearVelocity.magnitude;
            }
            events.Add(entry);
            if(sim.Settings.VerboseRuntimeLogs)Debug.Log("[GuardianDish] "+entry.Clock.ToString("F2")+"s "+action+" "+item.Id+" "+(previousKey==null?"Unplaced":previous+"/"+previousKey)+
                " -> "+item.Owner+"/"+item.OwnerKey+" dirty="+item.Dirty+" task="+item.ReservedBy+" position="+entry.Position.ToString("F3"));
        }
        private void RebuildVisual(GuardianDishItem item)
        {
            foreach (Transform child in item.transform) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            GameObject model=Instantiate(catalog.Get(AssetOf(item.Menu)),item.transform);
            model.name=(item.Dirty?"Eaten ":"Meal ")+item.Menu;
            // Each menu has its own dish set: "Dish" parts always show; once eaten, the food gives way to "Leftover" parts.
            foreach (Renderer part in model.GetComponentsInChildren<Renderer>(true))
                part.gameObject.SetActive(part.name.StartsWith("Dish") || part.name.StartsWith("Leftover")==item.Dirty);
            foreach (Collider c in model.GetComponentsInChildren<Collider>()) c.enabled=false;
        }
        /// <summary>True when every dish of this meal order still waits on the kitchen pass (nothing is on a tray or a table).</summary>
        public bool StillInKitchen(DeliveryTask task)=>task.Kind!=DeliveryKind.EmptyDishes && task.DishIds.Count>0
            && task.DishIds.All(id=>items.TryGetValue(id,out var item) && item.Owner==GuardianDishOwner.Kitchen);

        /// <summary>The guest left before their meal was picked up: the meal leaves the pass (StillInKitchen first).</summary>
        public void Discard(DeliveryTask task)
        {
            if(!StillInKitchen(task)) throw new InvalidOperationException("Only a meal still on the kitchen pass can be taken back "+task.Id);
            foreach(string id in task.DishIds)
            {
                var item=items[id];
                item.ReservedBy=null;
                Record(item,"withdrawn",item.Owner,item.OwnerKey);
                items.Remove(id); item.gameObject.SetActive(false); Destroy(item.gameObject);
            }
        }

        // ---- the guests at the tables (GuardianDinerFlow) ----

        /// <summary>A guest sits at this place (0-3, the chair's corner of the table) or leaves it.</summary>
        public void ReserveGuestPlace(int table,int place,bool seated)
        {
            if(seated) { guestPlaces.Add((table,place)); return; }
            guestPlaces.Remove((table,place));
            // A meal that came after its guest gave up goes back to the clock, so it is never left uncleared.
            foreach(var item in AtPlace(table,place))
                if(!item.Dirty && item.DirtyAt==float.MaxValue) item.DirtyAt=sim.DemoClock+sim.Settings.MealSeconds;
        }

        private IEnumerable<GuardianDishItem> AtPlace(int table,int place)=>items.Values.Where(i=>i.Owner==GuardianDishOwner.Table
            && i.OwnerKey==table.ToString() && (i.SurfaceSlot==place || i.SurfaceSlot==place+4));

        /// <summary>A dish the last guest at this place emptied before the scene opens: ready to be cleared at once.</summary>
        public void LeaveAtPlace(int table,int place,GuardianMenu menu)
        {
            var item=Create(menu); item.Dirty=true; item.SeatHint=place;
            Place(item,GuardianDishOwner.Table,table.ToString(),null); RebuildVisual(item);
        }

        /// <summary>The guest has eaten: everything in front of them is now an emptied dish, ready to be cleared.</summary>
        public int FinishMeals(int table,int place)
        {
            int n=0;
            foreach(var item in AtPlace(table,place).Where(i=>!i.Dirty).ToArray())
            { item.Dirty=true; item.DirtyAt=0; RebuildVisual(item); Record(item,"ready",item.Owner,item.OwnerKey); n++; }
            return n;
        }

        /// <summary>Nothing left at a guest's place (the dishes have been cleared).</summary>
        public bool PlaceClear(int table,int place)=>!AtPlace(table,place).Any();

        public void ValidateOwnership()
        {
            var seen=new HashSet<int>();
            foreach (var item in items.Values)
            {
                if (item==null || !seen.Add(item.GetInstanceID())) throw new InvalidOperationException("Missing/duplicate dish identity");
                Transform expected=item.Owner==GuardianDishOwner.Kitchen?kitchen:item.Owner==GuardianDishOwner.ReturnCounter?returns:
                    item.Owner==GuardianDishOwner.Table?tables[int.Parse(item.OwnerKey)]:
                    sim.Robots.Single(r=>r.RobotId==item.OwnerKey).GetComponent<GuardianCargoTray>().Anchors[item.TraySlot];
                if (item.transform.parent!=expected) throw new InvalidOperationException("Dish parent/owner mismatch "+item.Id);
                if(Vector3.Distance(item.transform.localScale,Vector3.one*GuardianDishItem.PhysicalScale)>.00001f)
                    throw new InvalidOperationException("Physical dish resized across ownership "+item.Id);
                if(item.Owner==GuardianDishOwner.Robot) sim.Robots.Single(r=>r.RobotId==item.OwnerKey).GetComponent<GuardianCargoTray>().ValidateMount(item.transform,item.TraySlot);
                if(item.gameObject.activeInHierarchy!=(item.Owner!=GuardianDishOwner.ReturnCounter))
                    throw new InvalidOperationException("Dish visible-state/owner mismatch "+item.Id);
                if (item.Owner==GuardianDishOwner.Robot && !sim.Robots.Any(r=>r.RobotId==item.OwnerKey && r.Carrying
                    && r.CurrentTask!=null && r.CurrentTask.DishIds.Contains(item.Id))) throw new InvalidOperationException("Unowned carried dish "+item.Id);
                if(!string.IsNullOrEmpty(item.ReservedBy))
                {
                    var task=sim.Tasks.SingleOrDefault(t=>t.Id==item.ReservedBy);
                    if(task==null || task.Phase==TaskPhase.Completed || !task.DishIds.Contains(item.Id))
                        throw new InvalidOperationException("Orphaned dish reservation "+item.Id);
                }
            }
            foreach(var robot in sim.Robots)
            {
                if(items.Values.Count(i=>i.Owner==GuardianDishOwner.Robot && i.OwnerKey==robot.RobotId)>TrayCapacity)
                    throw new InvalidOperationException("Overfilled physical tray "+robot.RobotId);
                var slots=items.Values.Where(i=>i.Owner==GuardianDishOwner.Robot && i.OwnerKey==robot.RobotId).Select(i=>i.TraySlot).ToArray();
                if(slots.Distinct().Count()!=slots.Length) throw new InvalidOperationException("Two dishes share one physical shelf "+robot.RobotId);
            }
            foreach(var surface in items.Values.Where(i=>i.Owner==GuardianDishOwner.Kitchen || i.Owner==GuardianDishOwner.Table).GroupBy(i=>i.Owner+"/"+i.OwnerKey))
                if(surface.Select(i=>i.SurfaceSlot).Distinct().Count()!=surface.Count()) throw new InvalidOperationException("Two dishes share one spot at "+surface.Key);
        }
    }
}
