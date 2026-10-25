using UnityEngine;
using UnityEngine.Rendering;

namespace Guardian.UnitySim
{
    /// <summary>Project-owned visual add-on. Reads detected state; contains no fault/input command.</summary>
    public sealed class GuardianRobotAlertMarker : MonoBehaviour
    {
        private GuardianSimulator sim;
        private GuardianRobot robot;
        private Transform overhead,tablet;
        private TextMesh overheadText,tabletText;
        private Material surface;
        private Font font;
        private bool ownsFont;
        public bool Visible {get;private set;}
        public string Symbol {get;private set;}="";
        public string Caption {get;private set;}="";
        public Collider TabletHitTarget {get;private set;}
        public void Initialize(GuardianSimulator simulator,GuardianRobot actor)
        {
            sim=simulator;robot=actor;
            if(tablet==null)
            {
                font=GuardianFonts.Create(48);
                ownsFont=font!=null;
                if(font==null)font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                surface=new Material(Shader.Find("Sprites/Default")??Shader.Find("Standard")){color=new Color(.035f,.045f,.065f)};
                var shape=robot.PhysicsDriver.Shape;
                float front=shape.center.z+shape.size.z*.5f+.03f;
                tablet=Frame("Guardian chest tablet",new Vector3(0,shape.center.y,front),new Vector3(.52f,.38f,.035f),out tabletText);
                TabletHitTarget=tablet.GetComponent<BoxCollider>();
                overhead=Frame("Guardian alert mark",new Vector3(0,shape.center.y+shape.size.y*.5f+.43f,0),new Vector3(.92f,.34f,.035f),out overheadText);
            }
            Refresh();
        }
        private Transform Frame(string name,Vector3 position,Vector3 scale,out TextMesh text)
        {
            var root=GameObject.CreatePrimitive(PrimitiveType.Cube);root.name=name;root.transform.SetParent(transform,false);root.transform.localPosition=position;root.transform.localScale=scale;
            // Pickable but excluded from physical occupancy/braking queries.
            root.GetComponent<BoxCollider>().isTrigger=true;
            var renderer=root.GetComponent<MeshRenderer>();renderer.sharedMaterial=surface;renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
            var label=new GameObject("Alert symbol and Japanese status");label.transform.SetParent(root.transform,false);
            label.transform.localPosition=new Vector3(0,0,.6f);label.transform.localRotation=Quaternion.Euler(0,180,0);
            label.transform.localScale=new Vector3(1/scale.x,1/scale.y,1/scale.z);
            text=label.AddComponent<TextMesh>();text.font=font;text.fontSize=48;text.characterSize=.032f;text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;text.color=Color.white;
            label.GetComponent<MeshRenderer>().sharedMaterial=font.material;
            return root.transform;
        }
        public void Refresh()
        {
            if(robot==null || tablet==null)return;
            Visible=sim.Mode==GuardianSimulationMode.Physics && GuardianDemoInterface.HasAlert(sim,robot);
            bool stopped=robot.EmergencyStopped || robot.PhysicsDriver.SafetyHeld;
            Symbol=Visible?(stopped?"×":"!"):"";Caption=Visible?(stopped?"停止・確認":"制限あり"):"";
            overheadText.text=robot.RobotId+"  "+Symbol+" "+Caption;tabletText.text=Symbol+"\n"+Caption;
            tablet.gameObject.SetActive(Visible);overhead.gameObject.SetActive(Visible);
        }
        private void LateUpdate()
        {
            Refresh();
            if(Visible && Camera.main!=null)overhead.rotation=Quaternion.LookRotation(Camera.main.transform.position-overhead.position);
        }
        private void OnDestroy(){if(surface!=null)Destroy(surface);if(ownsFont)GuardianFonts.Release(font);}
    }
}
