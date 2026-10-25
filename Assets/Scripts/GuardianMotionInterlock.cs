using System;
using UnityEngine;

namespace Guardian.UnitySim
{
    [Flags] public enum GuardianMotionHold { None=0, Pedestrian=1, Unobserved=2, InputUnavailable=4, Operator=8 }
    public sealed class GuardianMotionInterlock : MonoBehaviour
    {
        public GuardianMotionHold Current { get; private set; }
        public GuardianMotionHold Latched { get; private set; }
        public bool Held => (Current|Latched)!=GuardianMotionHold.None;
        public float ClearSince { get; private set; }=-1;
        public string Reason => (Current|Latched).ToString();
        public void Set(GuardianMotionHold reason,bool active,float clock)
        {
            if(active) {Current|=reason;Latched|=reason;ClearSince=-1;}
            else {Current&=~reason;if(Current==GuardianMotionHold.None && ClearSince<0)ClearSince=clock;}
        }
        public void KeepStopped() {Latched|=GuardianMotionHold.Operator;}
        internal void ResetForScenario(float clock) {Current=Latched=GuardianMotionHold.None;ClearSince=clock-.6f;}
        public bool CanRelease(float clock) => Current==GuardianMotionHold.None && ClearSince>=0 && clock-ClearSince>=.6f;
        internal bool Release(float clock) {if(!CanRelease(clock))return false;Latched=GuardianMotionHold.None;return true;}
    }

    /// <summary>Reusable PhysX buffers grow on saturation. The All fallback never silently drops hits.</summary>
    public sealed class GuardianQueryBuffer
    {
        public RaycastHit[] Hits=new RaycastHit[32];public Collider[] Colliders=new Collider[32];public int Saturations;
        public int Ray(Vector3 origin,Vector3 direction,float distance)
        {
            while(true) {int n=Physics.RaycastNonAlloc(origin,direction,Hits,distance,~0,QueryTriggerInteraction.Ignore);if(n<Hits.Length)return n;Saturations++;if(Hits.Length>=4096){Hits=Physics.RaycastAll(origin,direction,distance,~0,QueryTriggerInteraction.Ignore);return Hits.Length;}Array.Resize(ref Hits,Hits.Length*2);}
        }
        public int Sphere(Vector3 origin,float radius,Vector3 direction,float distance)
        {
            while(true) {int n=Physics.SphereCastNonAlloc(origin,radius,direction,Hits,distance,~0,QueryTriggerInteraction.Ignore);if(n<Hits.Length)return n;Saturations++;if(Hits.Length>=4096){Hits=Physics.SphereCastAll(origin,radius,direction,distance,~0,QueryTriggerInteraction.Ignore);return Hits.Length;}Array.Resize(ref Hits,Hits.Length*2);}
        }
        public int Overlap(Vector3 origin,float radius)
        {
            while(true) {int n=Physics.OverlapSphereNonAlloc(origin,radius,Colliders,~0,QueryTriggerInteraction.Ignore);if(n<Colliders.Length)return n;Saturations++;if(Colliders.Length>=4096){Colliders=Physics.OverlapSphere(origin,radius,~0,QueryTriggerInteraction.Ignore);return Colliders.Length;}Array.Resize(ref Colliders,Colliders.Length*2);}
        }
        public int Capsule(Vector3 lower,Vector3 upper,float radius,Vector3 direction,float distance)
        {
            while(true) {int n=Physics.CapsuleCastNonAlloc(lower,upper,radius,direction,Hits,distance,~0,QueryTriggerInteraction.Ignore);if(n<Hits.Length)return n;Saturations++;if(Hits.Length>=4096){Hits=Physics.CapsuleCastAll(lower,upper,radius,direction,distance,~0,QueryTriggerInteraction.Ignore);return Hits.Length;}Array.Resize(ref Hits,Hits.Length*2);}
        }
        public int OverlapCapsule(Vector3 lower,Vector3 upper,float radius)
        {
            while(true) {int n=Physics.OverlapCapsuleNonAlloc(lower,upper,radius,Colliders,~0,QueryTriggerInteraction.Ignore);if(n<Colliders.Length)return n;Saturations++;if(Colliders.Length>=4096){Colliders=Physics.OverlapCapsule(lower,upper,radius,~0,QueryTriggerInteraction.Ignore);return Colliders.Length;}Array.Resize(ref Colliders,Colliders.Length*2);}
        }
        public int Box(Vector3 center,Vector3 half,Quaternion rotation)
        {
            while(true){int n=Physics.OverlapBoxNonAlloc(center,half,Colliders,rotation,~0,QueryTriggerInteraction.Ignore);if(n<Colliders.Length)return n;Saturations++;if(Colliders.Length>=4096){Colliders=Physics.OverlapBox(center,half,rotation,~0,QueryTriggerInteraction.Ignore);return Colliders.Length;}Array.Resize(ref Colliders,Colliders.Length*2);}
        }
    }
}
