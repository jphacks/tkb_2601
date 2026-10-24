using System;
using System.Collections.Generic;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum RobotCondition { Normal, Warning, Limited }
    public enum DeliveryKind { Soup, Food, EmptyDishes }
    public enum TaskPhase { Pending, ToPickup, ToDropoff, ReturningCargo, Completed }

    [Serializable]
    public sealed class DeliveryTask
    {
        public string Id;
        public DeliveryKind Kind;
        public Vector3 Pickup;
        public Vector3 Destination;
        public TaskPhase Phase;
        public GuardianRobot AssignedTo;
        public GuardianMenu Menu;
        public int TableIndex;
        public readonly List<string> DishIds=new List<string>();
        public string PendingReason;
        public int AssignmentVersion;
        public float CreatedAt,FirstAssignedAt=-1,CompletedAt;
        public int RevokedVersion=-1;
        public string ParentDeliveryId,SameVisitReturnId;
        public bool FixedDishBatch,AwaitingServicePickup;
        public readonly List<string> ExcludedDishIds=new List<string>();

        public DeliveryTask(string id, DeliveryKind kind, Vector3 pickup, Vector3 destination)
        {
            Id = id;
            Kind = kind;
            Pickup = pickup;
            Destination = destination;
            Phase = TaskPhase.Pending;
        }
    }

    [Serializable]
    public sealed class GuardianTaskRequest
    {
        public int Id;
        public DeliveryKind Kind;
        public int TableIndex;
        public float ReceivedAt;
        public DeliveryTask Task;
        public bool Coalesced;
        public string Decision;
        public string ParentDeliveryId;
        public string Status=>Task==null?"Rejected":Task.Phase.ToString();
    }

    /// <summary>Kinematic navigation on simple restaurant lanes. No physics or NavMesh bake required.</summary>
    public sealed class GuardianRobot : MonoBehaviour
    {
        public string RobotId;
        public RobotCondition Condition;
        [Range(0f, 1f)] public float Vibration;
        public float Speed = 1.9f;
        public bool EmergencyStopped;
        public bool DispatchEnabled=true;
        public GuardianFleetRole FleetRole=GuardianFleetRole.Active;
        public DeliveryTask CurrentTask { get; private set; }
        public bool Carrying { get; private set; }
        public bool IsIdle { get { return CurrentTask == null && _route.Count == 0 && !(PhysicsDriver!=null && PhysicsDriver.Busy); } }
        public GuardianPhysicsDriver PhysicsDriver=>GetComponent<GuardianPhysicsDriver>();
        public event Action<GuardianRobot> Arrived;

        private readonly List<Vector3> _route = new List<Vector3>();
        private int _routeIndex;
        private GameObject _cargo;
        private Vector3 _home;

        private void Awake() { _home = transform.position; }

        public void ResetRobot(Vector3 home, RobotCondition condition, float vibration)
        {
            if (PhysicsDriver!=null && PhysicsDriver.Active) PhysicsDriver.ResetTo(home);
            else transform.position = home;
            _home = home;
            Condition = condition;
            Vibration = vibration;
            CurrentTask = null;
            Carrying = false;
            EmergencyStopped=false;
            ClearRoute();
            ClearCargo();
        }

        public void Assign(DeliveryTask task)
        {
            AttachTask(task);
            task.AwaitingServicePickup=false;
            DriveTo(task.Pickup);
        }

        internal void AssignAtServicePoint(DeliveryTask task)
        {
            AttachTask(task);
            task.AwaitingServicePickup=true;
        }

        private void AttachTask(DeliveryTask task)
        {
            CurrentTask = task;
            task.AssignedTo = this;
            task.Phase = TaskPhase.ToPickup;
            task.AssignmentVersion++;
            if (task.FirstAssignedAt<0) task.FirstAssignedAt=GetComponentInParent<GuardianSimulator>().DemoClock;
            task.PendingReason=null;
            Carrying = false;
            ClearCargo();
        }

        public void PickUp()
        {
            if (CurrentTask == null) return;
            var sim=GetComponentInParent<GuardianSimulator>();
            if (sim.Inventory!=null && !sim.Inventory.PickUp(CurrentTask,this))
            { sim.PickupUnavailable(this); return; }
            Carrying = true;
            CurrentTask.AwaitingServicePickup=false;
            if (sim.Inventory!=null) sim.Traffic.PickupCompleted(CurrentTask);
            CurrentTask.Phase = TaskPhase.ToDropoff;
            if (sim.Inventory==null) CreateCargo(CurrentTask.Kind);
            DriveTo(CurrentTask.Destination);
        }

        public void ReturnCargoToPickup()
        {
            if (CurrentTask == null || !Carrying) return;
            CurrentTask.Phase = TaskPhase.ReturningCargo;
            DriveTo(CurrentTask.Pickup);
        }

        public DeliveryTask ReleaseTask(bool returnHome=true)
        {
            DeliveryTask previous = CurrentTask;
            if(previous!=null) previous.AwaitingServicePickup=false;
            CurrentTask = null;
            Carrying = false;
            ClearRoute();
            ClearCargo();
            if (returnHome && PhysicsDriver!=null && PhysicsDriver.Active) PhysicsDriver.ReturnHome();
            return previous;
        }

        public void DriveTo(Vector3 destination)
        {
            ClearRoute();
            if (PhysicsDriver!=null && PhysicsDriver.Active) { PhysicsDriver.DriveTo(destination); return; }
            // The shared central aisle is x = -1; tables are beyond x = 5.
            // Two lane changes avoid crossing the 3D dining tables.
            float y = transform.position.y;
            Vector3 pivot1 = new Vector3(-1f, y, transform.position.z);
            Vector3 pivot2 = new Vector3(-1f, y, destination.z);
            if (Mathf.Abs(transform.position.x - pivot1.x) > 0.4f) _route.Add(pivot1);
            if (Vector3.Distance(_route.Count > 0 ? _route[_route.Count - 1] : transform.position, pivot2) > 0.25f)
                _route.Add(pivot2);
            _route.Add(new Vector3(destination.x, y, destination.z));
            _routeIndex = 0;
        }

        private void ClearRoute() { _route.Clear(); _routeIndex = 0; }

        private void Update()
        {
            if (PhysicsDriver!=null && PhysicsDriver.Active) return;
            if (EmergencyStopped) return;
            if (_routeIndex >= _route.Count) return;
            Vector3 target = _route[_routeIndex];
            Vector3 delta = target - transform.position;
            if (delta.sqrMagnitude <= 0.04f)
            {
                transform.position = target;
                _routeIndex++;
                if (_routeIndex >= _route.Count)
                {
                    ClearRoute();
                    if (Arrived != null) Arrived(this);
                }
                return;
            }
            Vector3 direction = delta.normalized;
            transform.position = Vector3.MoveTowards(transform.position, target, Speed * Time.deltaTime);
            if (direction.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(direction, Vector3.up), 7f * Time.deltaTime);
        }

        internal void NotifyArrived() { Arrived?.Invoke(this); }

        private void CreateCargo(DeliveryKind kind)
        {
            ClearCargo();
            _cargo = new GameObject(kind + " Cargo Display");
            _cargo.transform.SetParent(transform, false);
            _cargo.transform.localPosition = new Vector3(0f, 1.35f, 0.35f);
            // Actual 3D dish meshes travel with the robot, rather than a flat icon.
            CargoShape(_cargo.transform, "White Serving Plate", PrimitiveType.Cylinder,
                new Vector3(0, 0, 0), new Vector3(0.39f, 0.035f, 0.32f),
                new Color(0.94f, 0.94f, 0.88f));
            if (kind == DeliveryKind.Soup)
            {
                CargoShape(_cargo.transform, "Soup Bowl", PrimitiveType.Cylinder,
                    new Vector3(0, 0.11f, 0), new Vector3(0.26f, 0.08f, 0.26f),
                    new Color(0.94f, 0.91f, 0.81f));
                CargoShape(_cargo.transform, "Soup Surface", PrimitiveType.Cylinder,
                    new Vector3(0, 0.20f, 0), new Vector3(0.22f, 0.012f, 0.22f),
                    new Color(0.84f, 0.50f, 0.18f));
            }
            else if (kind == DeliveryKind.Food)
            {
                CargoShape(_cargo.transform, "Main Dish", PrimitiveType.Cube,
                    new Vector3(-0.06f, 0.09f, 0), new Vector3(0.39f, 0.12f, 0.23f),
                    new Color(0.78f, 0.42f, 0.24f));
                CargoShape(_cargo.transform, "Greens", PrimitiveType.Sphere,
                    new Vector3(0.17f, 0.14f, 0.06f), new Vector3(0.16f, 0.12f, 0.14f),
                    new Color(0.28f, 0.53f, 0.28f));
            }
            else
            {
                CargoShape(_cargo.transform, "Empty Dish 1", PrimitiveType.Cylinder,
                    new Vector3(0.04f, 0.075f, 0), new Vector3(0.28f, 0.027f, 0.25f),
                    new Color(0.90f, 0.91f, 0.90f));
                CargoShape(_cargo.transform, "Empty Dish 2", PrimitiveType.Cylinder,
                    new Vector3(0.04f, 0.14f, 0), new Vector3(0.25f, 0.027f, 0.23f),
                    new Color(0.90f, 0.91f, 0.90f));
            }
        }

        private static void CargoShape(Transform parent, string name, PrimitiveType shape,
            Vector3 position, Vector3 scale, Color color)
        {
            GameObject obj = GameObject.CreatePrimitive(shape);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localScale = scale;
            Collider collider = obj.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            Renderer renderer = obj.GetComponent<Renderer>();
            if (renderer == null) return;
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            Material mat = new Material(shader);
            mat.color = color;
            renderer.sharedMaterial = mat;
        }

        private void ClearCargo()
        {
            if (_cargo == null) return;
            foreach (Renderer renderer in _cargo.GetComponentsInChildren<Renderer>(true))
                if (renderer.sharedMaterial != null) Destroy(renderer.sharedMaterial);
            Destroy(_cargo);
            _cargo = null;
        }
    }
}
