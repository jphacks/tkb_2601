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
        public string Action,Id,FromKey,ToKey,TaskId;
        public GuardianDishOwner From,To;
        public Vector3 Position;
        public bool Dirty;
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
    }
    /// <summary>One persistent identity per dish. Reservations never move/hide an item.</summary>
    public sealed class GuardianDishInventory : MonoBehaviour
    {
        private readonly Dictionary<string,GuardianDishItem> items=new Dictionary<string,GuardianDishItem>();
        private readonly List<GameObject> hiddenDecor=new List<GameObject>();
        private GuardianSimulator sim;
        private RestaurantCatalog catalog;
        private Transform kitchen,returns;
        private Transform[] tables;
        private int sequence,returned;
        public const int TrayCapacity=4;
        private readonly List<GuardianDishEvent> events=new List<GuardianDishEvent>();
        public IReadOnlyList<GuardianDishEvent> Events=>events;
        public IReadOnlyDictionary<string,GuardianDishItem> Items=>items;
        public int Transfers { get; private set; }
        public static DeliveryKind KindOf(GuardianMenu menu)=>menu==GuardianMenu.Soup?DeliveryKind.Soup:DeliveryKind.Food;
        public static string AssetOf(GuardianMenu menu)=>"Food"+menu;
        public void ClearInventory()
        {
            foreach (var item in items.Values) if (item!=null) { item.gameObject.SetActive(false); Destroy(item.gameObject); }
            items.Clear(); events.Clear(); sequence=0; returned=0; Transfers=0;
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
            // A linked batch promises specific existing empty plates. Keep its
            // reservation through unassignment/source return so another request
            // cannot consume the pending job's source and leave a ghost trip.
            if(task.FixedDishBatch) return;
            foreach (string id in task.DishIds)
                if (items.TryGetValue(id,out var item) && item.ReservedBy==task.Id) item.ReservedBy=null;
            if (task.Kind==DeliveryKind.EmptyDishes) task.DishIds.Clear();
        }
        public bool PickUp(DeliveryTask task,GuardianRobot robot)
        {
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
                    item.Dirty=false;
                    Place(item,GuardianDishOwner.Table,task.TableIndex.ToString(),null);
                    item.DirtyAt=sim.DemoClock+sim.Settings.MealSeconds;
                }
                item.ReservedBy=null;
            }
        }
        public void ReturnToSource(DeliveryTask task)
        {
            foreach (string id in task.DishIds)
            {
                var item=items[id];
                if (item.Owner!=GuardianDishOwner.Robot) throw new InvalidOperationException("Handoff without carried dish "+id);
                Place(item,task.Kind==DeliveryKind.EmptyDishes?GuardianDishOwner.Table:GuardianDishOwner.Kitchen,
                    task.Kind==DeliveryKind.EmptyDishes?task.TableIndex.ToString():"Kitchen",null);
                item.ReservedBy=task.FixedDishBatch?task.Id:null;
            }
            if (task.Kind==DeliveryKind.EmptyDishes && !task.FixedDishBatch) task.DishIds.Clear();
        }
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
            item.Owner=owner; item.OwnerKey=key; Transfers++;
            Transform parent; Vector3 local;
            if (owner==GuardianDishOwner.Robot)
            { parent=robot.transform; int count=items.Values.Count(i=>i!=item && i.Owner==owner && i.OwnerKey==key); local=new Vector3(0,1.35f+count*.075f,.35f); }
            else if (owner==GuardianDishOwner.Table)
            { parent=tables[int.Parse(key)]; int count=items.Values.Count(i=>i!=item && i.Owner==owner && i.OwnerKey==key); local=new Vector3(count%2==0?-.32f:.32f,.825f+(count/2)*.025f,0); }
            else if (owner==GuardianDishOwner.Kitchen)
            { parent=kitchen; int count=items.Values.Count(i=>i!=item && i.Owner==owner); local=new Vector3(.10f,1.115f,(count%3-1)*.42f); }
            else
            { parent=returns; local=new Vector3(0,1.07f+(returned%8)*.024f,.1f); returned++; }
            item.transform.SetParent(parent,false); item.transform.localPosition=local; item.transform.localRotation=Quaternion.identity;
            item.gameObject.SetActive(owner!=GuardianDishOwner.ReturnCounter || returned<=12);
            Record(item,"move",previous,previousKey);
        }
        private void Record(GuardianDishItem item,string action,GuardianDishOwner previous,string previousKey)
        {
            var entry=new GuardianDishEvent{Clock=sim.DemoClock,Action=action,Id=item.Id,From=previous,FromKey=previousKey,
                To=item.Owner,ToKey=item.OwnerKey,Position=item.transform.position,Dirty=item.Dirty,TaskId=item.ReservedBy};
            events.Add(entry);
            Debug.Log("[GuardianDish] "+entry.Clock.ToString("F2")+"s "+action+" "+item.Id+" "+(previousKey==null?"Unplaced":previous+"/"+previousKey)+
                " -> "+item.Owner+"/"+item.OwnerKey+" dirty="+item.Dirty+" task="+item.ReservedBy+" position="+entry.Position.ToString("F3"));
        }
        private void RebuildVisual(GuardianDishItem item)
        {
            foreach (Transform child in item.transform) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            GameObject model=Instantiate(catalog.Get(item.Dirty?"Plate":AssetOf(item.Menu)),item.transform);
            model.name=item.Dirty?"Empty plate":"Meal "+item.Menu;
            foreach (Collider c in model.GetComponentsInChildren<Collider>()) c.enabled=false;
        }
        public void ValidateOwnership()
        {
            var seen=new HashSet<int>();
            foreach (var item in items.Values)
            {
                if (item==null || !seen.Add(item.GetInstanceID())) throw new InvalidOperationException("Missing/duplicate dish identity");
                Transform expected=item.Owner==GuardianDishOwner.Kitchen?kitchen:item.Owner==GuardianDishOwner.ReturnCounter?returns:
                    item.Owner==GuardianDishOwner.Table?tables[int.Parse(item.OwnerKey)]:
                    sim.Robots.Single(r=>r.RobotId==item.OwnerKey).transform;
                if (item.transform.parent!=expected) throw new InvalidOperationException("Dish parent/owner mismatch "+item.Id);
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
                if(items.Values.Count(i=>i.Owner==GuardianDishOwner.Robot && i.OwnerKey==robot.RobotId)>TrayCapacity)
                    throw new InvalidOperationException("Overfilled physical tray "+robot.RobotId);
        }
    }
}
