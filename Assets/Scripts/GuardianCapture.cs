using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// F9: record the live Unity Game view at 30fps as 1200 PNG frames.
    /// The resulting images can be encoded to MP4 with ffmpeg.
    /// All rendering is from real Unity GameObjects; nothing is pre-rendered.
    /// </summary>
    public sealed class GuardianCapture : MonoBehaviour
    {
        public int Fps = 30;
        public int DurationSeconds = 40;
        public bool Capturing { get; private set; }
        public string OutputDirectory { get; private set; }
        private Coroutine _routine;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (Capturing) StopCapture();
                else StartCapture();
            }
        }

        public void StartCapture()
        {
            if (Capturing) return;
            GuardianSimulator simulator = GetComponent<GuardianSimulator>();
            if (simulator == null)
            {
                Debug.LogError("[Guardian] Capture needs GuardianSimulator on the same object.");
                return;
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            OutputDirectory = Path.Combine(projectRoot, "Recordings", "guardian_" + stamp);
            Directory.CreateDirectory(OutputDirectory);
            simulator.ResetScenario();
            Capturing = true;
            Time.captureFramerate = Mathf.Clamp(Fps, 1, 60);
            _routine = StartCoroutine(CaptureFrames());
            Debug.Log("[Guardian] Recording frames to " + OutputDirectory);
        }

        private IEnumerator CaptureFrames()
        {
            int count = Mathf.Clamp(Fps, 1, 60) * Mathf.Clamp(DurationSeconds, 1, 600);
            // ScreenCapture.CaptureScreenshot saves at the end of the current frame.
            for (int i = 0; i < count && Capturing; ++i)
            {
                yield return new WaitForEndOfFrame();
                string output = Path.Combine(OutputDirectory, "frame_" + i.ToString("D5") + ".png");
                ScreenCapture.CaptureScreenshot(output);
            }
            StopCapture();
        }

        public void StopCapture()
        {
            if (!Capturing) return;
            Capturing = false;
            Time.captureFramerate = 0;
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
            Debug.Log("[Guardian] Capture finished: " + OutputDirectory +
                "  (ffmpeg -framerate 30 -i frame_%05d.png -c:v libx264 -pix_fmt yuv420p guardian_demo.mp4)");
        }

        private void OnDisable() { StopCapture(); }
    }
}
