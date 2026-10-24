using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Guardian.UnitySim
{
    /// <summary>Runtime demonstrator: evidence -> capability -> task eligibility -> reassignment.</summary>
    public sealed partial class GuardianSimulator : MonoBehaviour
    {
        private readonly List<GuardianRobot> _robots = new List<GuardianRobot>();
        private readonly List<DeliveryTask> _tasks = new List<DeliveryTask>();
        private readonly List<GuardianTaskRequest> _requests=new List<GuardianTaskRequest>();
        public IReadOnlyList<GuardianTaskRequest> Requests=>_requests;
        private readonly List<string> _events = new List<string>();
        private readonly Dictionary<string, Vector3> _starts = new Dictionary<string, Vector3>();
        private Camera _camera;
        private float _demoClock;
        private bool _autoDemo = true;
        private bool _demoTriggered;
        private GUIStyle _labelStyle;
        private bool _showTelemetry = true;
        private bool _storyTriggered;
        [SerializeField] private bool _storyMode = true;
        private const float StoryDegradationSeconds = 18f;
        private const float StoryDurationSeconds = 40f;
        [SerializeField] private GuardianSimulationMode _mode=GuardianSimulationMode.Physics;
        public GuardianSimulationSettings Settings=new GuardianSimulationSettings();
        public bool AutomaticTasks=true;
        public event Action<float> PhysicsStepBeginning;
        public bool ReserveFleet;
        public GuardianFleetOperator Fleet { get; private set; }
        private Vector2 _physicsScroll;
        private GuardianTrafficController _traffic;
        private GuardianPhysicsWorld _physicsWorld;
        private GuardianPedestrianSystem _pedestrianSystem;
        private GuardianInteractionPanel _interactionPanel;
        private GuardianDishInventory _inventory;
        private System.Random _workloadRandom,_faultRandom;
        private float _nextTask,_nextFault;
        private int _generated,_selected;
        public GuardianSimulationMode Mode=>_mode;
        public GuardianDishInventory Inventory=>_mode==GuardianSimulationMode.Physics?_inventory:null;
        public IReadOnlyList<GuardianRobot> Robots=>_robots;
        public IReadOnlyList<DeliveryTask> Tasks=>_tasks;
        public GuardianTrafficController Traffic=>_traffic;
        public GuardianPhysicsWorld PhysicsWorld=>_physicsWorld;
        public int RejectedWorkload { get; private set; }
        public int ScenarioRevision { get; private set; }
        public float DemoClock { get { return _demoClock; } }
        public bool StoryMode { get { return _mode==GuardianSimulationMode.LegacyStory && _storyMode; } }
        public string StoryScene {
            get {
                if (_mode==GuardianSimulationMode.Physics) return "PHYSICS / SINGLE-TRACK RESERVATIONS";
                if (_demoClock < 8f) return "SCENE 1  /  FLEET OPERATING";
                if (_demoClock < 18f) return "SCENE 2  /  CAPABILITY LEVELS";
                if (_demoClock < 27f) return "SCENE 3  /  EVIDENCE & REASSIGNMENT";
                return "SCENE 4  /  NEW WORK ALLOCATION";
            }
        }


        private static readonly Vector3 Kitchen = new Vector3(-6.2f, 0.34f, 3.6f);
        private static readonly Vector3 ReturnStation = new Vector3(-6.2f, 0.34f, -3.6f);
        private static readonly Vector3[] Tables = {
            new Vector3(3.6f, 0.34f, -3.4f),
            new Vector3(3.6f, 0.34f, 0.0f),
            new Vector3(3.6f, 0.34f, 3.4f)
        };

        private void Start()
        {
            EnsureWorld();
            string[] arguments=Environment.GetCommandLineArgs();
            if (GetComponent<GuardianCapture>() == null) gameObject.AddComponent<GuardianCapture>();
            if (arguments.Contains("-guardianVerify")) _mode=GuardianSimulationMode.LegacyStory;
            Settings=GuardianSettingsLoader.LoadOverrideOrKeep(Settings);
            if (arguments.Contains("-guardianPedestrianDemo")) Settings.PedestrianDemoEnabled=true;
            ResetScenario();
            if (arguments.Contains("-guardianInteractionDemo"))
            {
                _interactionPanel=GetComponent<GuardianInteractionPanel>()??gameObject.AddComponent<GuardianInteractionPanel>();
                _interactionPanel.Initialize(this);
            }
            if (arguments.Contains("-guardianPhysicsTest") && GetComponent<GuardianPhysicsVerification>()==null)
                gameObject.AddComponent<GuardianPhysicsVerification>();
        }

        /// <summary>Called by the editor scene generator as well as runtime fallback.</summary>
        public void EnsureWorld()
        {
            if (transform.Find("Generated 3D World") != null) return;
            Transform world = new GameObject("Generated 3D World").transform;
            world.SetParent(transform, false);
            // Scenery is independent of actors, camera and policy.
            RestaurantVisuals.Build(world);

            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(world);
            cameraObject.transform.position = new Vector3(0.8f, 15.6f, -14.1f);
            cameraObject.transform.LookAt(new Vector3(0, 0.8f, 0));
            _camera = cameraObject.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.76f, 0.85f, 0.91f);
            _camera.fieldOfView = 50f;
            cameraObject.AddComponent<AudioListener>();

            GameObject lightObject = new GameObject("Sun Light");
            lightObject.transform.SetParent(world);
            lightObject.transform.rotation = Quaternion.Euler(48, -35, 0);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;

            CreateRobot(world, "A", new Vector3(-4.2f, 0.34f, -4.8f));
            CreateRobot(world, "B", new Vector3(-4.2f, 0.34f, 0.0f));
            CreateRobot(world, "C", new Vector3(-4.2f, 0.34f, 4.8f));
        }

        private void CreateRobot(Transform parent, string id, Vector3 position)
        {
            GameObject root = new GameObject("Robot " + id);
            root.transform.SetParent(parent);
            root.transform.position = position;
            GuardianRobot robot = root.AddComponent<GuardianRobot>();
            robot.RobotId = id;
            _starts[id] = position;

            // Clone only the existing loaded visual subtree. Private model data
            // stays in local scene/build references, never added to Git.
            if (Application.isPlaying)
            {
                var template=GetComponentsInChildren<GuardianRobot>(true).FirstOrDefault(r=>r.RobotId=="A" && r!=robot);
                var source=template==null?null:template.transform.Find(NekobotVisualFactory.VisualRootName);
                if (source!=null) { var copy=Instantiate(source.gameObject,root.transform,false); copy.name=NekobotVisualFactory.VisualRootName; return; }
            }
            // All 3 robots share the same visual asset. The motion component stays
            // on this actor root; the imported model is only a child renderer.
            Transform visual = new GameObject(NekobotVisualFactory.VisualRootName).transform;
            visual.SetParent(root.transform, false);
            if (!NekobotVisualFactory.TryAttachLocalModel(visual, root.transform))
                BuildPlaceholder(visual);

        }

        private static void BuildPlaceholder(Transform robot)
        {
            CreatePart(robot, "Wheel Base", PrimitiveType.Cylinder,
                new Vector3(0, 0.26f, 0), new Vector3(0.66f, 0.25f, 0.66f),
                new Color(0.20f, 0.25f, 0.33f));
            CreatePart(robot, "Body", PrimitiveType.Cube,
                new Vector3(0, 0.91f, 0), new Vector3(0.78f, 1.08f, 0.70f),
                Color.white);
            CreatePart(robot, "Cat Face", PrimitiveType.Cube,
                new Vector3(0, 1.64f, 0.14f), new Vector3(0.75f, 0.53f, 0.47f),
                new Color(0.13f, 0.18f, 0.23f));
            CreatePart(robot, "Left Ear", PrimitiveType.Cube,
                new Vector3(-0.27f, 1.99f, 0.15f), new Vector3(0.20f, 0.26f, 0.30f),
                Color.white);
            CreatePart(robot, "Right Ear", PrimitiveType.Cube,
                new Vector3(0.27f, 1.99f, 0.15f), new Vector3(0.20f, 0.26f, 0.30f),
                Color.white);
            CreatePart(robot, "Delivery Tray", PrimitiveType.Cube,
                new Vector3(0, 1.26f, 0.40f), new Vector3(0.95f, 0.10f, 0.64f),
                new Color(0.72f, 0.80f, 0.87f));
            // A generic self-created cat-face screen, not the licensed BOOTH model.
            CreatePart(robot, "Screen Left Eye", PrimitiveType.Sphere,
                new Vector3(-0.17f, 1.68f, 0.39f), new Vector3(0.12f, 0.13f, 0.025f),
                new Color(0.31f, 0.87f, 0.93f));
            CreatePart(robot, "Screen Right Eye", PrimitiveType.Sphere,
                new Vector3(0.17f, 1.68f, 0.39f), new Vector3(0.12f, 0.13f, 0.025f),
                new Color(0.31f, 0.87f, 0.93f));
            CreatePart(robot, "Screen Mouth", PrimitiveType.Sphere,
                new Vector3(0, 1.52f, 0.39f), new Vector3(0.13f, 0.035f, 0.025f),
                new Color(0.31f, 0.87f, 0.93f));
        }

        internal static GameObject CreatePart(Transform parent, string name, PrimitiveType primitive,
            Vector3 localPosition, Vector3 localScale, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = localScale;
            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = MaterialFor(color, name);
            return part;
        }

        private static Material MaterialFor(Color color, string name)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (!AssetDatabase.IsValidFolder("Assets/GeneratedMaterials"))
                    AssetDatabase.CreateFolder("Assets", "GeneratedMaterials");
                string key = ColorUtility.ToHtmlStringRGB(color);
                string path = "Assets/GeneratedMaterials/Color_" + key + ".mat";
                Material saved = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (saved != null) return saved;
                saved = new Material(shader);
                saved.color = color;
                AssetDatabase.CreateAsset(saved, path);
                return saved;
            }
