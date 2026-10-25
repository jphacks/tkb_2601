using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianSimulator
    {
        public int MotionSpeedMultiplier=>Settings.MotionSpeedMultiplier;
        public bool TrySetMotionSpeed(int multiplier)
        {
            if(multiplier!=1 && multiplier!=2) return false;
            Settings.MotionSpeedMultiplier=multiplier;
            Log("Physical translation speed "+multiplier+"x; clocks, meal timers, turn and parking limits unchanged");
            return true;
        }
        internal void MotionControlsGUI()
        {
            GUILayout.Label("移動速度 "+MotionSpeedMultiplier+"x / 食事・返答待ちの時計は通常");
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("移動 1x")) TrySetMotionSpeed(1);
            if(GUILayout.Button("移動 2x")) TrySetMotionSpeed(2);
            GUILayout.EndHorizontal();
            GUILayout.Label("曲がり角・受渡・駐車・混雑時は減速します。");
            bool direct=Settings.Traffic==GuardianTrafficStyle.DirectConcurrent;
            CameraControlsGUI();
            GUILayout.Label(direct?"走行: 同時・直線ルート（交差は譲り合い）":"走行: 単線通路（旧方式・1台ずつ）");
            GUILayout.BeginHorizontal();
            if(GUILayout.Button(direct?"旧方式に切替(リセット)":"同時走行に切替(リセット)")) SetTrafficStyle(direct?GuardianTrafficStyle.SingleTrackAisle:GuardianTrafficStyle.DirectConcurrent);
            if(GUILayout.Button(Settings.BusyOrders?"注文: 多め":"注文: 標準")) { Settings.BusyOrders=!Settings.BusyOrders; Log("Seeded order pacing "+(Settings.BusyOrders?"busy (x0.35 interval)":"calm")); }
            GUILayout.EndHorizontal();
        }
        // Camera presets for demo footage: the original overview, a closer 3/4 view of the dining
        // floor (faces readable), and a camera that drives ahead of one robot looking at its face.
        public enum CameraView { Overview, Dining, Follow }
        public CameraView View {get;private set;}
        private Vector3 overviewPosition;private Quaternion overviewRotation;private bool overviewSaved;private int followIndex;
        public void CycleCamera()
        {
            if(View==CameraView.Follow && _robots.Count>0 && followIndex+1<_robots.Count) followIndex++;
            else {View=(CameraView)(((int)View+1)%3);followIndex=0;}
            Log("Camera "+View+(View==CameraView.Follow && _robots.Count>0?" / Robot "+_robots[followIndex].RobotId:""));
        }
        private void ApplyCameraPreset()
        {
            Transform cam=_camera.transform;
            if(!overviewSaved) {overviewPosition=cam.position;overviewRotation=cam.rotation;overviewSaved=true;}
            Vector3 position=overviewPosition;Quaternion rotation=overviewRotation;float rate=6;
            if(View==CameraView.Dining)
            {position=new Vector3(.6f,6.6f,-9f);rotation=Quaternion.LookRotation(new Vector3(-1.2f,.4f,1.1f)-position);}
            else if(View==CameraView.Follow && _robots.Count>0)
            {
                var robot=_robots[Mathf.Clamp(followIndex,0,_robots.Count-1)].transform;
                Vector3 ahead=robot.forward;ahead.y=0;ahead=ahead.sqrMagnitude>.01f?ahead.normalized:Vector3.forward;
                position=robot.position+ahead*3.3f+Vector3.Cross(Vector3.up,ahead)*1.1f+Vector3.up*2.2f;position.y=Mathf.Max(position.y,1.6f);
                rotation=Quaternion.LookRotation(robot.position+Vector3.up*1.2f-position);rate=2.5f;
            }
            float k=1-Mathf.Exp(-Time.unscaledDeltaTime*rate);
            cam.position=Vector3.Lerp(cam.position,position,k);cam.rotation=Quaternion.Slerp(cam.rotation,rotation,k);
        }
        internal void CameraControlsGUI()
        {
            if(GUILayout.Button("カメラ: "+(View==CameraView.Overview?"全体":View==CameraView.Dining?"近景":"追従 "+(_robots.Count>0?_robots[Mathf.Clamp(followIndex,0,_robots.Count-1)].RobotId:""))+" (V で切替)")) CycleCamera();
        }
        public void SetTrafficStyle(GuardianTrafficStyle style)
        {
            Settings.Traffic=style; Log("Traffic style "+style+" (scenario reset)");
            if(HumanExperienceActive) humanExperience.Restart(); else ResetScenario();
        }
    }
}
