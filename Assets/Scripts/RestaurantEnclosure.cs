using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Guardian.UnitySim
{
    /// <summary>
    /// When the camera reaches the solid back (north) or side (west) wall, that wall and its trim stop
    /// drawing but keep their shadows, so the room reads like a cut-away model from any angle. The wall
    /// goes as soon as the camera's near plane could touch its inner face, so the view never sits inside it.
    /// </summary>
    public static class RestaurantWallCutaway
    {
        private const string BackWall = "Beige back wall", SideWall = "Beige side wall";
        private static readonly List<Renderer> back = new List<Renderer>(), side = new List<Renderer>();
        private static Transform shell;
        private static bool backHidden, sideHidden;
        private static float backInner = RestaurantEnclosure.North, sideInner = RestaurantEnclosure.West;

        public static bool IsRoomWall(Collider c) => c.name == BackWall || c.name == SideWall;

        public static void Apply(Vector3 eye, float radius = 0)
        {
            if (shell == null && !Collect()) return;
            Set(back, eye.z > backInner - radius - .05f, ref backHidden);
            Set(side, eye.x < sideInner + radius + .05f, ref sideHidden);
        }

        private static bool Collect()
        {
            var found = GameObject.Find("Room Shell");
            if (found == null) return false;
            shell = found.transform; back.Clear(); side.Clear(); backHidden = sideHidden = false;
            backInner = RestaurantEnclosure.North; sideInner = RestaurantEnclosure.West;
            foreach (Renderer r in shell.GetComponentsInChildren<Renderer>(true))
            {
                if (r.bounds.max.y < .1f) continue;  // floor boards and kitchen tiles stay
                Vector3 c = r.bounds.center;
                if (c.z > RestaurantEnclosure.North - .3f) back.Add(r);       // wall, trim, wainscot, menu frames
                else if (c.x < RestaurantEnclosure.West + .3f) side.Add(r);
                // The room-facing surface of each wall, from its own mesh.
                if (r.name == BackWall) backInner = Mathf.Min(backInner, r.bounds.min.z);
                if (r.name == SideWall) sideInner = Mathf.Max(sideInner, r.bounds.max.x);
            }
            return true;
        }

        private static void Set(List<Renderer> renderers, bool hide, ref bool hidden)
        {
            if (hide == hidden) return;
            hidden = hide;
            foreach (Renderer r in renderers) if (r != null) r.shadowCastingMode = hide ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        }
    }

    /// <summary>
    /// Closes the room: a south wall with two automatic sliding doors (入口 and 出口), an east wall and a
    /// ceiling, matching the authored back and side walls. Every new wall is a one-sided quad facing into
    /// the room that casts no shadow, so the camera outside sees straight through it and the sunlight still
    /// reaches the floor; from inside they read as walls. Inside the entrance there is a sign-in board and
    /// two marked spots where guests wait to be shown to a table; the self-checkout stands by the exit and
    /// a pavement runs outside. Presentation only: no colliders, nothing on the route grid.
    /// </summary>
    public static class RestaurantEnclosure
    {
        public const string RootName = "Room Enclosure";
        public const float Height = 3.25f, North = 6.04f, South = -6.04f, West = -9.74f, East = 9.74f;
        public const float EntranceX = 1.5f, ExitX = 3.3f, DoorWidth = 1.2f, DoorHeight = 2.25f;
        /// <summary>Just inside and just outside each door; people pass straight between the two.</summary>
        public static Vector3 EntranceInside => new Vector3(EntranceX, 0, South + .75f);
        public static Vector3 EntranceOutside => new Vector3(EntranceX, 0, South - .7f);
        public static Vector3 ExitInside => new Vector3(ExitX, 0, South + .75f);
        public static Vector3 ExitOutside => new Vector3(ExitX, 0, South - .7f);
        /// <summary>Where arriving guests come from and leaving guests go, along the pavement.</summary>
        public static Vector3 StreetWest => new Vector3(EntranceX - 3.2f, 0, South - 1.3f);
        public static Vector3 StreetEast => new Vector3(ExitX + 3.2f, 0, South - 1.3f);
        /// <summary>The sign-in board just inside the entrance, and where a guest stands to write on it.</summary>
        public static Vector3 Board => new Vector3(EntranceX - .95f, 0, South + .3f);
        public static Vector3 BoardSpot => new Vector3(EntranceX - .95f, 0, South + .95f);
        /// <summary>The waiting line along the south wall, first in line nearest the entrance.</summary>
        public static readonly Vector3[] Line = { new Vector3(EntranceX - 1.75f, 0, South + .85f), new Vector3(EntranceX - 2.6f, 0, South + .85f) };
        /// <summary>Self-checkout kiosk against the south wall, east of the exit; a customer stands at CheckoutSpot facing it.</summary>
        public static Vector3 Kiosk => new Vector3(ExitX + 1.3f, 0, South + .3f);
        public static Vector3 CheckoutSpot => new Vector3(ExitX + 1.3f, 0, South + 1.05f);
        public const string KioskName = "Self Checkout", EntranceName = "Entrance door", ExitName = "Exit door";

        public static RestaurantSlidingDoor EntranceDoor { get; private set; }
        public static RestaurantSlidingDoor ExitDoor { get; private set; }

        public static void Ensure(Transform environment)
        {
            if (environment == null) return;
            var existing = environment.Find(RootName);
            if (existing != null && existing.Find(EntranceName) != null)
            {
                EntranceDoor = existing.Find(EntranceName).GetComponent<RestaurantSlidingDoor>();
                ExitDoor = existing.Find(ExitName)?.GetComponent<RestaurantSlidingDoor>();
                return;
            }
            if (existing != null) { existing.name = RootName + " (old)"; Object.Destroy(existing.gameObject); }  // the one-door layout
            var root = new GameObject(RootName).transform;
            root.SetParent(environment, false);
            Material wall = Find(environment, "Beige back wall"), oak = Find(environment, "Oak wainscot panel"), walnut = Find(environment, "Back timber trim");
            var ceiling = wall != null ? new Material(wall) { name = "Ceiling", color = new Color(.95f, .94f, .9f) } : null;

            float w = DoorWidth / 2;
            float[] openings = { EntranceX - w, EntranceX + w, ExitX - w, ExitX + w };
            // South wall: three pieces around the two doors, and the strips over them, facing north into the room.
            var pieces = new[] { (West, openings[0]), (openings[1], openings[2]), (openings[3], East) };
            foreach (var (from, to) in pieces)
            {
                Wall(root, "South wall", from, to, South, Vector3.forward, 0, Height, wall);
                // Oak wainscot and a walnut rail like the authored walls, a few centimetres proud of the wall.
                Wall(root, "South wainscot", from, to, South + .03f, Vector3.forward, .15f, 1.03f, oak);
                Wall(root, "South rail", from, to, South + .035f, Vector3.forward, 1.04f, 1.16f, walnut);
            }
            foreach (float x in new[] { EntranceX, ExitX })
            {
                Wall(root, "South wall over door", x - w, x + w, South, Vector3.forward, DoorHeight, Height, wall);
                Wall(root, "Door frame west", x - w - .1f, x - w, South + .04f, Vector3.forward, 0, DoorHeight + .1f, walnut);
                Wall(root, "Door frame east", x + w, x + w + .1f, South + .04f, Vector3.forward, 0, DoorHeight + .1f, walnut);
                Wall(root, "Door frame top", x - w - .1f, x + w + .1f, South + .04f, Vector3.forward, DoorHeight, DoorHeight + .1f, walnut);
                // Doormat inside each door.
                Quad(root, "Doormat", new Vector3(x, .006f, South + .55f), Vector3.up, new Vector2(DoorWidth, .8f), Tinted(new Color(.22f, .24f, .26f)));
            }
            // East wall, facing west into the room.
            Wall(root, "East wall", South, North, East, Vector3.left, 0, Height, wall);
            Wall(root, "East wainscot", South, North, East - .03f, Vector3.left, .15f, 1.03f, oak);
            Wall(root, "East rail", South, North, East - .035f, Vector3.left, 1.04f, 1.16f, walnut);

            EntranceDoor = RestaurantSlidingDoor.Build(root, EntranceName, new Vector3(EntranceX, 0, South - .06f), DoorWidth, DoorHeight, "入口");
            ExitDoor = RestaurantSlidingDoor.Build(root, ExitName, new Vector3(ExitX, 0, South - .06f), DoorWidth, DoorHeight, "出口");
            BuildKiosk(root, wall, walnut);
            BuildWaitingArea(root, walnut);
            // Pavement outside, so people walking in and out have ground under their feet.
            Quad(root, "Pavement", new Vector3((EntranceX + ExitX) / 2, .002f, South - 1.25f), Vector3.up, new Vector2(10, 2.4f), Tinted(new Color(.55f, .55f, .53f)));
            // Ceiling, facing down.
            Quad(root, "Ceiling", new Vector3((West + East) / 2, Height, (South + North) / 2), Vector3.down,
                new Vector2(East - West, North - South), ceiling);
        }

        private static void BuildKiosk(Transform root, Material body, Material trim)
        {
            var kiosk = new GameObject(KioskName).transform;
            kiosk.SetParent(root, false);
            kiosk.localPosition = Kiosk;
            Box(kiosk, "Kiosk stand", new Vector3(0, .5f, 0), new Vector3(.5f, 1f, .32f), trim, Color.white);
            Box(kiosk, "Kiosk top", new Vector3(0, 1.02f, 0), new Vector3(.56f, .04f, .38f), body, Color.white);
            // Screen tilted toward the customer (north), lit so it reads as a display.
            var screen = Box(kiosk, "Kiosk screen", new Vector3(0, 1.28f, .02f), new Vector3(.46f, .34f, .03f), null, new Color(.12f, .55f, .85f));
            screen.localRotation = Quaternion.Euler(-20, 0, 0);
            var glow = screen.GetComponent<Renderer>().sharedMaterial;
            glow.EnableKeyword("_EMISSION");
            glow.SetColor("_EmissionColor", new Color(.1f, .45f, .75f));
        }

        /// <summary>A sign-in board by the entrance and two floor marks where guests wait their turn.</summary>
        private static void BuildWaitingArea(Transform root, Material trim)
        {
            var board = new GameObject("Waiting board").transform;
            board.SetParent(root, false);
            board.localPosition = Board;
            Box(board, "Board stand", new Vector3(0, .55f, 0), new Vector3(.08f, 1.1f, .08f), trim, Color.white);
            var top = Box(board, "Board top", new Vector3(0, 1.12f, .03f), new Vector3(.5f, .36f, .03f), null, new Color(.96f, .95f, .9f));
            top.localRotation = Quaternion.Euler(-25, 0, 0);
            for (int i = 0; i < Line.Length; i++)
            {
                // A square floor mark with a number, like the tape marks in a real shop.
                Quad(root, "Waiting mark " + (i + 1), Line[i] + Vector3.up * .007f, Vector3.up, new Vector2(.5f, .5f), Tinted(new Color(.95f, .72f, .2f)));
                Quad(root, "Waiting mark inner " + (i + 1), Line[i] + Vector3.up * .008f, Vector3.up, new Vector2(.42f, .42f), Tinted(new Color(.33f, .3f, .27f)));
            }
        }

        internal static Material Tinted(Color color) => new Material(Shader.Find("Standard")) { color = color };

        internal static Transform Box(Transform parent, string name, Vector3 position, Vector3 size, Material material, Color tint)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            Object.DestroyImmediate(box.GetComponent<Collider>());
            box.transform.SetParent(parent, false);
            box.transform.localPosition = position;
            box.transform.localScale = size;
            var renderer = box.GetComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;
            else renderer.sharedMaterial = Tinted(tint);
            return box.transform;
        }

        private static Material Find(Transform environment, string rendererName)
        {
            foreach (Renderer r in environment.GetComponentsInChildren<Renderer>(true))
                if (r.name == rendererName) return r.sharedMaterial;
            return null;
        }

        /// <summary>A vertical strip from <paramref name="from"/> to <paramref name="to"/> along the wall, between two heights.</summary>
        private static void Wall(Transform root, string name, float from, float to, float plane, Vector3 inward, float bottom, float top, Material material)
        {
            float along = (from + to) / 2, y = (bottom + top) / 2;
            Vector3 center = inward.z != 0 ? new Vector3(along, y, plane) : new Vector3(plane, y, along);
            Quad(root, name, center, inward, new Vector2(Mathf.Abs(to - from), top - bottom), material);
        }

        private static void Quad(Transform root, string name, Vector3 center, Vector3 facing, Vector2 size, Material material)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(root, false);
            quad.transform.localPosition = center;
            // A Unity quad shows its face toward -Z; turn that face toward the room.
            quad.transform.localRotation = Quaternion.LookRotation(-facing, Mathf.Abs(facing.y) > .5f ? Vector3.forward : Vector3.up);
            quad.transform.localScale = new Vector3(size.x, size.y, 1);
            var renderer = quad.GetComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }
    }

    /// <summary>
    /// An automatic double sliding door: two framed glass leaves that part from the middle when someone is
    /// near (GuardianDinerFlow calls HoldOpen) and close again a moment later, with a lit sign above.
    /// The leaves run just outside the wall, so open they slide behind it.
    /// </summary>
    public sealed class RestaurantSlidingDoor : MonoBehaviour
    {
        public float Speed = 1.8f;  // leaf travel in metres a second
        private Transform left, right;
        private float width, open, openUntil;
        private TextMesh sign;
        private Font font;
        public bool IsOpen => open > .9f;
        public float Openness => open;

        public void HoldOpen(float seconds) { openUntil = Mathf.Max(openUntil, Time.time + seconds); }

        internal static RestaurantSlidingDoor Build(Transform root, string name, Vector3 at, float width, float height, string label)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = at;
            var door = go.AddComponent<RestaurantSlidingDoor>();
            door.width = width;
            var frame = new Material(Shader.Find("Standard")) { name = "Door aluminium", color = new Color(.72f, .74f, .76f) };
            frame.SetFloat("_Metallic", .8f); frame.SetFloat("_Glossiness", .6f);
            var glass = Glass();
            door.left = Leaf(go.transform, "Left leaf", width / 2, height, frame, glass);
            door.right = Leaf(go.transform, "Right leaf", width / 2, height, frame, glass);
            door.Place();
            // Operator housing over the door, with the sign facing the room and the street.
            RestaurantEnclosure.Box(go.transform, "Door operator", new Vector3(0, height + .14f, .02f), new Vector3(width + .2f, .22f, .16f), frame, Color.white);
            door.font = GuardianFonts.Create(48);
            var text = new GameObject("Door sign");
            text.transform.SetParent(go.transform, false);
            text.transform.localPosition = new Vector3(0, height + .5f, .2f);
            door.sign = text.AddComponent<TextMesh>();
            door.sign.font = door.font; door.sign.fontSize = 48; door.sign.characterSize = .04f;
            door.sign.anchor = TextAnchor.MiddleCenter; door.sign.alignment = TextAlignment.Center; door.sign.text = label;
            door.sign.color = label == "入口" ? new Color(.35f, .85f, .45f) : new Color(1f, .7f, .3f);
            text.GetComponent<MeshRenderer>().sharedMaterial = door.font.material;
            return door;
        }

        private static Transform Leaf(Transform door, string name, float w, float h, Material frame, Material glass)
        {
            var leaf = new GameObject(name).transform;
            leaf.SetParent(door, false);
            const float bar = .05f, depth = .04f;
            RestaurantEnclosure.Box(leaf, "Glass", new Vector3(0, h / 2, 0), new Vector3(w - bar, h - bar, .012f), glass, Color.white);
            RestaurantEnclosure.Box(leaf, "Frame top", new Vector3(0, h - bar / 2, 0), new Vector3(w, bar, depth), frame, Color.white);
            RestaurantEnclosure.Box(leaf, "Frame bottom", new Vector3(0, bar, 0), new Vector3(w, bar * 2, depth), frame, Color.white);
            RestaurantEnclosure.Box(leaf, "Frame side a", new Vector3(-w / 2 + bar / 2, h / 2, 0), new Vector3(bar, h, depth), frame, Color.white);
            RestaurantEnclosure.Box(leaf, "Frame side b", new Vector3(w / 2 - bar / 2, h / 2, 0), new Vector3(bar, h, depth), frame, Color.white);
            return leaf;
        }

        /// <summary>Pale see-through glass (Standard shader, transparent mode).</summary>
        private static Material Glass()
        {
            var m = new Material(Shader.Find("Standard")) { name = "Door glass", color = new Color(.78f, .9f, .97f, .28f) };
            m.SetFloat("_Mode", 3); m.SetFloat("_Glossiness", .92f);
            m.SetInt("_SrcBlend", (int)BlendMode.One); m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha); m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON"); m.DisableKeyword("_ALPHABLEND_ON"); m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)RenderQueue.Transparent;
            return m;
        }

        private void Place()
        {
            // Closed: the leaves meet in the middle. Open: each slides a leaf's width toward its side.
            float quarter = width / 4, travel = width / 2 * open;
            left.localPosition = new Vector3(-quarter - travel, 0, 0);
            right.localPosition = new Vector3(quarter + travel, 0, 0);
        }

        private void Update()
        {
            float target = Time.time < openUntil ? 1 : 0;
            if (Mathf.Approximately(open, target)) return;
            open = Mathf.MoveTowards(open, target, Speed / Mathf.Max(.1f, width / 2) * Time.deltaTime);
            Place();
        }

        private void LateUpdate()
        {
            if (sign != null && Camera.main != null) sign.transform.rotation = Camera.main.transform.rotation;
        }

        private void OnDestroy() { GuardianFonts.Release(font); }
    }
}
