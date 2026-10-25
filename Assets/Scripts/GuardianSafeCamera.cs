using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed class GuardianCameraObstacle : MonoBehaviour { }
    /// <summary>
    /// One camera controller; the eye sphere contains the whole near plane. The eye always eases toward
    /// where the view asks for, never snaps: it stays above people, robots and tables (MinEyeHeight),
    /// rises over anything taller instead of jumping toward the pivot, keeps off the ceiling and the
    /// inside of the walls, and walls between the eye and the room are cut away (RestaurantWallCutaway).
    /// </summary>
    public sealed class GuardianSafeCamera : MonoBehaviour
    {
        public Camera View {get;private set;}public Transform Follow {get;private set;}
        public Vector3 Pivot=new Vector3(0,.8f,0);public float Yaw=0,Pitch=58,Distance=9;
        public bool Valid {get;private set;}public bool TargetOccluded {get;private set;}
        public float Radius {get;private set;}public int Recoveries {get;private set;}
        private readonly GuardianQueryBuffer query=new GuardianQueryBuffer();private SphereCollider probe;private GameObject probeObject;
        private Vector3 lastSafe;private bool haveSafe;
        /// <summary>The last eye position that touched nothing.</summary>
        public Vector3 LastSafe=>haveSafe?lastSafe:View.transform.position;
        public static Bounds Room => new Bounds(new Vector3(0,4.7f,0),new Vector3(19,8.7f,11.6f));
        /// <summary>
        /// Where the eye may go: all round the room for wide views. Walls between the eye and the room
        /// are cut away (RestaurantWallCutaway), so orbiting never stops at a wall.
        /// </summary>
        public static Bounds EyeSpace => new Bounds(new Vector3(0,7.18f,0),new Vector3(32,13.65f,32));
        public void Initialize(Camera camera)
        {
            View=camera;View.nearClipPlane=.12f;View.farClipPlane=80;View.fieldOfView=58;
            probeObject=new GameObject("Disabled camera clearance probe");probeObject.hideFlags=HideFlags.HideAndDontSave;probe=probeObject.AddComponent<SphereCollider>();probe.enabled=false;ResetView();
        }
        public static float NearRadius(float near,float fov,float aspect)
        {float h=near*Mathf.Tan(fov*Mathf.Deg2Rad*.5f);return Mathf.Sqrt(near*near+h*h*(1+aspect*aspect))+.045f;}
        private bool Obstacle(Collider c)
        {
            if(c==null)return false;if(RestaurantWallCutaway.IsRoomWall(c))return false;  // cut away instead of blocking the eye
            if(c.GetComponent<GuardianPhysicsObstacle>()!=null || c.GetComponent<GuardianCameraObstacle>()!=null)return true;
            var r=c.GetComponentInParent<GuardianRobot>();if(r!=null)return r.transform!=Follow;
            var p=c.GetComponentInParent<GuardianPedestrianMotion>();return p!=null && p.transform!=Follow;
        }
        private Vector3 Clamp(Vector3 p)
        {Bounds b=EyeSpace;return new Vector3(Mathf.Clamp(p.x,b.min.x+Radius,b.max.x-Radius),Mathf.Clamp(p.y,b.min.y+Radius,b.max.y-Radius),Mathf.Clamp(p.z,b.min.z+Radius,b.max.z-Radius));}
        public bool Clear(Vector3 p)
        {int n=query.Overlap(p,Radius);for(int i=0;i<n;i++)if(Obstacle(query.Colliders[i]) && Physics.ComputePenetration(probe,p,Quaternion.identity,query.Colliders[i],query.Colliders[i].transform.position,query.Colliders[i].transform.rotation,out Vector3 d,out float depth) && depth>.0005f)return false;return true;}
        /// <summary>Eye height kept above people (1.8 m), robots and tables, so passing guests never push the camera.</summary>
        public const float MinEyeHeight=2.3f;
        /// <summary>Raise the eye straight up out of anything it overlaps (furniture taller than MinEyeHeight, a test occluder).</summary>
        private Vector3 Lift(Vector3 p)
        {
            for(int pass=0;pass<4;pass++)
            {
                float top=float.NegativeInfinity;int n=query.Overlap(p,Radius);
                for(int i=0;i<n;i++)
                {
                    Collider c=query.Colliders[i];
                    if(Obstacle(c) && Physics.ComputePenetration(probe,p,Quaternion.identity,c,c.transform.position,c.transform.rotation,out Vector3 d,out float depth) && depth>.0005f)
                        top=Mathf.Max(top,c.bounds.max.y);
                }
                if(float.IsNegativeInfinity(top))return p;
                p.y=Mathf.Min(top+Radius+.02f,EyeSpace.max.y-Radius);Recoveries++;
            }
            return p;
        }
        /// <summary>Not touching the ceiling or the one-sided south and east walls from inside: step through to the far side.</summary>
        private Vector3 OffSurfaces(Vector3 p)
        {
            bool inside=p.x>RestaurantEnclosure.West && p.x<RestaurantEnclosure.East && p.z>RestaurantEnclosure.South && p.z<RestaurantEnclosure.North;
            if(!inside)return p;
            float h=RestaurantEnclosure.Height,m=Radius+.05f;
            if(p.y>h-m && p.y<h+m)p.y=h+m;
            if(p.z<RestaurantEnclosure.South+m)p.z=RestaurantEnclosure.South-.02f;
            if(p.x>RestaurantEnclosure.East-m)p.x=RestaurantEnclosure.East+.02f;
            return p;
        }
        private Vector3 Safe(Vector3 p)=>Lift(OffSurfaces(p));
        public void Step(float dt)
        {
            if(View==null)return;
            GuardianFrameMarker.Enter("camera");
            View.fieldOfView=Mathf.Clamp(View.fieldOfView,35,70);Radius=NearRadius(View.nearClipPlane,View.fieldOfView,Mathf.Max(.2f,View.aspect));probe.radius=Radius;
            if(Follow!=null)Pivot=Vector3.Lerp(Pivot,Follow.position+Vector3.up*.9f,1-Mathf.Exp(-8*Mathf.Max(0,dt)));
            Pivot=new Vector3(Mathf.Clamp(Pivot.x,-8.5f,8.5f),Mathf.Clamp(Pivot.y,.5f,2),Mathf.Clamp(Pivot.z,-5.1f,5.1f));
            Yaw=Mathf.DeltaAngle(0,Yaw);Pitch=Mathf.Clamp(Pitch,30,80);Distance=Mathf.Clamp(Distance,2,14);
            Vector3 desired=Clamp(Pivot-Quaternion.Euler(Pitch,Yaw,0)*Vector3.forward*Distance);
            desired.y=Mathf.Max(desired.y,MinEyeHeight);
            desired=Safe(desired);
            // Ease toward the wanted eye; if the eased point would touch something, lift it, and if it still
            // does (the eye was put inside something), go straight to the wanted point, which is clear.
            Vector3 next=Safe(Vector3.Lerp(View.transform.position,desired,1-Mathf.Exp(-10*Mathf.Max(0,dt))));
            if(!Clear(next))next=desired;
            Valid=Clear(next);
            View.transform.position=next;if((Pivot-next).sqrMagnitude>.001f)View.transform.rotation=Quaternion.LookRotation(Pivot-next,Vector3.up);
            if(Valid){lastSafe=next;haveSafe=true;}
            View.enabled=true;
            RestaurantWallCutaway.Apply(next,Radius);
            TargetOccluded=false;
            if(Follow!=null)
            {Vector3 ray=Follow.position+Vector3.up*.9f-next;int n=query.Ray(next,ray.normalized,ray.magnitude);for(int i=0;i<n;i++)if(Obstacle(query.Hits[i].collider)){TargetOccluded=true;break;}}
        }
        public void Focus(Transform target) {Follow=target;if(target!=null)Pivot=target.position+Vector3.up*.9f;Distance=5;}
        public void Orbit(Vector2 pixels) {if(!float.IsFinite(pixels.x)||!float.IsFinite(pixels.y))return;Yaw+=pixels.x*.25f;Pitch-=pixels.y*.20f;}
        public void Pan(Vector2 pixels)
        {if(!float.IsFinite(pixels.x)||!float.IsFinite(pixels.y))return;Follow=null;Vector3 right=View.transform.right;Vector3 forward=Vector3.Cross(right,Vector3.up);Pivot+=(-right*pixels.x-forward*pixels.y)*Distance*.0018f;}
        public void Zoom(float wheel) {if(float.IsFinite(wheel))Distance*=Mathf.Exp(-Mathf.Clamp(wheel,-20,20)*.18f);}
        public void ResetView() {Follow=null;Pivot=new Vector3(0,.8f,0);Yaw=0;Pitch=58;Distance=9;Step(1);}
        public void AislePreset() {Follow=null;Pivot=new Vector3(-1,.8f,0);Yaw=25;Pitch=46;Distance=7;Step(1);}
        private void OnDestroy() {if(probeObject!=null)Destroy(probeObject);}
    }
}
