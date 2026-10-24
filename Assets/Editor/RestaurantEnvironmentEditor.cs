using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    public static class RestaurantEnvironmentEditor
    {
        // Swept clearance includes the project-owned robot tray at changing yaw.
        public const float Clearance = .85f;
        private static readonly string[] Legacy = { "Restaurant Decor (primitive meshes)", "Floor",
            "Kitchen Counter", "Dishes Return", "Back Wall", "Left Wall", "Dining Table 1", "Dining Table 2",
            "Dining Table 3", "Table Leg 1", "Table Leg 2", "Table Leg 3" };

        [MenuItem("Guardian/Restaurant/Replace Environment Only")]
        public static void ReplaceMenu()
        {
            Apply(false, true);
            Debug.Log("[Guardian] Environment replaced; robot roots, visuals, camera and simulator retained. Save the scene after review.");
        }

        [MenuItem("Guardian/Restaurant/Validate Layout")]
        public static void ValidateMenu()
        {
            GuardianSimulator simulator = FindSimulator();
            Transform root = simulator.transform.Find("Generated 3D World/" + RestaurantVisuals.RootName);
            if (root == null) throw new InvalidOperationException("Replace the environment first");
            var report = Validate(root, simulator, false);
            Debug.Log("[Guardian] GUARDIAN_RESTAURANT_LAYOUT_OK\n" + string.Join("\n", report));
        }

        public static void ValidateBatch()
        {
            if (Application.unityVersion!="6000.3.12f1") throw new InvalidOperationException("Unity 6000.3.12f1 required");
            const string path="Assets/Scenes/GuardianRestaurant.unity";
            if (!File.Exists(path)) throw new FileNotFoundException("Create or capture the scene first",path);
            EditorSceneManager.OpenScene(path);
            ValidateMenu();
        }

        private static GuardianSimulator FindSimulator()
        {
            var simulator = UnityEngine.Object.FindFirstObjectByType<GuardianSimulator>();
            if (simulator == null) throw new InvalidOperationException("Open GuardianRestaurant.unity first");
            return simulator;
        }

        public static void Apply(bool prototype, bool recordUndo = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode; scene environment edits do not reset a running demo");
            RestaurantAssetPipeline.EnsureAssets();
            GuardianSimulator simulator = FindSimulator();
            Transform world = simulator.transform.Find("Generated 3D World");
            if (world == null) throw new InvalidOperationException("Generated 3D World missing");
            if (world.localPosition != Vector3.zero || world.localRotation != Quaternion.identity || world.lossyScale != Vector3.one)
                throw new InvalidOperationException("World transform must retain its original meter coordinates");
            GuardianRobot[] robots = simulator.GetComponentsInChildren<GuardianRobot>(true);
            if (robots.Length != 3) throw new InvalidOperationException("Expected three existing robots");
            // Build and validate a candidate BEFORE removing any existing scenery.
            Transform candidate = RestaurantVisuals.Create(world, prototype);
            candidate.name = RestaurantVisuals.RootName + " (staging)";
            try { Validate(candidate, simulator, prototype); }
            catch { UnityEngine.Object.DestroyImmediate(candidate.gameObject); throw; }
            var remove = new List<GameObject>();
            foreach (Transform child in world)
            {
                if (child == candidate) continue;
                if (child.name != RestaurantVisuals.RootName && !Legacy.Contains(child.name)) continue;
                if (child.GetComponentInChildren<GuardianRobot>(true) != null || child.GetComponentInChildren<Camera>(true) != null
                    || child.GetComponentInChildren<GuardianSimulator>(true) != null)
                {
                    UnityEngine.Object.DestroyImmediate(candidate.gameObject);
                    throw new InvalidOperationException("Protected actor/camera inside environment: " + child.name);
                }
                remove.Add(child.gameObject);
            }
            // Register the final name so Redo restores the actual environment root,
            // rather than a stale staging name from the creation snapshot.
            candidate.name = RestaurantVisuals.RootName;
            int group = -1;
            if (recordUndo)
            {
                Undo.IncrementCurrentGroup(); group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Replace restaurant environment only");
                Undo.RegisterCreatedObjectUndo(candidate.gameObject, "Create restaurant environment");
            }
            foreach (GameObject old in remove)
            {
                if (recordUndo) Undo.DestroyObjectImmediate(old);
                else UnityEngine.Object.DestroyImmediate(old);
            }
            if (recordUndo) Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(simulator.gameObject.scene);
        }

        public static Bounds BoundsOf(Component component)
        {
            Renderer[] renderers = component.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("No mesh on " + component.name);
            Bounds result = renderers[0].bounds;
            foreach (Renderer renderer in renderers) result.Encapsulate(renderer.bounds);
            return result;
        }

        private static Rect Footprint(Bounds b) { return Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z); }
        private static Rect Lane(Vector3 a, Vector3 b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.x,b.x)-Clearance, Mathf.Min(a.z,b.z)-Clearance,
                Mathf.Max(a.x,b.x)+Clearance, Mathf.Max(a.z,b.z)+Clearance);
        }

        private static Vector3 Station(string name)
        {
            return (Vector3)typeof(GuardianSimulator).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        }

        public static List<string> Validate(Transform root, GuardianSimulator simulator, bool prototype)
        {
            var errors = new List<string>();
            var report = new List<string>();
            RestaurantFixture[] all = root.GetComponentsInChildren<RestaurantFixture>(true);
            RestaurantFixture[] furniture = all.Where(f => f.BlocksRoutes).ToArray();
            int rows = prototype ? 1 : 3;
            foreach (var expected in new[] { ("DiningTable",rows), ("DiningChair",rows*4), ("BoothBench",prototype?1:2),
                ("KitchenPrep",1), ("KitchenSink",1), ("KitchenRange",1), ("PickupCounter",1), ("ReturnCounter",1), ("RoomShell",1) })
                if (all.Count(f=>f.AssetId==expected.Item1)!=expected.Item2) errors.Add("Unexpected count: " + expected.Item1);
            var lanes = new List<Rect>();
            Vector3[] tables = (Vector3[])typeof(GuardianSimulator).GetField("Tables", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Vector3[] stations = new[] { Station("Kitchen"), Station("ReturnStation") }.Concat(tables).ToArray();
            // Derived from actual simulator station constants; no endpoint moved for the art.
            lanes.Add(Lane(new Vector3(-1,0,-4.8f),new Vector3(-1,0,4.8f)));
            foreach (Vector3 p in stations) lanes.Add(Lane(new Vector3(-1,0,p.z),p));
            foreach (float z in new[] {-4.8f,0f,4.8f}) lanes.Add(Lane(new Vector3(-4.2f,0,z),new Vector3(-1,0,z)));
            var names = new HashSet<string>();
            var shell = all.SingleOrDefault(f=>f.AssetId=="RoomShell");
            if (shell != null)
            {
                Renderer back = shell.GetComponentsInChildren<Renderer>().Single(r=>r.name=="Beige back wall");
                Renderer left = shell.GetComponentsInChildren<Renderer>().Single(r=>r.name=="Beige side wall");
                if (Mathf.Abs(back.bounds.center.z-6.13f)>.02f || Mathf.Abs(left.bounds.center.x+9.83f)>.02f)
                    errors.Add("Room shell FBX axes place walls on wrong sides");
            }
            foreach (Transform child in root)
                if (!names.Add(child.name)) errors.Add("Duplicate fixture name: " + child.name);
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.sharedMaterials.Length == 0) errors.Add("No materials: " + renderer.name);
                foreach (Material mat in renderer.sharedMaterials)
                    if (mat == null || mat.shader == null || !mat.shader.isSupported || mat.shader.name.Contains("Error"))
                        errors.Add("Missing/unsupported material: " + renderer.name);
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) errors.Add("Missing mesh: " + renderer.name);
            }
            for (int i=0;i<furniture.Length;i++)
            {
                Bounds b = BoundsOf(furniture[i]);
                Rect footprint = Footprint(b);
                if (b.min.x < -9.7f || b.max.x > 9.7f || b.min.z < -6.05f || b.max.z > 6.03f || b.min.y < -.025f)
                    errors.Add("Outside room/floor: " + furniture[i].name + " " + b);
                if (lanes.Any(l=>l.Overlaps(footprint))) errors.Add("Route / handoff clearance invaded: " + furniture[i].name);
                for (int j=i+1;j<furniture.Length;j++)
                    if (furniture[i].GetComponent<RestaurantPerson>()==null && furniture[j].GetComponent<RestaurantPerson>()==null
                        && footprint.Overlaps(Footprint(BoundsOf(furniture[j]))))
                        errors.Add("Furniture overlap: " + furniture[i].name + " / " + furniture[j].name);
                foreach (GuardianRobot robot in simulator.GetComponentsInChildren<GuardianRobot>(true))
                    if (b.Intersects(BoundsOf(robot))) errors.Add("Robot / furniture overlap: " + robot.RobotId + " / " + furniture[i].name);
                report.Add(furniture[i].name + " bounds=" + b);
            }
            foreach (string dependency in AssetDatabase.GetDependencies(RestaurantAssetPipeline.Root + "/Resources/RestaurantCatalog.asset",true))
                if (dependency.StartsWith("Assets/Nekobot/", StringComparison.OrdinalIgnoreCase))
                    errors.Add("Third-party Nekobot dependency in restaurant: " + dependency);
            foreach (RestaurantFixture sink in all.Where(f=>f.AssetId=="KitchenSink"))
                VerifySinkCavity(sink);
            report.AddRange(RestaurantPeopleValidation.Validate(root,prototype));
            if (errors.Count>0) throw new InvalidOperationException(string.Join("\n",errors));
            report.Add("PASS: " + furniture.Length + " furniture, " + all.Length + " prefab instances; materials/meshes/counts/unique names");
            report.Add("PASS: furniture pairs, robot bounds, floor bounds, station access and swept lanes (radius " + Clearance + "m)");
            report.Add("PASS: restaurant catalog has no Nekobot dependency");
            report.Add("PASS: imported sink triangles expose the drain and two basin-bottom points from above");
            return report;
        }

        private static void VerifySinkCavity(RestaurantFixture sink)
        {
            Renderer drain=sink.GetComponentsInChildren<Renderer>().Single(r=>r.name=="Drain");
            Vector3 center=drain.bounds.center;
            Vector3[] probes={center,center+new Vector3(.12f,0,0),center+new Vector3(0,0,.12f)};
            for (int i=0;i<probes.Length;i++)
            {
                Ray ray=new Ray(new Vector3(probes[i].x,2f,probes[i].z),Vector3.down);
                string hitName=null; float nearest=float.PositiveInfinity;
                foreach (MeshFilter filter in sink.GetComponentsInChildren<MeshFilter>())
                {
                    Mesh mesh=filter.sharedMesh;
                    Vector3[] vertices=mesh.vertices; int[] triangles=mesh.triangles;
                    Matrix4x4 matrix=filter.transform.localToWorldMatrix;
                    for (int j=0;j<triangles.Length;j+=3)
                    {
                        Vector3 a=matrix.MultiplyPoint3x4(vertices[triangles[j]]);
                        Vector3 b=matrix.MultiplyPoint3x4(vertices[triangles[j+1]]);
                        Vector3 c=matrix.MultiplyPoint3x4(vertices[triangles[j+2]]);
                        if (TriangleDistance(ray,a,b,c,out float distance) && distance<nearest)
                        { nearest=distance; hitName=filter.name; }
                    }
                }
                string expected=i==0?"Drain":"Basin bottom";
                float height=ray.origin.y-nearest;
                if (hitName!=expected || height<.66f || height>.73f)
                    throw new InvalidOperationException("Sink cavity occluded: probe "+i+" hit "+hitName+" at y="+height+
                        "; expected exposed "+expected);
            }
        }

        // Two-sided triangle intersection reads the actual imported FBX, with no
        // permanent colliders or changes to physics/navigation in the live scene.
        private static bool TriangleDistance(Ray ray,Vector3 a,Vector3 b,Vector3 c,out float distance)
        {
            distance=0;
            Vector3 edge1=b-a, edge2=c-a, p=Vector3.Cross(ray.direction,edge2);
            float determinant=Vector3.Dot(edge1,p);
            if (Mathf.Abs(determinant)<.0000001f) return false;
            float inverse=1f/determinant;
            Vector3 t=ray.origin-a;
            float u=Vector3.Dot(t,p)*inverse;
            if (u<0 || u>1) return false;
            Vector3 q=Vector3.Cross(t,edge1);
            float v=Vector3.Dot(ray.direction,q)*inverse;
            if (v<0 || u+v>1) return false;
            distance=Vector3.Dot(edge2,q)*inverse;
            return distance>=0;
        }

        /// <summary>Also sampled at every actual demo frame by the evidence runner.</summary>
        public static void ValidateRobotClearance(GuardianSimulator simulator)
        {
            RestaurantClearance.Measure(simulator);
        }

        public static void VerifyIdempotence(bool prototype,string reportDirectory="docs/evidence/restaurant-priority-a")
        {
            GuardianSimulator sim=FindSimulator();
            UnityEngine.Object[] protectedObjects=sim.GetComponentsInChildren<Transform>(true)
                .Where(t=>t.GetComponent<GuardianRobot>()!=null || t.GetComponent<Camera>()!=null || t.GetComponent<Light>()!=null
                    || t.name==NekobotVisualFactory.VisualRootName).Cast<UnityEngine.Object>().ToArray();
            string snapshot=string.Join("\n",protectedObjects.Select(o=>o.GetInstanceID()+":"+EditorJsonUtility.ToJson(o)));
            string policy=EditorJsonUtility.ToJson(sim);
            Apply(prototype);
            int count=sim.GetComponentsInChildren<RestaurantFixture>(true).Length;
            Apply(prototype);
            if (count!=sim.GetComponentsInChildren<RestaurantFixture>(true).Length
                || sim.transform.Find("Generated 3D World").Cast<Transform>().Count(t=>t.name==RestaurantVisuals.RootName)!=1
                || protectedObjects.Any(o=>o==null)
                || snapshot!=string.Join("\n",protectedObjects.Select(o=>o.GetInstanceID()+":"+EditorJsonUtility.ToJson(o)))
                || policy!=EditorJsonUtility.ToJson(sim))
                throw new InvalidOperationException("Idempotence / actor preservation failed");
            var report=Validate(sim.transform.Find("Generated 3D World/"+RestaurantVisuals.RootName),sim,prototype);
            report.Add("PASS: repeated replacements preserve actor/visual/camera/light instance IDs, transforms and serialized simulator settings");
            if (!prototype)
            {
                Action checkPreserved=()=>
                {
                    if (protectedObjects.Any(o=>o==null) || policy!=EditorJsonUtility.ToJson(sim)
                        || snapshot!=string.Join("\n",protectedObjects.Select(o=>o.GetInstanceID()+":"+EditorJsonUtility.ToJson(o))))
                        throw new InvalidOperationException("Undo/Redo changed protected actors or simulator");
                    Transform world=sim.transform.Find("Generated 3D World");
                    if (world.Cast<Transform>().Count(t=>t.name==RestaurantVisuals.RootName)!=1
                        || sim.GetComponentsInChildren<RestaurantFixture>(true).Length!=count)
                        throw new InvalidOperationException("Undo/Redo duplicated or lost environment fixtures");
                    Validate(world.Find(RestaurantVisuals.RootName),sim,false);
                };
                Apply(false,true); checkPreserved();
                Undo.PerformUndo(); checkPreserved();
                Undo.PerformRedo(); checkPreserved();
                report.Add("PASS: Undo-enabled replacement -> Undo -> Redo each preserves one environment, fixture counts, actor/visual/camera/light IDs and simulator state");
                VerifyGuardsAndFreshWorld(sim, report);
            }
            Directory.CreateDirectory(reportDirectory);
            File.WriteAllLines(Path.Combine(reportDirectory,(prototype?"prototype":"after")+"-layout.txt"),report);
            if (!EditorSceneManager.SaveScene(sim.gameObject.scene)) throw new IOException("Scene save failed");
            Debug.Log("[Guardian] GUARDIAN_RESTAURANT_LAYOUT_OK");
        }

        private static void VerifyGuardsAndFreshWorld(GuardianSimulator simulator,List<string> report)
        {
            Transform root=simulator.transform.Find("Generated 3D World/"+RestaurantVisuals.RootName);
            var fixtures=root.GetComponentsInChildren<RestaurantFixture>();
            RestaurantFixture pickup=fixtures.Single(f=>f.AssetId=="PickupCounter");
            Vector3 position=pickup.transform.localPosition;
            try
            {
                pickup.transform.localPosition=new Vector3(-1,0,0);
                ExpectRejected(root,simulator,"Route / handoff");
            }
            finally { pickup.transform.localPosition=position; }
            RestaurantFixture chair=fixtures.First(f=>f.AssetId=="DiningChair");
            position=chair.transform.localPosition;
            try
            {
                chair.transform.localPosition=fixtures.First(f=>f.AssetId=="DiningTable").transform.localPosition;
                ExpectRejected(root,simulator,"Furniture overlap");
            }
            finally { chair.transform.localPosition=position; }
            Renderer renderer=chair.GetComponentInChildren<Renderer>();
            Material[] materials=renderer.sharedMaterials;
            try { renderer.sharedMaterial=null; ExpectRejected(root,simulator,"Missing/unsupported material"); }
            finally { renderer.sharedMaterials=materials; }
            GameObject duplicate=UnityEngine.Object.Instantiate(chair.gameObject,root);
            duplicate.name=chair.name;
            try { ExpectRejected(root,simulator,"Unexpected count"); }
            finally { UnityEngine.Object.DestroyImmediate(duplicate); }
            Validate(root,simulator,false);
            report.Add("PASS: negative probes reject aisle obstruction, furniture overlap, missing material and duplicate chair");
            RestaurantPerson person=root.GetComponentsInChildren<RestaurantPerson>().First(p=>!p.Standing);
            Vector3 personPosition=person.transform.position;
            try
            {
                person.transform.position+=Vector3.down*.08f;
                ExpectRejected(root,simulator,"Person shoes not on floor");
            }
            finally { person.transform.position=personPosition; }
            RestaurantFixture assignedSeat=person.Seat;
            try
            {
                person.Seat=null;
                ExpectRejected(root,simulator,"Missing/duplicate support chair");
            }
            finally { person.Seat=assignedSeat; }
            Transform palm=person.GetComponentsInChildren<MeshFilter>().First(p=>p.name=="Hand").transform;
            Vector3 palmPosition=palm.position;
            try
            {
                Renderer top=root.GetComponentsInChildren<RestaurantFixture>().First(f=>f.AssetId=="DiningTable")
                    .GetComponentsInChildren<Renderer>().Single(r=>r.name=="Solid oak rounded top");
                // FBX node pivots can differ from mesh centers; move the actual palm envelope.
                Vector3 target=new Vector3(top.bounds.center.x,top.bounds.max.y-.005f,top.bounds.center.z);
                palm.position+=target-palm.GetComponent<Renderer>().bounds.center;
                Debug.Log("[Guardian] Contact negative probe palm="+palm.GetComponent<Renderer>().bounds+
                    " table="+top.bounds+" person="+RestaurantClearance.BoundsOf(person)+" depth="+
                    RestaurantPeopleValidation.Penetration(palm.GetComponent<MeshFilter>(),top.GetComponent<MeshFilter>()));
                ExpectRejected(root,simulator,"Person penetrates furniture");
            }
            finally { palm.position=palmPosition; }
            RestaurantPerson staff=root.GetComponentsInChildren<RestaurantPerson>().Single(p=>p.Standing);
            Vector3 staffPosition=staff.transform.position;
            try
            {
                staff.transform.position=personPosition;
                ExpectRejected(root,simulator,"People overlap");
            }
            finally { staff.transform.position=staffPosition; }
            try
            {
                staff.transform.position=new Vector3(-1,0,-4);
                ExpectRejected(root,simulator,"Route / handoff clearance invaded");
            }
            finally { staff.transform.position=staffPosition; }
            Validate(root,simulator,false);
            report.Add("PASS: negative people probes reject sunken feet, missing chair anchor, hand/table mesh penetration, overlapping people and route invasion");
            RestaurantFixture sink=fixtures.Single(f=>f.AssetId=="KitchenSink");
            GameObject closure=GameObject.CreatePrimitive(PrimitiveType.Cube);
            closure.name="Closed sink cabinet regression probe";
            closure.transform.SetParent(sink.transform,false);
            closure.transform.localPosition=new Vector3(0,.54f,0);
            closure.transform.localScale=new Vector3(1.55f,.68f,.88f);
            try
            {
                try { VerifySinkCavity(sink); }
                catch (InvalidOperationException e)
                {
                    if (!e.Message.Contains("Sink cavity occluded")) throw;
                    report.Add("PASS: cavity regression probe rejects a closed cabinet top at y=0.88");
                    UnityEngine.Object.DestroyImmediate(closure); closure=null;
                }
                if (closure!=null) throw new InvalidOperationException("Sink cavity regression probe was not detected");
            }
            finally { if (closure!=null) UnityEngine.Object.DestroyImmediate(closure); }
            // Exercise EnsureWorld itself in an isolated additive scene, then restore the active scene.
            var previous=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var fresh=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            try
            {
                var sim=new GameObject("Fresh construction probe").AddComponent<GuardianSimulator>();
                sim.EnsureWorld(); sim.EnsureWorld();
                if (sim.GetComponentsInChildren<GuardianRobot>().Length!=3
                    || sim.GetComponentsInChildren<RestaurantFixture>().Count(f=>f.AssetId=="DiningTable")!=3)
                    throw new InvalidOperationException("Fresh EnsureWorld construction failed");
                Validate(sim.transform.Find("Generated 3D World/"+RestaurantVisuals.RootName),sim,false);
                report.Add("PASS: fresh EnsureWorld builds complete environment and three actors; repeated call adds nothing");
            }
            finally
            {
                EditorSceneManager.CloseScene(fresh,true);
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(previous);
            }
        }

        private static void ExpectRejected(Transform root,GuardianSimulator simulator,string expected)
        {
            try { Validate(root,simulator,false); }
            catch (InvalidOperationException e) { if (e.Message.Contains(expected)) return; throw; }
            throw new InvalidOperationException("Validator accepted invalid fixture: "+expected);
        }
    }
}
