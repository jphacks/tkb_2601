using UnityEngine;
namespace Guardian.UnitySim
{
    /// <summary>Conservative yaw/translation occupancy, including delayed finite braking.
    /// Each angular interval is enclosed, rather than testing only discrete corners.</summary>
    public sealed class GuardianRotationEnvelope
    {
        private readonly GuardianQueryBuffer query=new GuardianQueryBuffer();
        private const float Skin=.012f,AngleStep=2*Mathf.Deg2Rad;
        public bool InitialOverlap {get;private set;}public int Queries {get;private set;}
        public bool Clear(GuardianPhysicsDriver driver,Vector3 command,float omegaCommand,float dt)
        {
            InitialOverlap=false;Queries=0;var body=driver.Body;var shape=driver.Shape;var model=driver.Dynamics;
            if(model==null)return true;
            if(Hit(driver,body.position,body.rotation,shape.size*.5f,Vector3.zero,true))return false;
            bool pending=model.RetainsPreviousOnStop(dt);
            float w=Mathf.Abs(body.angularVelocity.y),cap=Mathf.Max(w,Mathf.Max(Mathf.Abs(omegaCommand),pending?Mathf.Abs(model.LastYawCommand):0));
            if(cap<.001f)return true;
            float a=Mathf.Min(model.Assumptions.MaximumYawAcceleration,model.Assumptions.YawTorque/model.Inertia.y),b=a*model.Assumptions.BrakeReserve,tr=model.ReactionSeconds(dt);
            float ta=Mathf.Min(tr,Mathf.Max(0,cap-w)/a),wd=w+a*ta;
            float angle=w*ta+.5f*a*ta*ta+wd*(tr-ta)+wd*wd/(2*b);
            float duration=Mathf.Max(tr+wd/b,tr+Mathf.Max(driver.PlanarVelocity.magnitude,Mathf.Max(command.magnitude,pending?model.LastRootCommand.magnitude:0))/model.ConservativeBrakeAcceleration);
            if(!float.IsFinite(angle)||angle>4096*AngleStep||!float.IsFinite(duration)||dt<=0||!float.IsFinite(dt)||duration/dt>4096)return false;
            Vector3 min=Vector3.zero,max=Vector3.zero;
            TranslationBounds(driver.PlanarVelocity,command,model,tr,duration,dt,ref min,ref max);
            if(pending)TranslationBounds(driver.PlanarVelocity,model.LastRootCommand,model,tr,duration,dt,ref min,ref max);
            Vector3 mid=(min+max)*.5f,span=(max-min)*.5f;mid.y=span.y=0;
            float comRadius=new Vector2(model.Center.x,model.Center.z).magnitude;
            float radius=driver.TurningRadius+Skin+2*comRadius;
            // Fast broad rejection; the square contains the complete rotation disc
            // at every possible translational position in the forecast rectangle.
            Vector3 broadCenter=body.position+mid+Vector3.up*shape.center.y;
            int n=query.Box(broadCenter,new Vector3(radius+span.x,shape.size.y*.5f,radius+span.z),Quaternion.identity);Queries++;
            bool relevant=false;for(int i=0;i<n;i++)if(Blocks(query.Colliders[i],body)){relevant=true;break;}if(!relevant)return true;
            float sign=w>.001f?Mathf.Sign(body.angularVelocity.y):Mathf.Abs(omegaCommand)>.001f?Mathf.Sign(omegaCommand):Mathf.Sign(model.LastYawCommand);
            int intervals=Mathf.CeilToInt(angle/AngleStep);
            if(intervals>4096)return false; // Fail closed for extreme uncalibrated parameters; never truncate the sweep.
            bool reverse=body.angularVelocity.y*omegaCommand<-.000001f || (pending && model.LastYawCommand*omegaCommand<-.000001f) || (pending && body.angularVelocity.y*model.LastYawCommand<-.000001f);
            for(int branch=0;branch<(reverse?2:1);branch++)for(int i=0;i<Mathf.Max(1,intervals);i++)
            {
                float lo=i*AngleStep,hi=Mathf.Min(angle,(i+1)*AngleStep),yaw=(lo+hi)*.5f;
                Quaternion rotation=Quaternion.AngleAxis((branch==0?sign:-sign)*yaw*Mathf.Rad2Deg,Vector3.up)*body.rotation;
                // Maximum chord displacement from the interval midpoint encloses
                // all intermediate angles, including objects thinner than a step.
                float inflate=2*driver.TurningRadius*Mathf.Sin((hi-lo)*.25f)+Skin+2*comRadius;
                Vector3 localSpan=Abs(Quaternion.Inverse(rotation)*new Vector3(span.x,0,0))+Abs(Quaternion.Inverse(rotation)*new Vector3(0,0,span.z));
                if(Hit(driver,body.position+mid,rotation,shape.size*.5f+localSpan+new Vector3(inflate,0,inflate),Vector3.zero,false))return false;
            }
            return true;
        }
        private static void TranslationBounds(Vector3 velocity,Vector3 command,GuardianPlanarDynamics model,float tr,float duration,float dt,ref Vector3 min,ref Vector3 max)
        {
            Vector3 p=Vector3.zero;
            for(float t=0;t<duration;t+=dt)
            {float h=Mathf.Min(dt,duration-t);Vector3 next=t+1e-6f<tr?Vector3.MoveTowards(velocity,command,model.DriveAcceleration*h):Vector3.MoveTowards(velocity,Vector3.zero,model.ConservativeBrakeAcceleration*h);p+=(velocity+next)*(.5f*h);min=Vector3.Min(min,p);max=Vector3.Max(max,p);velocity=next;}
        }
        private bool Hit(GuardianPhysicsDriver driver,Vector3 root,Quaternion rotation,Vector3 half,Vector3 offset,bool initial)
        {
            int n=query.Box(root+rotation*driver.Shape.center+offset,half,rotation);Queries++;
            for(int i=0;i<n;i++)
            {
                var c=query.Colliders[i];if(!Blocks(c,driver.Body))continue;
                if(initial)
                {
                    ColliderPose(c,out Vector3 p,out Quaternion q);
                    if(Physics.ComputePenetration(driver.Shape,driver.Body.position,driver.Body.rotation,c,p,q,out var axis,out float depth)&&depth>0){InitialOverlap=true;return true;}
                }
                else return true;
            }
            return false;
        }
        private static void ColliderPose(Collider collider,out Vector3 p,out Quaternion q)
        {
            var rb=collider.attachedRigidbody;p=collider.transform.position;q=collider.transform.rotation;if(rb==null)return;
            // Preserve the CHILD collider's offset/orientation, replacing only
            // the interpolated parent frame by the current simulation pose.
            Quaternion delta=rb.rotation*Quaternion.Inverse(rb.transform.rotation);
            p=rb.position+delta*(p-rb.transform.position);q=delta*q;
        }
        private static bool Blocks(Collider c,Rigidbody self)=>c!=null && c.attachedRigidbody!=self && c.GetComponent<GuardianPhysicsObstacle>()?.Floor!=true;
        private static Vector3 Abs(Vector3 v)=>new Vector3(Mathf.Abs(v.x),Mathf.Abs(v.y),Mathf.Abs(v.z));
    }
}
