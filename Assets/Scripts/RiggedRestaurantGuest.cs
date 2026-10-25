using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuestPose { Idle, Walk, Waiting, SeatReady, SitDown, Seated, StandUp, Handoff, HandoffStanding }

    /// <summary>Original guest's authored animation contract. Navigation and dish ownership stay with their authoritative systems.</summary>
    [DisallowMultipleComponent]
    public sealed class RiggedRestaurantGuest : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private Transform leftHandSocket, rightHandSocket, seatContact;
        [SerializeField] private GuestRigSettings rigSettings=new GuestRigSettings();
        private static readonly int[] States = {
            Animator.StringToHash("Base Layer.Idle"), Animator.StringToHash("Base Layer.Walk"),
            Animator.StringToHash("Base Layer.Waiting"), Animator.StringToHash("Base Layer.SeatReady"),
            Animator.StringToHash("Base Layer.SitDown"), Animator.StringToHash("Base Layer.Seated"),
            Animator.StringToHash("Base Layer.StandUp"), Animator.StringToHash("Base Layer.Handoff"),
            Animator.StringToHash("Base Layer.HandoffStanding") };
        public Animator Animator => animator;
        public Transform LeftHandSocket => leftHandSocket;
        public Transform RightHandSocket => rightHandSocket;
        public Transform SeatContact => seatContact;
        public GuestPose Pose { get; private set; }
        public bool HasSeat { get; private set; }
        public GuestRigSettings RigSettings => rigSettings;

        public void Configure(Animator value, Transform left, Transform right, Transform seat,GuestRigSettings settings=null)
        { animator=value; leftHandSocket=left; rightHandSocket=right; seatContact=seat;rigSettings=settings ?? new GuestRigSettings(); }

        public bool Play(GuestPose pose)
        {
            if ((uint)pose>=(uint)States.Length) return false;
            if (animator==null || !animator.isActiveAndEnabled || !animator.HasState(0,States[(int)pose])) return false;
            if (HasSeat && (pose==GuestPose.Idle || pose==GuestPose.Walk || pose==GuestPose.Waiting || pose==GuestPose.HandoffStanding)) return false;
            if ((pose==GuestPose.SeatReady || pose==GuestPose.SitDown || pose==GuestPose.Seated
                || pose==GuestPose.StandUp || pose==GuestPose.Handoff) && !HasSeat) return false;
            if (pose==GuestPose.SeatReady) return false; // Only BindSeat may change the coordinate frame.
            if (pose==GuestPose.SitDown && Pose!=GuestPose.SeatReady) return false;
            if ((pose==GuestPose.StandUp || pose==GuestPose.Handoff) && Pose!=GuestPose.Seated) return false;
            if (pose==GuestPose.Seated && Pose!=GuestPose.Seated
                && !((Pose==GuestPose.SitDown || Pose==GuestPose.Handoff) && Completed())) return false;
            Pose=pose; animator.Play(States[(int)pose],0,0); return true;
        }

        private bool Completed()
        {var state=animator.GetCurrentAnimatorStateInfo(0);return state.fullPathHash==States[(int)Pose] && state.normalizedTime>=.999f;}

        private void Update()
        {
            if (animator==null) return;
            if ((Pose==GuestPose.SitDown || Pose==GuestPose.Handoff) && Completed()) Play(GuestPose.Seated);
            else if (Pose==GuestPose.StandUp) FinishStanding();
            else if (Pose==GuestPose.HandoffStanding && Completed()) Play(GuestPose.Idle);
        }

        /// <summary>The frame is chair floor origin, facing the table. Call only after navigation reaches its approach.</summary>
        public bool BindSeat(Transform chairFrame,Transform footSupport=null)
        {
            if (chairFrame==null || HasSeat || animator==null || Pose==GuestPose.Walk || Pose==GuestPose.HandoffStanding
                || !animator.isActiveAndEnabled || !animator.HasState(0,States[(int)GuestPose.SeatReady])) return false;
            if (rigSettings.RequiresFootrest && (footSupport==null || Vector3.Distance(footSupport.position,chairFrame.position)>.01f
                || Quaternion.Angle(footSupport.rotation,chairFrame.rotation)>1 || footSupport.GetComponent<GuestFootSupport>()==null
                || Mathf.Abs(footSupport.GetComponent<GuestFootSupport>().SupportHeight-rigSettings.supportHeight)>.001f
                || Vector3.Distance(footSupport.lossyScale,Vector3.one)>.001f)) return false;
            Vector3 approach=chairFrame.TransformPoint(rigSettings.Approach);
            if (Vector3.Distance(transform.position,approach)>.04f || Quaternion.Angle(transform.rotation,chairFrame.rotation)>3) return false;
            transform.SetPositionAndRotation(chairFrame.position,chairFrame.rotation);
            HasSeat=true; Pose=GuestPose.SeatReady;animator.Play(States[(int)Pose],0,0);return true;
        }

        public bool FinishStanding()
        {
            if (!HasSeat || Pose!=GuestPose.StandUp || animator==null || !animator.isActiveAndEnabled
                || !animator.HasState(0,States[(int)GuestPose.Idle])) return false;
            AnimatorStateInfo state=animator.GetCurrentAnimatorStateInfo(0);
            if (state.fullPathHash!=States[(int)GuestPose.StandUp] || state.normalizedTime<.999f) return false;
            Vector3 standingPosition=transform.position+transform.rotation*rigSettings.Approach;
            // Prepare every fallible condition before committing root/seat changes.
            animator.Play(States[(int)GuestPose.Idle],0,0);
            transform.position=standingPosition; HasSeat=false;Pose=GuestPose.Idle;return true;
        }
    }
}
