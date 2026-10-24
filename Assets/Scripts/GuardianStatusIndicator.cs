using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>One state source for eligibility, traffic lights and the readable robot label.</summary>
    public sealed class GuardianStatusIndicator : MonoBehaviour
    {
        private GuardianRobot robot;
        private Renderer[] lamps;
        private TextMesh label,shadow;
        private Material material;
        private MaterialPropertyBlock block;
        private static readonly Color[] Colors={new Color(.15f,.85f,.32f),new Color(1f,.73f,.08f),new Color(.95f,.12f,.08f)};
        private void Start()
        {
            block=new MaterialPropertyBlock();
            robot=GetComponent<GuardianRobot>();
            float top=GuardianPhysicsWorld.LocalBounds(transform.Find(NekobotVisualFactory.VisualRootName)).max.y;
            var root=new GameObject("Guardian status display").transform; root.SetParent(transform,false);
            root.localPosition=new Vector3(0,top+.13f,0);
            material=new Material(Shader.Find("Standard")); material.EnableKeyword("_EMISSION");
            lamps=new Renderer[3];
            for (int i=0;i<3;i++)
            {
                var lamp=GameObject.CreatePrimitive(PrimitiveType.Sphere); lamp.name=((RobotCondition)i)+" status lamp";
                lamp.transform.SetParent(root,false); lamp.transform.localPosition=new Vector3((i-1)*.18f,0,0);
                lamp.transform.localScale=Vector3.one*.14f; lamp.GetComponent<Collider>().enabled=false; Destroy(lamp.GetComponent<Collider>());
                lamps[i]=lamp.GetComponent<Renderer>(); lamps[i].sharedMaterial=material;
            }
            var text=new GameObject("State and allowed tasks"); text.transform.SetParent(root,false); text.transform.localPosition=Vector3.up*.17f;
            label=text.AddComponent<TextMesh>(); label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize=64; label.characterSize=.042f; label.anchor=TextAnchor.LowerCenter; label.alignment=TextAlignment.Center;
            text.GetComponent<MeshRenderer>().sharedMaterial=label.font.material;
            var shade=new GameObject("Label shadow"); shade.transform.SetParent(text.transform,false); shade.transform.localPosition=new Vector3(.022f,-.022f,.012f);
            shadow=shade.AddComponent<TextMesh>(); shadow.font=label.font; shadow.fontSize=label.fontSize; shadow.characterSize=label.characterSize; shadow.anchor=label.anchor; shadow.alignment=label.alignment; shadow.color=Color.black;
            shade.GetComponent<MeshRenderer>().sharedMaterial=label.font.material;
        }
        private void LateUpdate()
        {
            if (lamps==null) return;
            for (int i=0;i<3;i++)
            {
                Color c=Colors[i]*((int)robot.Condition==i?1f:.08f);
                block.SetColor("_Color",c); block.SetColor("_EmissionColor",c*.65f); lamps[i].SetPropertyBlock(block);
            }
            label.text=robot.RobotId+" "+robot.FleetRole+" "+robot.Condition.ToString().ToUpperInvariant()+"\n"+
                GuardianCapabilityPolicy.AllowedText(robot.Condition,robot.EmergencyStopped);
            shadow.text=label.text;
            label.color=robot.EmergencyStopped?Color.white:Colors[(int)robot.Condition];
            if (Camera.main!=null) label.transform.rotation=Camera.main.transform.rotation;
        }
        private void OnDestroy() { if (material!=null) Destroy(material); }
    }
}
