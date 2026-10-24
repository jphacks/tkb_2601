using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Local build diagnostics, activated only with -guardianVerify. No policy changes.</summary>
    [DefaultExecutionOrder(100)]
    public sealed class RestaurantPlayerProbe : MonoBehaviour
    {
        private readonly List<string> report=new List<string>();
        private readonly Dictionary<string,float> minima=new Dictionary<string,float>();
        private readonly HashSet<string> jobs=new HashSet<string>();
        private GuardianSimulator simulator;
        private GuardianCapture capture;
        private string output;
        private bool active,failed,foodCompleted;
        private int shot;
        private readonly float[] times={1,19,40};

        private void Start()
        {
            string[] args=Environment.GetCommandLineArgs();
            if (!args.Contains("-guardianVerify")) { enabled=false; return; }
            output=Path.Combine(Directory.GetParent(Application.dataPath).FullName,"LocalVerification");
            Directory.CreateDirectory(output);
            simulator=GetComponent<GuardianSimulator>();
            Application.runInBackground=true;
            Application.logMessageReceived+=OnLog;
            active=true;
            report.Add("Unity="+Application.unityVersion+" graphics="+SystemInfo.graphicsDeviceName);
            report.Add("Main camera enabled="+Camera.main.enabled+" target="+Camera.main.targetTexture+" screen="+Screen.width+"x"+Screen.height);
            Check(simulator.GetComponentsInChildren<GuardianRobot>().All(r=>r.transform.Find("Robot Visual/Nekobot Model")!=null),"actual local model present on every robot");
            Check(Application.unityVersion=="6000.3.12f1","exact Unity version");
            RestaurantPerson[] people=simulator.GetComponentsInChildren<RestaurantPerson>();
            Check(people.Count(p=>!p.Standing)==4 && people.Count(p=>p.Standing)==1,"four seated adults and one standing staff member");
            Check(simulator.GetComponentsInChildren<Renderer>().All(r=>r.sharedMaterials.All(m=>m!=null && m.shader!=null && m.shader.isSupported)),"all player materials/shaders supported");
            capture=GetComponent<GuardianCapture>();
            if (args.Contains("-guardianCapture")) capture.StartCapture();
            else Time.captureFramerate=30;
            report.Add("Capture enabled="+(capture!=null && capture.Capturing));
        }
        private void OnLog(string message,string stack,LogType type)
        {
            if (!active) return;
            if (message.StartsWith("[Guardian]")) report.Add(message);
            if (message=="[Guardian] F-204 completed by Robot A") foodCompleted=true;
            if (type==LogType.Error || type==LogType.Exception || type==LogType.Assert)
            { failed=true; report.Add(type+": "+message+"\n"+stack); }
        }
        private void Check(bool condition,string name)
        {
            if (!condition) { failed=true; report.Add("FAIL: "+name); }
            else report.Add("PASS: "+name);
        }
        private void LateUpdate()
        {
            if (!active) return;
            try { RestaurantClearance.Measure(simulator,minima); }
            catch (Exception error) { failed=true; report.Add("FAIL: "+error.Message); Finish(); return; }
            foreach (GuardianRobot robot in simulator.GetComponentsInChildren<GuardianRobot>())
                if (robot.CurrentTask!=null) jobs.Add(robot.CurrentTask.Id+":"+robot.RobotId);
            if (shot<times.Length && simulator.DemoClock>=times[shot])
            {
                CaptureCamera(times[shot]);
                if (capture==null || !capture.Capturing)
                    ScreenCapture.CaptureScreenshot(Path.Combine(output,"player-"+times[shot].ToString("00")+"s-game.png"));
                shot++;
            }
            if (simulator.DemoClock<42 || (capture!=null && capture.Capturing)) return;
            Check(simulator.GetComponentsInChildren<GuardianRobot>().Length==3,"three actual robot actors");
            Check(foodCompleted,"F-204 completed by A");
            Check(jobs.Contains("S-101:A") && jobs.Contains("F-102:B") && jobs.Contains("R-103:C"),"initial task allocation");
            Check(simulator.GetComponentsInChildren<GuardianRobot>().Single(r=>r.RobotId=="B").Condition==RobotCondition.Limited,"B became LIMITED");
            if (capture!=null && !string.IsNullOrEmpty(capture.OutputDirectory))
            {
                var frames=Directory.GetFiles(capture.OutputDirectory,"frame_*.png");
                Check(frames.Length==1200 && frames.All(f=>new FileInfo(f).Length>0),"existing GuardianCapture wrote 1200 nonempty frames");
                foreach (int index in new[]{0,600,1199})
                {
                    string path=Path.Combine(capture.OutputDirectory,"frame_"+index.ToString("D5")+".png");
                    var image=new Texture2D(2,2,TextureFormat.RGB24,false);
                    try
                    {
                        bool decoded=image.LoadImage(File.ReadAllBytes(path));
                        Check(decoded && HasScenePixels(image),"GuardianCapture frame "+index+" contains scene pixels (not a black frame)");
                    }
                    finally { DestroyImmediate(image); }
                }
                report.Add("Capture directory="+capture.OutputDirectory);
            }
            Finish();
        }
        private void Finish()
        {
            active=false;
            foreach (var item in minima) report.Add("Minimum rendered envelope clearance "+item.Key+"="+item.Value.ToString("F4")+"m");
            report.Add(failed?"GUARDIAN_PLAYER_FAILED":"GUARDIAN_PLAYER_OK");
            File.WriteAllLines(Path.Combine(output,"player-verification.txt"),report);
            Time.captureFramerate=0;
            Application.Quit(failed?1:0);
        }

        private static bool HasScenePixels(Texture2D image)
        {
            Color32[] pixels=image.GetPixels32();
            int minimum=765,maximum=0,lit=0;
            for (int i=0;i<pixels.Length;i+=97)
            {
                int brightness=pixels[i].r+pixels[i].g+pixels[i].b;
                minimum=Mathf.Min(minimum,brightness); maximum=Mathf.Max(maximum,brightness);
                if (brightness>60) lit++;
            }
            return maximum-minimum>60 && lit>100;
        }
        private void CaptureCamera(float clock)
        {
            Camera camera=Camera.main;
            var target=new RenderTexture(1600,900,24) { antiAliasing=4 };
            var previous=camera.targetTexture; var activeTarget=RenderTexture.active;
            var image=new Texture2D(1600,900,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=target; camera.Render(); RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,1600,900),0,0); image.Apply();
                Check(HasScenePixels(image),"player camera "+clock+"s has nonblack rendered scene");
                File.WriteAllBytes(Path.Combine(output,"player-"+clock.ToString("00")+"s-camera.png"),image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture=previous; RenderTexture.active=activeTarget;
                DestroyImmediate(image); target.Release(); DestroyImmediate(target);
            }
        }
    }
}