#endif
            Material material = new Material(shader);
            material.color = color;
            return material;
        }

        public void ResetScenario()
        {
            if (Settings==null) Settings=new GuardianSimulationSettings(); Settings.Validate();
            ScenarioRevision++;
            _robots.Clear();
            _tasks.Clear(); _requests.Clear();
            _events.Clear();
            _demoClock = 0f;
            _demoTriggered = false;
            _storyTriggered = false;
            if (_camera == null) _camera = Camera.main;
            if (_traffic==null) _traffic=GetComponent<GuardianTrafficController>()??gameObject.AddComponent<GuardianTrafficController>();
            if (_physicsWorld==null) _physicsWorld=GetComponent<GuardianPhysicsWorld>()??gameObject.AddComponent<GuardianPhysicsWorld>();
            if (_pedestrianSystem==null) _pedestrianSystem=GetComponent<GuardianPedestrianSystem>();
            if (_pedestrianSystem==null && Settings.PedestrianDemoEnabled) _pedestrianSystem=gameObject.AddComponent<GuardianPedestrianSystem>();
            _traffic.ResetTraffic(this,Settings.Seed);
            if (_pedestrianSystem!=null && _pedestrianSystem.Configure(this,_mode==GuardianSimulationMode.Physics && Settings.PedestrianDemoEnabled))
                _physicsWorld.Invalidate();
            _physicsWorld.Ensure(this,_mode==GuardianSimulationMode.Physics);
            if (_inventory!=null) _inventory.ClearInventory();
            Transform world=transform.Find("Generated 3D World");
            if (_mode==GuardianSimulationMode.Physics && ReserveFleet)
                foreach (string id in new[]{"D","E"})
                    if (!GetComponentsInChildren<GuardianRobot>(true).Any(r=>r.RobotId==id)) CreateRobot(world,id,new Vector3(-4.8f,.34f,id=="D"?-1.7f:1.7f));
            foreach (GuardianRobot robot in GetComponentsInChildren<GuardianRobot>(true))
            {
                bool spare=robot.RobotId=="D"||robot.RobotId=="E";
                robot.gameObject.SetActive(!spare || (_mode==GuardianSimulationMode.Physics && ReserveFleet));
                if (!robot.gameObject.activeSelf) continue;
                robot.Arrived -= OnRobotArrived;
                robot.Arrived += OnRobotArrived;
                _robots.Add(robot);
                RobotCondition condition = robot.RobotId == "A" ? RobotCondition.Normal :
                    robot.RobotId == "B" ? RobotCondition.Warning : spare?RobotCondition.Normal:RobotCondition.Limited;
                float vibration = robot.RobotId == "A" ? 0.12f : robot.RobotId == "B" ? 0.45f : spare?0.12f:0.82f;
                if (!_starts.ContainsKey(robot.RobotId)) _starts[robot.RobotId] = robot.transform.position;
                var motor=robot.GetComponent<GuardianPhysicsDriver>()??robot.gameObject.AddComponent<GuardianPhysicsDriver>();
                motor.Configure(robot,_traffic,Settings,_mode==GuardianSimulationMode.Physics);
                Vector3 home=_starts[robot.RobotId];
                if (_mode==GuardianSimulationMode.Physics)
                {
                    home=new Vector3(spare?-4.8f:-4.2f,motor.PlaneY,robot.RobotId=="A"?-5.25f:robot.RobotId=="B"?0:robot.RobotId=="C"?5.25f:robot.RobotId=="D"?-1.7f:1.7f);
                    // A larger owned fallback must be able to turn at the north bay.
                    // Derive clearance from the actual envelope and wall geometry.
                    if (robot.RobotId=="C" && _physicsWorld.Obstacles.FirstOrDefault(c=>c.name=="Beige back wall") is BoxCollider wall)
                    {
                        float wallZ=float.PositiveInfinity; Vector3 half=wall.size*.5f;
                        for (int x=-1;x<=1;x+=2) for (int y=-1;y<=1;y+=2) for (int z=-1;z<=1;z+=2)
                            wallZ=Mathf.Min(wallZ,wall.transform.TransformPoint(wall.center+Vector3.Scale(half,new Vector3(x,y,z))).z);
                        float radius=0; Vector3 bodyHalf=motor.Shape.size*.5f;
                        for (int x=-1;x<=1;x+=2) for (int z=-1;z<=1;z+=2)
                            radius=Mathf.Max(radius,new Vector2(motor.Shape.center.x+x*bodyHalf.x,motor.Shape.center.z+z*bodyHalf.z).magnitude);
                        home.z=Mathf.Min(home.z,wallZ-.02f-radius);
                    }
                }
                robot.ResetRobot(home, condition, vibration);
                if (robot.GetComponent<GuardianStatusIndicator>()==null) robot.gameObject.AddComponent<GuardianStatusIndicator>();
            }
            if (Fleet==null) Fleet=GetComponent<GuardianFleetOperator>()??gameObject.AddComponent<GuardianFleetOperator>();
            Fleet.ResetOperator(this);
            if (_mode==GuardianSimulationMode.Physics)
            {
                _inventory=GetComponent<GuardianDishInventory>()??gameObject.AddComponent<GuardianDishInventory>(); _inventory.ResetInventory(this);
                _workloadRandom=new System.Random(Settings.Seed); _faultRandom=new System.Random(Settings.Seed^0x37ab);
                _generated=0; RejectedWorkload=0; _nextTask=Range(_workloadRandom,.5f,2); _nextFault=Range(_faultRandom,Settings.MinimumFaultInterval,Settings.MaximumFaultInterval);
                Log("Physics reset; seed="+Settings.Seed+"; dynamic bodies; local branch/service point and narrow aisle reservations");
                return;
            }
            _tasks.Add(new DeliveryTask("S-101", DeliveryKind.Soup, Kitchen, Tables[0]));
            _tasks.Add(new DeliveryTask("F-102", DeliveryKind.Food, Kitchen, Tables[1]));
            _tasks.Add(new DeliveryTask("R-103", DeliveryKind.EmptyDishes, Tables[2], ReturnStation));
            // Three initial tasks are fixed so Robot A/B/C display different
            // capabilities immediately. Follow-on work arrives at t=18s.
            Log("Scenario initialized: 3 robot actors");
            Replan();
        }

        private static bool CanExecute(RobotCondition condition, DeliveryKind kind)
        {
            return GuardianCapabilityPolicy.Allows(condition,kind);
        }

        public void SubmitVibration(string robotId, float value)
        {
            GuardianRobot robot = _robots.Find(r => r.RobotId == robotId);
            if (robot == null) return;
            robot.Vibration = Mathf.Clamp01(value);
            // Demo thresholds are illustrative policy, not a certified safety limit.
            RobotCondition next = GuardianCapabilityPolicy.FromVibration(robot.Vibration,Settings);
            if (robot.Condition == next) return;
            robot.Condition = next;
            Log(robot.RobotId + " -> " + next + " (evidence changed)");
            RevokeIneligible(robot);
            Replan();
        }

        private void RevokeIneligible(GuardianRobot robot)
        {
            if (robot.CurrentTask != null && (!robot.DispatchEnabled || !GuardianCapabilityPolicy.Allows(robot.Condition,robot.CurrentTask.Kind,robot.EmergencyStopped))
                && robot.CurrentTask.RevokedVersion!=robot.CurrentTask.AssignmentVersion)
            {
                robot.CurrentTask.RevokedVersion=robot.CurrentTask.AssignmentVersion;
                if (robot.Carrying)
                {
                    Log(robot.RobotId + " returning cargo to handoff point");
                    robot.ReturnCargoToPickup();
                }
                else
                {
                    DeliveryTask task = robot.ReleaseTask();
                    if (_mode==GuardianSimulationMode.Physics) _traffic.ReleaseJob(task);
                    Inventory?.ReleaseReservation(task);
                    task.AssignedTo = null;
                    task.Phase = TaskPhase.Pending;
                    Log(task.Id + " permit revoked; task pending");
                }
            }
        }

        private void Replan()
        {
            ProgressSameVisitPickups();
            foreach (DeliveryTask task in _tasks)
            {
                if (task.Phase != TaskPhase.Pending) continue;
                if(_mode==GuardianSimulationMode.Physics && (Settings.CollectEmptyOnDelivery || _tasks.Any(t=>t.FixedDishBatch && t.Phase!=TaskPhase.Completed)) && ReturningSourceBlocks(task))
                { task.PendingReason="Waiting for cargo source return at this service point"; continue; }
                GuardianRobot[] eligible=_robots.Where(r=>r.DispatchEnabled && GuardianCapabilityPolicy.Allows(r.Condition,task.Kind,r.EmergencyStopped)).ToArray();
                task.PendingReason=eligible.Length==0?"All robots ineligible for "+task.Kind:"Waiting for eligible idle robot";
                GuardianRobot best = null;
                float bestCost = float.MaxValue;
                foreach (GuardianRobot robot in _robots)
                {
                    if (!robot.DispatchEnabled || !robot.IsIdle || !GuardianCapabilityPolicy.Allows(robot.Condition, task.Kind,robot.EmergencyStopped)) continue;
                    // Prefer to preserve higher capability actors for sensitive tasks.
                    float scarcityPenalty = task.Kind == DeliveryKind.EmptyDishes && robot.Condition == RobotCondition.Normal ? 5f : 0f;
                    float cost = Vector3.Distance(robot.transform.position, task.Pickup) + scarcityPenalty;
                    if (cost < bestCost) { best = robot; bestCost = cost; }
                }
                if (best == null) continue;
                if (_mode==GuardianSimulationMode.Physics && !_traffic.CanAssign(task))
                { task.PendingReason="Waiting for local pickup/dropoff service point"; continue; }
                if (Inventory!=null && !Inventory.Reserve(task)) continue;
                if (_mode==GuardianSimulationMode.Physics) _traffic.ClaimJob(task);
                best.Assign(task);
                Log(task.Id + " (" + task.Kind + ") -> Robot " + best.RobotId);
            }
        }

        private void OnRobotArrived(GuardianRobot robot)
        {
            DeliveryTask task = robot.CurrentTask;
            if (task == null) return;
            Vector3 endpoint=task.Phase==TaskPhase.ToDropoff?task.Destination:task.Pickup;
            if(_mode==GuardianSimulationMode.Physics && !robot.PhysicsDriver.AtServicePoint(endpoint))
            { Log("Ignored stale arrival for "+task.Id+" away from its physical service point"); return; }
            if (task.Phase == TaskPhase.ToPickup)
            {
                if(task.AwaitingServicePickup) ProgressSameVisitPickups(); else robot.PickUp();
            }
            else if (task.Phase == TaskPhase.ReturningCargo)
            {
                Inventory?.ReturnToSource(task);
                if (_mode==GuardianSimulationMode.Physics) _traffic.ReleaseJob(task);
                robot.ReleaseTask();
                task.AssignedTo = null;
                task.Phase = TaskPhase.Pending;
                Log(task.Id + " handed off; eligibility changed");
                Replan();
            }
            else if (task.Phase == TaskPhase.ToDropoff)
            {
                Inventory?.Complete(task);
                task.Phase = TaskPhase.Completed; task.CompletedAt=DemoClock;
                Log(task.Id + " completed by Robot " + robot.RobotId);
                if(!StartSameVisitReturn(robot,task))
                {
                    if (_mode==GuardianSimulationMode.Physics) _traffic.ReleaseJob(task);
                    robot.ReleaseTask();
                }
                Replan();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F7)) { SetMode(_mode==GuardianSimulationMode.Physics?GuardianSimulationMode.LegacyStory:GuardianSimulationMode.Physics); return; }
            if (_mode==GuardianSimulationMode.Physics) { PhysicsInput(); return; }
            _demoClock += Time.deltaTime;
            if (_storyMode && !_storyTriggered && _demoClock >= StoryDegradationSeconds)
            {
                _storyTriggered = true;
                // B was WARNING, hence suitable for food in the original plan.
                // Its next food job is no longer eligible after new evidence.
                Log("Robot B: next FOOD_DELIVERY planned");
                SubmitVibration("B", 0.85f);
                Log("Vibration increased: B WARNING -> LIMITED");
                Log("Capability updated: B FOOD_DELIVERY restricted");
                Log("Task reassignment triggered: FOOD -> eligible A");
                _tasks.Add(new DeliveryTask("F-204", DeliveryKind.Food, Kitchen, Tables[2]));
                _tasks.Add(new DeliveryTask("R-205", DeliveryKind.EmptyDishes, Tables[0], ReturnStation));
                _tasks.Add(new DeliveryTask("R-206", DeliveryKind.EmptyDishes, Tables[1], ReturnStation));
                Replan();
            }
            else if (!_storyMode && _autoDemo && !_demoTriggered && _demoClock >= 11.5f)
            {
                _demoTriggered = true;
                SubmitVibration("B", 0.85f);
            }
            if (Input.GetKeyDown(KeyCode.R)) ResetScenario();
            if (Input.GetKeyDown(KeyCode.F8)) { _storyMode = !_storyMode; ResetScenario(); }
            if (Input.GetKeyDown(KeyCode.H)) _showTelemetry = !_showTelemetry;
            if (Input.GetKeyDown(KeyCode.Alpha1)) SubmitVibration("A", 0.47f);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SubmitVibration("B", 0.87f);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SubmitVibration("C", 0.12f);
        }

        public void SetReserveFleet(bool enabled) { ReserveFleet=enabled; SetMode(GuardianSimulationMode.Physics); }
        public void SetDispatchEnabled(GuardianRobot robot,bool enabled)
        { robot.DispatchEnabled=enabled; if (!enabled) RevokeIneligible(robot); Replan(); }
        public void SetMode(GuardianSimulationMode mode) { _mode=mode; ResetScenario(); }
        public void SetCondition(string id,RobotCondition condition)
        {
            float value=condition==RobotCondition.Normal?Settings.WarningThreshold*.4f:
                condition==RobotCondition.Warning?(Settings.WarningThreshold+Settings.LimitedThreshold)*.5f:
                Mathf.Lerp(Settings.LimitedThreshold,1,.5f);
            SubmitVibration(id,value);
        }
        public void SetEmergencyStop(string id,bool stopped)
        {
            GuardianRobot robot=_robots.Find(r=>r.RobotId==id); if (robot==null || robot.EmergencyStopped==stopped) return;
            robot.EmergencyStopped=stopped; Log(id+(stopped?" EMERGENCY STOP":" mobility recovered")); RevokeIneligible(robot); Replan();
        }
        public void PickupUnavailable(GuardianRobot robot)
        {
            DeliveryTask task=robot.ReleaseTask(); Inventory?.ReleaseReservation(task); _traffic.ReleaseJob(task);
            task.AssignedTo=null; task.Phase=TaskPhase.Pending; task.PendingReason="Pickup source unavailable";
            Log(task.Id+" waiting: no valid source dish");
        }
        public DeliveryTask QueueTask(DeliveryKind kind,int table,GuardianMenu menu=GuardianMenu.CurryRice)
        {
            if (_mode!=GuardianSimulationMode.Physics) throw new InvalidOperationException("QueueTask requires Physics mode");
            if (table<0 || table>=Tables.Length) throw new ArgumentOutOfRangeException(nameof(table));
            var request=new GuardianTaskRequest{Id=_requests.Count+1,Kind=kind,TableIndex=table,ReceivedAt=DemoClock}; _requests.Add(request);
            if (kind==DeliveryKind.EmptyDishes)
            {
                int available=Inventory.Items.Values.Count(i=>i.Dirty && i.Owner==GuardianDishOwner.Table && i.OwnerKey==table.ToString() && string.IsNullOrEmpty(i.ReservedBy));
                var future=_tasks.Where(t=>t.Kind==kind && t.TableIndex==table && t.Phase==TaskPhase.Pending && t.DishIds.Count==0).ToArray();
                DeliveryTask covered=null;
                if (available>0 && future.Length*GuardianDishInventory.TrayCapacity>=available) covered=future.Last();
                if (available==0) covered=_tasks.FirstOrDefault(t=>t.Kind==kind && t.TableIndex==table
                    && (t.Phase==TaskPhase.ToPickup || t.Phase==TaskPhase.Pending) && t.DishIds.Count>0
                    && t.DishIds.All(id=>Inventory.Items[id].Owner==GuardianDishOwner.Table && Inventory.Items[id].Dirty));
                if (covered!=null)
                { request.Task=covered; request.Coalesced=true; request.Decision="Included in unpicked batch "+covered.Id; Log("Request #"+request.Id+": "+request.Decision); return covered; }
                if (available==0)
                { request.Decision="No unreserved empty dishes at table "+(table+1)+"; carried batches do not cover future requests"; Log("Request #"+request.Id+" rejected: "+request.Decision); return null; }
            }
            if (_tasks.Count(t=>t.Phase!=TaskPhase.Completed)>=Settings.MaximumOpenTasks)
            { RejectedWorkload++; request.Decision="Open-task capacity reached; request not accepted"; Log("Request #"+request.Id+" rejected: "+request.Decision); return null; }
            if (kind==DeliveryKind.Soup) menu=GuardianMenu.Soup;
            if (kind==DeliveryKind.Food && GuardianDishInventory.KindOf(menu)!=kind) throw new ArgumentException("Menu classification mismatch");
            Vector3 kitchen=Kitchen+Vector3.right*.2f,returns=ReturnStation+Vector3.right*.2f;
            var task=new DeliveryTask((kind==DeliveryKind.Soup?"S":kind==DeliveryKind.Food?"F":"R")+"-"+(++_generated).ToString("D4"),
                kind,kind==DeliveryKind.EmptyDishes?Tables[table]:kitchen,kind==DeliveryKind.EmptyDishes?returns:Tables[table])
                {Menu=menu,TableIndex=table,CreatedAt=DemoClock};
            _tasks.Add(task); request.Task=task; request.Decision="Accepted as "+task.Id; Inventory.Prepare(task);
            Log("Generated "+task.Id+" "+kind+" "+menu+" table="+(table+1)); Replan(); return task;
        }
        private static float Range(System.Random random,float minimum,float maximum)
        { return minimum+(float)random.NextDouble()*(maximum-minimum); }
        private void FixedUpdate()
        {
            if (_mode!=GuardianSimulationMode.Physics || _traffic==null) return;
            float dt=Time.fixedDeltaTime; _demoClock+=dt; Inventory.Tick(); PhysicsStepBeginning?.Invoke(_demoClock);
            if (AutomaticTasks && _demoClock>=_nextTask)
            {
                DeliveryKind kind=(DeliveryKind)_workloadRandom.Next(3);
                int table=_workloadRandom.Next(3); GuardianMenu menu=(GuardianMenu)_workloadRandom.Next(1,4);
                if (kind==DeliveryKind.EmptyDishes)
                {
                    var ready=Inventory.Items.Values.Where(i=>i.Dirty && i.Owner==GuardianDishOwner.Table && string.IsNullOrEmpty(i.ReservedBy)).ToArray();
                    if (ready.Length==0) kind=DeliveryKind.Food;
                    else table=int.Parse(ready[_workloadRandom.Next(ready.Length)].OwnerKey);
                }
                QueueTask(kind,table,menu); _nextTask+=Range(_workloadRandom,Settings.MinimumTaskInterval,Settings.MaximumTaskInterval);
            }
            if (Settings.RandomFaults && _demoClock>=_nextFault)
            {
                GuardianRobot robot=_robots[_faultRandom.Next(_robots.Count)]; RobotCondition next=(RobotCondition)_faultRandom.Next(3);
                Log("Seeded condition event "+robot.RobotId+" -> "+next); SetCondition(robot.RobotId,next);
                _nextFault+=Range(_faultRandom,Settings.MinimumFaultInterval,Settings.MaximumFaultInterval);
            }
            Replan(); _traffic.Tick();
            _pedestrianSystem?.Tick(dt);
            foreach (GuardianRobot robot in _robots) robot.PhysicsDriver.Tick(dt);
            Fleet.Tick();
        }
        private void PhysicsInput()
        {
            if (Input.GetKeyDown(KeyCode.R)) { ResetScenario(); return; }
            if (Input.GetKeyDown(KeyCode.H)) _showTelemetry=!_showTelemetry;
            if (Input.GetKeyDown(KeyCode.Alpha1)) _selected=0;
            if (Input.GetKeyDown(KeyCode.Alpha2)) _selected=1;
            if (Input.GetKeyDown(KeyCode.Alpha3)) _selected=2;
            if (_robots.Count==0) return;
            GuardianRobot robot=_robots[Mathf.Clamp(_selected,0,_robots.Count-1)];
            if (Input.GetKeyDown(KeyCode.N)) SetCondition(robot.RobotId,RobotCondition.Normal);
            if (Input.GetKeyDown(KeyCode.W)) SetCondition(robot.RobotId,RobotCondition.Warning);
            if (Input.GetKeyDown(KeyCode.L)) SetCondition(robot.RobotId,RobotCondition.Limited);
            if (Input.GetKeyDown(KeyCode.E)) SetEmergencyStop(robot.RobotId,!robot.EmergencyStopped);
        }
        private Vector2 guiScreenOrigin;
        private GUISkin physicsSkin;
        private Texture2D panelTexture;
        private bool cameraDefaultsSaved;
        private Rect savedViewport;
        private float savedFov;
        private string seedText;
        private int PanelWidth=>Mathf.Clamp(Mathf.RoundToInt(Screen.width*.29f),350,460);
        private void EnsurePhysicsSkin()
        {
            if (physicsSkin!=null) return;
            physicsSkin=Instantiate(GUI.skin); physicsSkin.label.fontSize=13; physicsSkin.label.wordWrap=true; physicsSkin.button.fontSize=13; physicsSkin.box.fontSize=14;
            panelTexture=new Texture2D(1,1,TextureFormat.RGBA32,false); panelTexture.SetPixel(0,0,new Color(.055f,.075f,.10f,1)); panelTexture.Apply();
            physicsSkin.box.normal.background=panelTexture;
        }
        private void LateUpdate()
        {
            if (_camera==null) return;
            if (!cameraDefaultsSaved) { savedViewport=_camera.rect; savedFov=_camera.fieldOfView; cameraDefaultsSaved=true; }
            bool panel=_mode==GuardianSimulationMode.Physics && _showTelemetry;
            _camera.rect=panel?new Rect(0,0,Mathf.Max(.45f,(Screen.width-PanelWidth-20f)/Screen.width),1):savedViewport;
            _camera.fieldOfView=panel?Mathf.Max(savedFov,58):savedFov;
        }
        private void OnDestroy() { if (physicsSkin!=null) Destroy(physicsSkin); if (panelTexture!=null) Destroy(panelTexture); }

        internal Vector2 ModeButtonClientPoint { get; private set; }
        internal int PanelModeActivations { get; private set; }
        private readonly Dictionary<string,Vector2> nativePoints=new Dictionary<string,Vector2>();
        private readonly Dictionary<string,int> nativeClicks=new Dictionary<string,int>();
        internal string SeedInputValue=>seedText;
        internal int NativeClickCount(string key)=>nativeClicks.TryGetValue(key,out int n)?n:0;
        internal Vector2 NativePoint(string key)=>nativePoints.TryGetValue(key,out var value)?value:new Vector2(-1,-1);
        internal bool PrepareNativeControl(string key)
        {
            if(!nativePoints.TryGetValue(key,out var point)) return false;
            if(point.y>Screen.height-50 || point.y<20) _physicsScroll.y=Mathf.Max(0,_physicsScroll.y+point.y-Screen.height*.55f);
            return true;
        }
        private bool ControlButton(string key,string caption,float height=22)
        {
            Rect rect=GUILayoutUtility.GetRect(new GUIContent(caption),GUI.skin.button,GUILayout.Height(height));
            if(Event.current.type==EventType.Repaint) nativePoints[key]=GUIUtility.GUIToScreenPoint(rect.center)-guiScreenOrigin;
            bool clicked=GUI.Button(rect,caption);
            if(clicked) { nativeClicks[key]=NativeClickCount(key)+1; Debug.Log("[Guardian] Native UI control "+key+" activated"); }
            return clicked;
        }
        private bool ModePanelButton(string caption)
        {
            bool clicked=ControlButton("mode",caption,26); ModeButtonClientPoint=NativePoint("mode"); if(clicked) PanelModeActivations++; return clicked;
        }
        private void PhysicsGUI()
        {
            int width=PanelWidth;
            GUILayout.BeginArea(new Rect(Screen.width-width-10,10,width,Screen.height-20),GUI.skin.box);
            _physicsScroll=GUILayout.BeginScrollView(_physicsScroll);
            GUILayout.Label("PHYSICAL AI GUARDIAN / PHYSICS",GUI.skin.box);
            if (ModePanelButton("40s legacy regression mode (F7)"))
            { GUILayout.EndScrollView(); GUILayout.EndArea(); SetMode(GuardianSimulationMode.LegacyStory); return; }
            if (GUILayout.Button(ReserveFleet?"Fleet: five bays (reset to three)":"Fleet: three bays (reset to 3 + 2 reserves)")) SetReserveFleet(!ReserveFleet);
            GUILayout.Label("Seed "+Settings.Seed+" | "+DemoClock.ToString("F1")+"s | dynamic bodies");
            GUILayout.BeginHorizontal(); if (seedText==null) seedText=Settings.Seed.ToString(); seedText=GUILayout.TextField(seedText,GUILayout.Width(110)); if(Event.current.type==EventType.Repaint) nativePoints["seed-field"]=GUIUtility.GUIToScreenPoint(GUILayoutUtility.GetLastRect().center)-guiScreenOrigin;
            if (ControlButton("seed-apply","Apply seed + reset") && int.TryParse(seedText,out int chosenSeed)) { Settings.Seed=chosenSeed; ResetScenario(); }
            GUILayout.EndHorizontal();
            MealControlsGUI();
            if (ReserveFleet)
            {
                if (GUILayout.Button("Deploy next healthy standby")) Fleet.Deploy();
                foreach (var command in Fleet.Commands.Reverse().Take(3)) GUILayout.Label("#"+command.Id+" "+command.Action+" "+command.RobotId+" / "+command.State+": "+command.Reason);
            }
            GUILayout.Label("Dispatch "+_robots.Count(r=>r.DispatchEnabled)+" | standby "+_robots.Count(r=>r.FleetRole==GuardianFleetRole.Standby)+" | retiring "+_robots.Count(r=>r.FleetRole==GuardianFleetRole.Retiring));
            GUILayout.Label("1/2/3 select  N/W/L condition  E stop  R reset  F7 mode | scroll for task controls");
            GUILayout.Label("Single-track route: "+(_traffic.Owner==null?"free":_traffic.Owner.Robot.RobotId)+" | waiting "+_traffic.QueueLength);
            if (_pedestrianSystem!=null && _pedestrianSystem.Active)
                GUILayout.Label("Pedestrian demo: "+(_pedestrianSystem.Pedestrian.Moving?"crossing":"waiting")+
                    " | robot holds "+_robots.Count(r=>r.PhysicsDriver.PedestrianSafetyHold)+
                    " | SIMULATED observation only");
            foreach (GuardianRobot robot in _robots)
            {
                GUILayout.Label(robot.RobotId+"  "+robot.FleetRole+"  "+robot.Condition.ToString().ToUpperInvariant()+"  "+GuardianCapabilityPolicy.AllowedText(robot.Condition,robot.EmergencyStopped));
                GUILayout.Label("Sim vibration "+robot.Vibration.ToString("F2")+" | "+robot.PhysicsDriver.WaitReason);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Normal")) SetCondition(robot.RobotId,RobotCondition.Normal);
                if (GUILayout.Button("Warning")) SetCondition(robot.RobotId,RobotCondition.Warning);
                if (GUILayout.Button("Limited")) SetCondition(robot.RobotId,RobotCondition.Limited);
                if (ControlButton("stop/"+robot.RobotId,robot.EmergencyStopped?"Recover":"STOP")) SetEmergencyStop(robot.RobotId,!robot.EmergencyStopped);
                GUILayout.EndHorizontal();
                if (ReserveFleet)
                {
                    GUILayout.BeginHorizontal();
                    if (ControlButton("retire/"+robot.RobotId,"Retire "+robot.RobotId)) Fleet.Retire(robot.RobotId);
                    if (ControlButton("deploy/"+robot.RobotId,"Deploy "+robot.RobotId)) Fleet.Deploy(robot.RobotId);
                    if (ControlButton("recover/"+robot.RobotId,"Recover to standby")) Fleet.RecoverToStandby(robot.RobotId);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Label("Job "+(robot.CurrentTask==null?"idle / returning home":robot.CurrentTask.Id+" "+robot.CurrentTask.Phase+(robot.CurrentTask.AwaitingServicePickup?" / waiting at table":"")));
            }
            AutomaticTasks=GUILayout.Toggle(AutomaticTasks,"Seeded random tasks");
            Settings.RandomFaults=GUILayout.Toggle(Settings.RandomFaults,"Seeded random faults / recovery");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Soup T1")) QueueTask(DeliveryKind.Soup,0,GuardianMenu.Soup);
            if (GUILayout.Button("Food T2")) QueueTask(DeliveryKind.Food,1,(GuardianMenu)(1+_generated%3));
            if (GUILayout.Button("Return T3")) QueueTask(DeliveryKind.EmptyDishes,2);
            GUILayout.EndHorizontal();
            GUILayout.Label("Requests "+_requests.Count+" | rejected "+_requests.Count(r=>r.Task==null)+" | pending "+_requests.Count(r=>r.Task!=null && r.Task.Phase==TaskPhase.Pending)+" | merged "+_requests.Count(r=>r.Coalesced));
            foreach (var r in _requests.AsEnumerable().Reverse().Take(2)) GUILayout.Label("Request #"+r.Id+" / "+r.Status+": "+r.Decision);
            GUILayout.Label("Completed "+_tasks.Count(t=>t.Phase==TaskPhase.Completed)+" / "+_tasks.Count+" | dishes "+Inventory.Items.Count);
            foreach (DeliveryTask task in _tasks.Where(t=>t.Phase==TaskPhase.Pending).Take(3)) GUILayout.Label(task.Id+": "+task.PendingReason);
            foreach (string line in _events.Take(5)) GUILayout.Label(line);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        private void Log(string message)
        {
            _events.Insert(0, string.Format("{0:00.0}s  {1}", _demoClock, message));
            if (_events.Count > 12) _events.RemoveAt(_events.Count - 1);
            Debug.Log("[Guardian] " + message);
        }

        private void OnGUI()
        {
            if (!Application.isPlaying || !_showTelemetry) return;
            guiScreenOrigin=GUIUtility.GUIToScreenPoint(Vector2.zero);
            if (Event.current.type==EventType.MouseUp && GetComponent<GuardianPhysicsVerification>()!=null) Debug.Log("[Guardian] Native mouse root="+Event.current.mousePosition+" screenOrigin="+guiScreenOrigin);
            if (_mode==GuardianSimulationMode.Physics) { var savedSkin=GUI.skin; try { EnsurePhysicsSkin(); GUI.skin=physicsSkin; if (Event.current.type==EventType.Repaint) GUI.DrawTexture(new Rect(Screen.width-PanelWidth-20,0,PanelWidth+20,Screen.height),panelTexture); PhysicsGUI(); } finally { GUI.skin=savedSkin; } return; }
            int panelWidth = Mathf.Clamp(Mathf.RoundToInt(Screen.width * 0.22f), 260, 380);
            Rect panel = new Rect(Screen.width - panelWidth - 10, 10, panelWidth, Screen.height - 20);
            GUILayout.BeginArea(panel, GUI.skin.box);
            GUILayout.Label("PHYSICAL AI GUARDIAN", GUI.skin.box);
            if (ModePanelButton("Return to physics simulator"))
            { GUILayout.EndArea(); SetMode(GuardianSimulationMode.Physics); return; }
            GUILayout.Label("Unity 3D Fleet Simulator | 6000.3.12f1", GUI.skin.label);
            GUILayout.Label(StoryScene + "   " + _demoClock.ToString("00.0") + "s / 40s");
            GUILayout.Label("H: HUD  R: reset  F8: story/interactive  F9: capture");
            GUILayout.Label("RUN MODE: " + (_storyMode ? "40s STORY" : "INTERACTIVE"));
            GUILayout.Space(6);
            foreach (GuardianRobot robot in _robots)
            {
                Color old = GUI.contentColor;
                GUI.contentColor = robot.Condition == RobotCondition.Normal ? new Color(0.25f, 0.78f, 0.43f) :
                    robot.Condition == RobotCondition.Warning ? new Color(0.92f, 0.70f, 0.20f) : new Color(0.98f, 0.49f, 0.27f);
                GUILayout.Label("ROBOT " + robot.RobotId + "   " + robot.Condition.ToString().ToUpperInvariant());
                GUI.contentColor = old;
                GUILayout.Label("Vibration: " + robot.Vibration.ToString("0.00") + " (demo)");
                float vibration = GUILayout.HorizontalSlider(robot.Vibration, 0f, 1f);
                if (Mathf.Abs(vibration - robot.Vibration) > 0.005f) SubmitVibration(robot.RobotId, vibration);
                GUILayout.Label("Allowed: " + (robot.Condition == RobotCondition.Normal ? "SOUP / FOOD / RETURN" :
                    robot.Condition == RobotCondition.Warning ? "FOOD / RETURN" : "RETURN ONLY"));
                GUILayout.Label("Task: " + (robot.CurrentTask == null ? "idle" : robot.CurrentTask.Id + " - " + robot.CurrentTask.Kind));
                if (robot.CurrentTask != null)
                    GUILayout.Label("Destination: " + robot.CurrentTask.Destination.x.ToString("0.0") + ", " +
                        robot.CurrentTask.Destination.z.ToString("0.0") + "  " + robot.CurrentTask.Phase);
                GUILayout.Space(8);
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("B -> LIMITED")) SubmitVibration("B", 0.85f);
            if (GUILayout.Button("RESET (R)")) ResetScenario();
            GUILayout.EndHorizontal();
            if (!_storyMode) _autoDemo = GUILayout.Toggle(_autoDemo, "Automatic degradation at 11.5s");
            else GUILayout.Label("18s: B restriction / task reassignment");
            GUILayout.Space(8);
            GUILayout.Label("DECISION / ASSIGNMENT LOG", GUI.skin.box);
            for (int i = 0; i < Mathf.Min(7, _events.Count); i++)
                GUILayout.Label(_events[i]);
            GUILayout.EndArea();

            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
                _labelStyle.normal.textColor = Color.white;
            }
            foreach (GuardianRobot robot in _robots)
            {
                Vector3 point = _camera.WorldToScreenPoint(robot.transform.position + Vector3.up * 2.4f);
                if (point.z < 0) continue;
                Rect label = new Rect(point.x - 98, Screen.height - point.y - 14, 196, 27);
                string taskIcon = robot.CurrentTask == null ? "IDLE" :
                    robot.CurrentTask.Kind == DeliveryKind.Soup ? "SOUP" :
                    robot.CurrentTask.Kind == DeliveryKind.Food ? "FOOD" : "RETURN";
                GUI.Box(label, robot.RobotId + "  " + robot.Condition.ToString().ToUpperInvariant() +
                    "  " + taskIcon, _labelStyle);
            }
        }
    }
}
