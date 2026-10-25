using System;
using System.Collections.Generic;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Configurable simulation assumptions, NOT identified hardware parameters.</summary>
    [Serializable] public sealed class GuardianPhysicalSettings
    {
        public bool Enabled;
        public float ChassisMass=65,DriveForce=390,BrakeForce=520,YawTorque=80,MaximumYawAcceleration=8,MaximumYawRate=3;
        public Vector3 ChassisCenter=new Vector3(0,.30f,0);
        public float WheelFriction=.8f,Gravity=9.81f,MaximumLateralAcceleration=1.8f,BrakeReserve=.75f;
        public float SensorPeriod=.08f,DriverResponseDelay=.04f,StoppingMargin=.12f;
        public float WheelSupportHalfWidth=.25f,WheelSupportHalfLength=.28f;
        public float SoupMass=1,FoodMass=.9f,EmptyDishMass=.35f,CargoCenterHeight=.08f,CargoStaticFriction=.35f,CargoSupportHalfWidth=.12f,CargoSupportHalfLength=.12f;
        public void Validate()
        {
            foreach(float v in new[]{ChassisMass,DriveForce,BrakeForce,YawTorque,MaximumYawAcceleration,MaximumYawRate,WheelFriction,Gravity,MaximumLateralAcceleration,BrakeReserve,WheelSupportHalfWidth,WheelSupportHalfLength,SoupMass,FoodMass,EmptyDishMass,CargoCenterHeight,CargoStaticFriction,CargoSupportHalfWidth,CargoSupportHalfLength})
                if(!float.IsFinite(v) || v<=0)throw new ArgumentException("Physical assumptions must be finite and positive");
            foreach(float v in new[]{SensorPeriod,DriverResponseDelay,StoppingMargin})if(!float.IsFinite(v) || v<0)throw new ArgumentException("Invalid physical timing/margin");
            if(BrakeReserve>1 || !float.IsFinite(ChassisCenter.x) || !float.IsFinite(ChassisCenter.y) || !float.IsFinite(ChassisCenter.z) || ChassisCenter.y<=0)throw new ArgumentException("Invalid physical center/reserve");
        }
    }
    [Serializable] public struct GuardianPayloadPoint
    {
        public float Mass;public Vector3 Center;
        public float CenterHeight,StaticFriction,SupportHalfWidth,SupportHalfLength;
        public GuardianPayloadPoint(float mass,Vector3 center,GuardianPhysicalSettings p)
        {Mass=mass;Center=center;CenterHeight=p.CargoCenterHeight;StaticFriction=p.CargoStaticFriction;SupportHalfWidth=p.CargoSupportHalfWidth;SupportHalfLength=p.CargoSupportHalfLength;}
    }
    [Serializable] public struct GuardianLoadRisk
    {
        public float Acceleration,LateralAcceleration,PayloadPointAcceleration,SupportUtilization,SlideUtilization,ItemTipUtilization;
    }

    /// <summary>One force/torque actuator, called only by the driver (or an isolated physical test).
    /// Planar constrained chassis with lumped cargo; no rigid cargo, tilt or liquid solver.</summary>
    public sealed class GuardianPlanarDynamics
    {
        public GuardianPhysicalSettings Assumptions {get;}
        public Rigidbody Body {get;}
        public float Mass {get;private set;}public Vector3 Center {get;private set;}public Vector3 Inertia {get;private set;}
        public Vector3 LastForce {get;private set;}public float LastYawTorque {get;private set;}
        public GuardianLoadRisk Risk {get;private set;}
        public float DriveAcceleration=>Mathf.Min(Assumptions.DriveForce/Mass,Assumptions.WheelFriction*Assumptions.Gravity);
        public float BrakeAcceleration=>Mathf.Min(Assumptions.BrakeForce/Mass,Assumptions.WheelFriction*Assumptions.Gravity);
        public float ConservativeBrakeAcceleration=>BrakeAcceleration*Assumptions.BrakeReserve;
        public Vector3 RootVelocity {get {Vector3 v=Body.GetPointVelocity(Body.position);v.y=0;return v;}}
        public Vector3 LastRootCommand {get;private set;}
        public bool SafetyBrakeActive {get;private set;}
        public float LastYawCommand=>lastOmegaCommand;
        public bool RetainsPreviousOnStop(float dt)=>!stopping || stopAge+1e-6f<QuantizedSteps(Assumptions.DriverResponseDelay,dt)*dt;
        private readonly Vector3 chassisSize;
        private readonly List<GuardianPayloadPoint> loads=new List<GuardianPayloadPoint>();
        private Vector3 previousVelocity,previousOmega;private float lastOmegaCommand,stopAge;private bool stopping;
        private int inventoryRevision=-1;
        public GuardianPlanarDynamics(Rigidbody body,Vector3 size,GuardianPhysicalSettings settings)
        {
            settings.Validate();Body=body;chassisSize=size;Assumptions=settings;
            Body.linearDamping=0;Body.angularDamping=0;Body.sleepThreshold=0;SetPayload(Array.Empty<GuardianPayloadPoint>());
            ResetObservationForScenario();
        }
        public void SetPayload(IReadOnlyList<GuardianPayloadPoint> value)
        {
            loads.Clear();Mass=Assumptions.ChassisMass;Vector3 weighted=Assumptions.ChassisCenter*Mass;
            foreach(var p in value)
            {
                if(!float.IsFinite(p.Mass)||p.Mass<=0||!float.IsFinite(p.Center.x)||!float.IsFinite(p.Center.y)||!float.IsFinite(p.Center.z)||p.CenterHeight<=0||p.StaticFriction<=0||p.SupportHalfWidth<=0||p.SupportHalfLength<=0)throw new ArgumentException("Invalid lumped cargo assumption");
                foreach(float v in new[]{p.CenterHeight,p.StaticFriction,p.SupportHalfWidth,p.SupportHalfLength})if(!float.IsFinite(v))throw new ArgumentException("Nonfinite cargo assumption");
                loads.Add(p);Mass+=p.Mass;weighted+=p.Center*p.Mass;
            }
            if(!float.IsFinite(Mass))throw new ArgumentException("Nonfinite total mass");Center=weighted/Mass;Vector3 s=chassisSize;
            Inertia=Assumptions.ChassisMass/12*new Vector3(s.y*s.y+s.z*s.z,s.x*s.x+s.z*s.z,s.x*s.x+s.y*s.y);
            Inertia+=ParallelAxis(Assumptions.ChassisMass,Assumptions.ChassisCenter-Center);
            foreach(var p in loads)Inertia+=ParallelAxis(p.Mass,p.Center-Center);
            Body.mass=Mass;Body.centerOfMass=Center;Body.inertiaTensor=Inertia;Body.inertiaTensorRotation=Quaternion.identity;
        }
        private static Vector3 ParallelAxis(float mass,Vector3 d)=>mass*new Vector3(d.y*d.y+d.z*d.z,d.x*d.x+d.z*d.z,d.x*d.x+d.y*d.y);
        public void RefreshInventory(GuardianDishInventory inventory,GuardianRobot robot)
        {
            if(inventory==null || inventory.Events.Count==inventoryRevision)return;
            inventoryRevision=inventory.Events.Count;var points=new List<GuardianPayloadPoint>();var tray=robot.GetComponent<GuardianCargoTray>();
            foreach(var item in inventory.Items.Values)
                if(item.Owner==GuardianDishOwner.Robot && item.OwnerKey==robot.RobotId && item.TraySlot>=0 && item.TraySlot<tray.Anchors.Count)
                {
                    Vector3 center=robot.transform.InverseTransformPoint(tray.Anchors[item.TraySlot].position)+Vector3.up*Assumptions.CargoCenterHeight;
                    points.Add(new GuardianPayloadPoint(item.Dirty?Assumptions.EmptyDishMass:item.Menu==GuardianMenu.Soup?Assumptions.SoupMass:Assumptions.FoodMass,center,Assumptions));
                }
            SetPayload(points);
        }
        // Unity's timeScale conversion can return .01999999 for a .02 step.
        // Ignore sub-1e-4-step rounding noise, not a material timing interval.
        public static int QuantizedSteps(float seconds,float dt)=>Mathf.Max(0,Mathf.CeilToInt(seconds/dt-.0001f));
        public float ReactionSeconds(float dt)=>(QuantizedSteps(Assumptions.SensorPeriod,dt)+1+QuantizedSteps(Assumptions.DriverResponseDelay,dt))*dt;
        public void ResetObservationForScenario()
        {previousVelocity=Body.linearVelocity;previousOmega=Body.angularVelocity;Risk=default;LastForce=LastRootCommand=Vector3.zero;LastYawTorque=lastOmegaCommand=stopAge=0;stopping=false;}
        public float StoppingBound(float speed,float speedCap,float dt,bool includePreviousCommand=true)
        {
            speed=Mathf.Max(0,speed);speedCap=Mathf.Max(speed,Mathf.Max(speedCap,includePreviousCommand?LastRootCommand.magnitude:0));
            float t=ReactionSeconds(dt),a=DriveAcceleration,ta=Mathf.Min(t,(speedCap-speed)/a),v=speed+a*ta;
            return speed*ta+.5f*a*ta*ta+v*(t-ta)+v*v/(2*ConservativeBrakeAcceleration)+Assumptions.StoppingMargin;
        }
        public float ClearanceSpeed(float speed,float requested,float clearance,float dt)
        {
            // Ordinary navigation speed commands take effect this control step.
            // A future safety hold retains THAT new command during driver delay.
            // Reduce the new command before accelerating into an unrecoverable gap.
            if(StoppingBound(speed,0,dt,false)>clearance)return -1;
            float low=0,high=requested;for(int i=0;i<16;i++){float mid=(low+high)*.5f;if(StoppingBound(speed,mid,dt,false)<=clearance)low=mid;else high=mid;}return low;
        }
        public void Observe(float dt)
        {
            Vector3 acceleration=(Body.linearVelocity-previousVelocity)/dt,alpha=(Body.angularVelocity-previousOmega)/dt;
            previousVelocity=Body.linearVelocity;previousOmega=Body.angularVelocity;
            Vector3 local=Quaternion.Inverse(Body.rotation)*acceleration;local.y=0;
            Vector3 support=Center-new Vector3(local.x,0,local.z)*Center.y/Assumptions.Gravity;
            var r=new GuardianLoadRisk{Acceleration=local.magnitude,LateralAcceleration=Mathf.Abs(local.x),SupportUtilization=Mathf.Max(Mathf.Abs(support.x)/Assumptions.WheelSupportHalfWidth,Mathf.Abs(support.z)/Assumptions.WheelSupportHalfLength)};
            foreach(var item in loads)
            {
                Vector3 offset=Body.rotation*(item.Center-Center),point=acceleration+Vector3.Cross(alpha,offset)+Vector3.Cross(Body.angularVelocity,Vector3.Cross(Body.angularVelocity,offset));
                point=Quaternion.Inverse(Body.rotation)*point;point.y=0;r.PayloadPointAcceleration=Mathf.Max(r.PayloadPointAcceleration,point.magnitude);
                r.SlideUtilization=Mathf.Max(r.SlideUtilization,point.magnitude/(item.StaticFriction*Assumptions.Gravity));
                r.ItemTipUtilization=Mathf.Max(r.ItemTipUtilization,Mathf.Max(Mathf.Abs(point.x)*item.CenterHeight/(Assumptions.Gravity*item.SupportHalfWidth),Mathf.Abs(point.z)*item.CenterHeight/(Assumptions.Gravity*item.SupportHalfLength)));
            }
            Risk=r;
        }
        public void Apply(Vector3 desiredRootVelocity,float desiredOmega,float dt,bool safetyStop)
        {
            Observe(dt);desiredRootVelocity.y=0;SafetyBrakeActive=false;
            if(safetyStop)
            {
                if(!stopping){stopping=true;stopAge=0;}
                // A bounded response delay holds the preceding command. No velocity
                // reset, FreezeAll, transform movement or NavMesh agent is involved.
                if(stopAge+1e-6f<QuantizedSteps(Assumptions.DriverResponseDelay,dt)*dt){desiredRootVelocity=LastRootCommand;desiredOmega=lastOmegaCommand;}
                else {desiredRootVelocity=Vector3.zero;desiredOmega=0;SafetyBrakeActive=true;}stopAge+=dt;
            }
            else {stopping=false;stopAge=0;LastRootCommand=desiredRootVelocity;lastOmegaCommand=desiredOmega;}
            desiredOmega=Mathf.Clamp(desiredOmega,-Assumptions.MaximumYawRate,Assumptions.MaximumYawRate);
            if(Mathf.Abs(desiredOmega)>.001f)desiredRootVelocity=Vector3.ClampMagnitude(desiredRootVelocity,Assumptions.MaximumLateralAcceleration/Mathf.Abs(desiredOmega));
            // Control the actor origin even when cargo moves the COM. Its velocity
            // differs from COM velocity by omega x (origin-COM).
            Vector3 targetCom=desiredRootVelocity+Vector3.Cross(Body.angularVelocity,Body.worldCenterOfMass-Body.position);
            Vector3 error=targetCom-Body.linearVelocity;error.y=0;
            bool braking=Vector3.Dot(error,RootVelocity)<0;
            LastForce=Mass*Vector3.ClampMagnitude(error/dt,braking?BrakeAcceleration:DriveAcceleration);
            Body.AddForce(LastForce,ForceMode.Force);
            float alpha=Mathf.Clamp((desiredOmega-Body.angularVelocity.y)/dt,-Assumptions.MaximumYawAcceleration,Assumptions.MaximumYawAcceleration);
            LastYawTorque=Mathf.Clamp(Inertia.y*alpha,-Assumptions.YawTorque,Assumptions.YawTorque);Body.AddTorque(Vector3.up*LastYawTorque,ForceMode.Force);
        }
    }
}
