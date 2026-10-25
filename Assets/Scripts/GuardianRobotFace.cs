using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Guardian.UnitySim
{
    /// <summary>Expressive face screen on the robot head. It reads the same state as the status lamps
    /// (condition, stop, holds, cargo, waiting) and redraws its small texture only when the face changes.
    /// The screen is a separate quad in front of the model's own face mesh; model assets are untouched.</summary>
    public sealed class GuardianRobotFace : MonoBehaviour
    {
        public const string DisplayName="Guardian Face Display";
        public GuardianFaceExpression Expression {get;private set;}
        private GuardianFaceExpression shown;private float shownUntil=-1;
        /// <summary>Shows a face for a while whatever the state says (the film's dialogue: worried while asking for help).</summary>
        public void Show(GuardianFaceExpression face,float seconds) {shown=face;shownUntil=Time.time+seconds;}
        public bool Visible=>display!=null;
        private GuardianRobot robot;private GuardianStatusIndicator status;private GuardianMotionInterlock hold;
        private GuardianFacePainter painter;private Texture2D texture;private Material material;private Mesh mesh;private Transform display;
        private int lastKey=int.MinValue;private float nextBlink,blinkUntil,delightUntil,gaze;
        private DeliveryTask lastTask;private TaskPhase lastPhase;private Vector3 lastPosition;private float speed;
        private System.Random random;
        private static readonly Color[] Lines={new Color(.55f,.92f,1f),new Color(1f,.76f,.22f),new Color(1f,.48f,.25f),new Color(1f,.28f,.26f),new Color(1f,.82f,.38f)};

        private void Start()
        {
            robot=GetComponent<GuardianRobot>();status=GetComponent<GuardianStatusIndicator>();
            random=new System.Random(robot.RobotId.Length>0?robot.RobotId[0]:1);
            if(!TryFindScreen(out Vector3 center,out Vector2 size)) {Debug.LogWarning("[Guardian] No face mesh on Robot "+robot.RobotId+"; face display disabled");enabled=false;return;}
            painter=new GuardianFacePainter(192,128);
            texture=new Texture2D(painter.Width,painter.Height,TextureFormat.RGBA32,false){wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear,name="Guardian face "+robot.RobotId};
            // Unlit and alpha-blended so the screen glows with rounded corners; Standard is a last resort.
            Shader shader=Shader.Find("Sprites/Default");if(shader==null) shader=Shader.Find("Unlit/Transparent");if(shader==null) shader=Shader.Find("UI/Default");
            if(shader==null) shader=Shader.Find("Standard");
            if(shader==null) {Debug.LogWarning("[Guardian] No unlit shader for the face display");Destroy(texture);enabled=false;return;}
            material=new Material(shader){mainTexture=texture,name="Guardian face "+robot.RobotId};
            display=new GameObject(DisplayName).transform;display.SetParent(transform,false);display.localPosition=center;display.localRotation=Quaternion.identity;
            mesh=Quad(size);display.gameObject.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=display.gameObject.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
            renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
            // The placeholder's static eye/mouth primitives would poke through the screen.
            Transform visual=transform.Find(NekobotVisualFactory.VisualRootName);
            foreach(string part in new[]{"Screen Left Eye","Screen Right Eye","Screen Mouth"})
            {var r=visual!=null?visual.Find(part)?.GetComponent<Renderer>():null;if(r!=null) r.enabled=false;}
            lastPosition=transform.position;nextBlink=Time.time+2+(float)random.NextDouble()*3;
        }

        /// <summary>Front plate of the model's own face mesh ("face" on the local Nekobot, "Cat Face" on the placeholder), in actor space.</summary>
        private bool TryFindScreen(out Vector3 center,out Vector2 size)
        {
            center=default;size=default;
            Transform visual=transform.Find(NekobotVisualFactory.VisualRootName);if(visual==null) return false;
            Renderer face=null;
            foreach(var r in visual.GetComponentsInChildren<Renderer>(true))
                if(r.GetComponent<TextMesh>()==null && (r.name.Equals("face",StringComparison.OrdinalIgnoreCase) || r.name=="Cat Face")) {face=r;break;}
            if(face==null) return false;
            Bounds world=face.bounds;bool first=true;Bounds b=default;
            for(int x=-1;x<=1;x+=2) for(int y=-1;y<=1;y+=2) for(int z=-1;z<=1;z+=2)
            {
                Vector3 p=transform.InverseTransformPoint(world.center+Vector3.Scale(world.extents,new Vector3(x,y,z)));
                if(first) {b=new Bounds(p,Vector3.zero);first=false;} else b.Encapsulate(p);
            }
            if(b.size.x<.05f || b.max.z<.02f) return false;
            float width=b.size.x*.82f,height=Mathf.Min(b.size.y*.82f,width*.68f);
            // A face mesh that also covers ears is taller than the plate: keep the plate centred low.
            float y0=b.center.y-Mathf.Max(0,b.size.y*.82f-height)*.2f;
            center=new Vector3(b.center.x,y0,b.max.z+.006f);size=new Vector2(width,height);
            return true;
        }

        private static Mesh Quad(Vector2 size)
        {
            float w=size.x*.5f,h=size.y*.5f;
            // Seen from the front (+Z looking back), actor +X is on the viewer's left: map u=0 there.
            var quad=new Mesh{name="Guardian face quad"};
            quad.vertices=new[]{new Vector3(w,-h,0),new Vector3(-w,-h,0),new Vector3(w,h,0),new Vector3(-w,h,0)};
            quad.uv=new[]{new Vector2(0,0),new Vector2(1,0),new Vector2(0,1),new Vector2(1,1)};
            quad.colors=new[]{Color.white,Color.white,Color.white,Color.white}; // Sprites/Default multiplies vertex colour
            quad.triangles=new[]{0,2,1,2,3,1};quad.RecalculateNormals();quad.RecalculateBounds();
            return quad;
        }

        private void LateUpdate()
        {
            if(display==null) return;
            float now=Time.time,dt=Mathf.Max(1e-4f,Time.deltaTime);
            var driver=robot.PhysicsDriver;
            Vector3 velocity=driver!=null && driver.Active?driver.PlanarVelocity:(transform.position-lastPosition)/dt;
            lastPosition=transform.position;speed=Mathf.Lerp(speed,new Vector2(velocity.x,velocity.z).magnitude,Mathf.Clamp01(dt*8));
            // A task that just completed earns a short delighted face.
            if(lastTask!=null && lastTask.Phase==TaskPhase.Completed && lastPhase!=TaskPhase.Completed) delightUntil=now+2.2f;
            lastTask=robot.CurrentTask;lastPhase=lastTask!=null?lastTask.Phase:TaskPhase.Pending;
            if(hold==null) hold=GetComponent<GuardianMotionInterlock>();
            bool waiting=driver!=null && driver.Active && driver.Busy && speed<.05f && driver.WaitReason!=null
                && (driver.WaitReason.Contains("yield") || driver.WaitReason.Contains("waiting") || driver.WaitReason.Contains("obstacle") || driver.WaitReason.Contains("blocked"));
            int colour=robot.Condition==RobotCondition.Warning?1:robot.Condition==RobotCondition.Limited?2:0;
            GuardianFaceExpression face;
            if(robot.EmergencyStopped) {face=GuardianFaceExpression.Stopped;colour=3;}
            else if((status!=null && status.EvidenceUnknown) || (hold!=null && hold.Held)) {face=GuardianFaceExpression.Caution;colour=4;}
            else if(robot.CurrentTask!=null && robot.CurrentTask.Phase==TaskPhase.ReturningCargo) face=GuardianFaceExpression.Sorry;
            else if(now<delightUntil) face=GuardianFaceExpression.Delight;
            else if(robot.Condition==RobotCondition.Limited) face=GuardianFaceExpression.Tired;
            else if(robot.Condition==RobotCondition.Warning) face=GuardianFaceExpression.Worried;
            else if(waiting) face=GuardianFaceExpression.Waiting;
            else if(robot.Carrying) face=GuardianFaceExpression.Happy;
            else if(robot.CurrentTask!=null || speed>.15f) face=GuardianFaceExpression.Focused;
            else face=GuardianFaceExpression.Calm;
            if(now<shownUntil) face=shown;
            Expression=face;
            bool blinks=face==GuardianFaceExpression.Calm || face==GuardianFaceExpression.Focused || face==GuardianFaceExpression.Waiting || face==GuardianFaceExpression.Worried;
            if(now>=nextBlink) {blinkUntil=now+.14f;nextBlink=now+2.5f+(float)random.NextDouble()*3;}
            bool blink=blinks && now<blinkUntil;
            // Eyes follow the turn: turning right looks to the robot's right (the viewer's left).
            float turn=driver!=null && driver.Active?driver.Body.angularVelocity.y:0;
            gaze=Mathf.Lerp(gaze,-Mathf.Clamp(turn/1.5f,-1,1),Mathf.Clamp01(dt*6));
            int gazeStep=Mathf.RoundToInt(gaze*4);
            bool animated=face==GuardianFaceExpression.Waiting || face==GuardianFaceExpression.Delight || face==GuardianFaceExpression.Tired;
            int frame=animated?Mathf.FloorToInt(now*8):0;
            int key=(((((int)face*8+colour)*2+(blink?1:0))*16+gazeStep+8)*100003)^frame;
            if(key==lastKey) return;
            lastKey=key;Color line=Lines[colour];
            painter.Paint(face,line.r,line.g,line.b,gazeStep/4f,0,blink?1:0,frame/8f);
            texture.LoadRawTextureData(painter.Pixels);texture.Apply(false);
        }

        private void OnDestroy()
        {
            if(material!=null) Destroy(material);if(texture!=null) Destroy(texture);if(mesh!=null) Destroy(mesh);
        }
    }
}
