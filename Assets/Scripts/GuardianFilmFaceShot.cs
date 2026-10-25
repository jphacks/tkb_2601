using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// The film's face shot: while a robot speaks, the main camera stands in front of its face screen
    /// (three-quarter view, a little below eye level) and follows it. It runs after the camera rig and keeps its
    /// own eye position, so the rig's step in between cannot pull it about (that tug of war shook the frame);
    /// the film cuts back to the rig's view when the line ends. The overhead rig keeps a minimum eye height, so it
    /// cannot take this shot itself.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class GuardianFilmFaceShot : MonoBehaviour
    {
        /// <summary>The robot to film; null leaves the camera to the rig.</summary>
        public GuardianRobot Target;
        public float Distance = 1.45f, Side = .4f, Below = .08f;

        private GuardianRobot framed;
        private Vector3 eye, look, facing, eyeVelocity, lookVelocity;

        private void LateUpdate()
        {
            var cam = Camera.main;
            if (Target == null || cam == null) { framed = null; return; }
            Vector3 face = Face(Target);
            Vector3 forward = Vector3.ProjectOnPlane(Target.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward;
            bool cut = framed != Target;
            // A turning robot swings the camera round slowly instead of whipping the frame with it.
            facing = cut ? forward : Vector3.Slerp(facing, forward, 1 - Mathf.Exp(-Time.deltaTime / 1.2f)).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, facing);
            Vector3 wanted = Unblocked(face, face + facing * Distance + right * Side - Vector3.up * Below);
            if (cut) { framed = Target; eye = wanted; look = face; eyeVelocity = lookVelocity = Vector3.zero; }
            else
            {
                eye = Vector3.SmoothDamp(eye, wanted, ref eyeVelocity, .3f);
                look = Vector3.SmoothDamp(look, face, ref lookVelocity, .15f);
            }
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(look - eye, Vector3.up));
        }

        // Centre of the face screen; without one, the upper part of the robot.
        private static Vector3 Face(GuardianRobot robot)
        {
            Transform display = robot.transform.Find(GuardianRobotFace.DisplayName);
            return display != null ? display.position : robot.transform.position + Vector3.up * 1.5f;
        }

        // A table, wall or another robot between the face and the camera pulls the camera in front of it.
        private Vector3 Unblocked(Vector3 face, Vector3 eye)
        {
            Vector3 ray = eye - face;
            float length = ray.magnitude, nearest = length;
            foreach (RaycastHit hit in Physics.RaycastAll(face, ray / length, length, ~0, QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(Target.transform) && hit.distance < nearest) nearest = hit.distance;
            return nearest < length ? face + ray / length * Mathf.Max(.6f, nearest - .1f) : eye;
        }
    }
}
