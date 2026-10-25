using UnityEngine;
using UnityEngine.Rendering;

namespace Guardian.UnitySim
{
    /// <summary>
    /// Which robot is which, at a glance: each robot wears its own colour (A blue, B pink, C purple; the spares
    /// D brown, E grey) on its ears, its tray and a band round the body, with its letter on the back. The table,
    /// the robot card and the film's subtitles use the same colours. The face screen keeps showing the robot's condition, and none of these colours is a signal
    /// colour (green, amber, red), so a pink robot never reads as a stopped one.
    /// </summary>
    public sealed class GuardianRobotLivery : MonoBehaviour
    {
        private const string Prefix = "Guardian livery ";
        private static readonly Color Blue = new Color(.16f, .47f, 1f), Pink = new Color(1f, .36f, .66f), Purple = new Color(.6f, .34f, .95f),
            Brown = new Color(.58f, .38f, .22f), Grey = new Color(.42f, .45f, .5f);

        public static Color ColorOf(string robotId) =>
            robotId == "A" ? Blue : robotId == "B" ? Pink : robotId == "C" ? Purple : robotId == "D" ? Brown : robotId == "E" ? Grey : new Color(.6f, .64f, .7f);

        public static string ColorName(string robotId) =>
            robotId == "A" ? "青" : robotId == "B" ? "ピンク" : robotId == "C" ? "紫" : robotId == "D" ? "茶" : robotId == "E" ? "灰" : "";

        /// <summary>"B（ピンク）"; just the letter for a robot without a colour of its own.</summary>
        public static string Label(string robotId)
        {
            string name = ColorName(robotId);
            return name.Length > 0 ? robotId + "（" + name + "）" : robotId;
        }

        private Material paint, pale, lettered;
        private Texture2D badge;

        private void Start()
        {
            var robot = GetComponent<GuardianRobot>();
            Transform visual = transform.Find(NekobotVisualFactory.VisualRootName);
            if (robot == null || visual == null) { enabled = false; return; }
            // A robot whose visual was copied from another keeps none of that robot's livery.
            for (int i = visual.childCount - 1; i >= 0; i--)
                if (visual.GetChild(i).name.StartsWith(Prefix)) Destroy(visual.GetChild(i).gameObject);

            Color colour = ColorOf(robot.RobotId);
            paint = Paint(colour, robot.RobotId);
            pale = Paint(Color.Lerp(colour, Color.white, .45f), robot.RobotId + " tray");
            // Parts of the project's own robot body; a locally imported model has none and gets the band and letter.
            Dress(visual.Find("Left Ear"), paint);
            Dress(visual.Find("Right Ear"), paint);
            Dress(visual.Find("Delivery Tray"), pale);

            Transform bodyPart = visual.Find("Body");
            Bounds body = bodyPart != null ? new Bounds(bodyPart.localPosition, bodyPart.localScale) : GuardianPhysicsWorld.LocalBounds(visual);
            float low = body.min.y + body.size.y * .23f;
            Plate("band", visual, new Vector3(body.center.x, low, body.center.z), new Vector3(body.size.x + .03f, .1f, body.size.z + .03f), paint);
            // The letter on the back, readable from behind. A texture rather than 3D text: the built-in text shader
            // draws through the body, so the letter would show mirrored on the face.
            badge = Badge(robot.RobotId, colour);
            if (badge != null) { lettered = Paint(Color.white, robot.RobotId + " letter"); lettered.mainTexture = badge; }
            Plate("letter", visual, new Vector3(body.center.x, body.min.y + body.size.y * .58f, body.min.z - .006f), new Vector3(.3f, .38f, .012f), lettered ?? paint);
        }

        // 5 x 7 block letters; a robot with another name gets a plain panel.
        private static readonly string[] GlyphA = { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" };
        private static readonly string[] GlyphB = { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." };
        private static readonly string[] GlyphC = { ".####", "#....", "#....", "#....", "#....", "#....", ".####" };
        private static readonly string[] GlyphD = { "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." };
        private static readonly string[] GlyphE = { "#####", "#....", "#....", "####.", "#....", "#....", "#####" };

        /// <summary>The robot's letter in white on its colour, with a one-pixel margin; drawn with hard pixel edges.</summary>
        private static Texture2D Badge(string id, Color colour)
        {
            string[] glyph = id == "A" ? GlyphA : id == "B" ? GlyphB : id == "C" ? GlyphC : id == "D" ? GlyphD : id == "E" ? GlyphE : null;
            if (glyph == null) return null;
            var texture = new Texture2D(7, 9, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = Prefix + id };
            for (int y = 0; y < 9; y++)
                for (int x = 0; x < 7; x++)
                {
                    int row = 7 - y, column = x - 1;  // texture rows count up from the bottom
                    bool ink = row >= 0 && row < 7 && column >= 0 && column < 5 && glyph[row][column] == '#';
                    texture.SetPixel(x, y, ink ? Color.white : colour);
                }
            texture.Apply(false);
            return texture;
        }

        private static Material Paint(Color colour, string name)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            return new Material(shader) { color = colour, name = Prefix + name };
        }

        private static void Dress(Transform part, Material material)
        {
            var renderer = part != null ? part.GetComponent<Renderer>() : null;
            if (renderer != null) renderer.sharedMaterial = material;
        }

        private static void Plate(string name, Transform parent, Vector3 position, Vector3 size, Material material)
        {
            var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = Prefix + name;
            plate.transform.SetParent(parent, false);
            plate.transform.localPosition = position;
            plate.transform.localScale = size;
            // Paint only: not part of the body that the camera, the physics and the clicks see.
            var collider = plate.GetComponent<Collider>(); collider.enabled = false; Destroy(collider);
            var renderer = plate.GetComponent<Renderer>();
            renderer.sharedMaterial = material; renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        private void OnDestroy()
        {
            if (paint != null) Destroy(paint);
            if (pale != null) Destroy(pale);
            if (lettered != null) Destroy(lettered);
            if (badge != null) Destroy(badge);
        }
    }
}
