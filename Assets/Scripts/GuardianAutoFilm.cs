using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Where the film's frames and sound go. The editor supplies an MP4 encoder; without one, PNG frames and a WAV.</summary>
    public interface IGuardianFilmSink
    {
        /// <summary>Opens the output (sampleRate 0: no sound) and returns the file or folder written.</summary>
        string Begin(string pathWithoutExtension, int width, int height, int fps, int sampleRate);
        void Frame(Texture2D frame);
        /// <summary>Interleaved stereo samples for one frame.</summary>
        void Sound(NativeArray<float> stereo);
        void End();
    }

    /// <summary>Which film: the 30-second main film for the pitch, or the 40-second presentation-mode film.</summary>
    public enum GuardianFilmStory { Main, Pitch }

    /// <summary>
    /// A film made with nobody at the keyboard. The restaurant runs for a while unrecorded (at triple speed)
    /// so guests are seated and robots busy; then a story plays on its own timeline:
    /// - Main (30 s, the team's agreed demo): robot B's vibration rises, its soup goes to another robot while B
    ///   keeps carrying food, then only dishes; nothing stops. At double speed, with a subtitle line, the
    ///   "今できる仕事" table and an end card (GuardianAutoFilm.Main.cs).
    /// - Pitch (40 s): the presentation mode, with an on-screen pointer making the person's clicks through
    ///   the same card and repair-crew calls as a real click (GuardianAutoFilm.Pitch.cs).
    /// The scripted parts (the vibration values, the orders, the clicks) are staged; the decisions the film shows
    /// (what each robot may carry, who takes over) are the simulator's own. Frames are taken at a fixed 30 fps
    /// from the Game view, with the game's own sound (AudioRenderer), so a slow PC makes a slower recording,
    /// not a jerky film. Started from the editor menu "Robomori/…動画を撮る" or scripts/film-demo.cmd
    /// (or -robomoriFilm on a built player).
    /// </summary>
    public sealed partial class GuardianAutoFilm : MonoBehaviour
    {
        public const int Fps = 30;
        public float WarmupSeconds = 30, WarmupSpeed = 3;
        public string Target = "B";
        public GuardianFilmStory Story { get; private set; }
        public float Seconds => Story == GuardianFilmStory.Main ? 30 : 40;
        /// <summary>How much faster than real time the film runs (the main film is fast-forwarded).</summary>
        public float Speed => Story == GuardianFilmStory.Main ? 2 : 1;

        /// <summary>Set by the editor before Play; read once by the simulator.</summary>
        public static bool Requested;
        public static GuardianFilmStory RequestedStory = GuardianFilmStory.Main;
        public static IGuardianFilmSink Sink;
        /// <summary>The film is written (the file or folder).</summary>
        public static event Action<string> Finished;
        public static bool Wanted => Requested || (!Application.isEditor && Array.IndexOf(Environment.GetCommandLineArgs(), "-robomoriFilm") >= 0);
        /// <summary>"-robomoriFilmStory Pitch" picks the presentation-mode film; anything else, the main film.</summary>
        public static GuardianFilmStory StoryFromArguments()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-robomoriFilmStory");
            return i >= 0 && i + 1 < args.Length && args[i + 1].Equals("Pitch", StringComparison.OrdinalIgnoreCase) ? GuardianFilmStory.Pitch : GuardianFilmStory.Main;
        }
        /// <summary>A film is being made in this run.</summary>
        public static bool Busy { get; private set; }

        /// <summary>
        /// One line for scripts/film-demo.ps1 to follow: "warming 7", "recording 12/40", "done &lt;file&gt;" or
        /// "error &lt;reason&gt;", in Recordings/film-status.txt.
        /// </summary>
        public static void Status(string line)
        {
            try
            {
                string folder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Recordings");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "film-status.txt"), line);
            }
            catch (Exception e) { Debug.LogWarning("[Robomori] Film status not written: " + e.Message); }
        }

        public string Output { get; private set; }

        private GuardianSimulator sim;
        private int frames;
        private bool warming, recording;
        private float warmEnds;
        // The evidence clock: real time, then frame time while recording, never jumping (see GuardianEvidenceInput.ClockOverride).
        private double clockOffset, clockAtStart;
        private Texture2D frame, arrow, ring, dot;
        private GUIStyle noticeStyle;
        private Font font;

        public void Initialize(GuardianSimulator simulator)
        {
            sim = simulator;
            Story = RequestedStory;
            Requested = false; RequestedStory = GuardianFilmStory.Main; Busy = true;
            GuardianEvidenceInput.ClockOverride = Clock;
            if (Story == GuardianFilmStory.Main) InitializeMain(); else InitializePitch();
            StartCoroutine(Run());
        }

        private double Clock() => recording ? clockAtStart + frames / (double)Fps : Time.realtimeSinceStartupAsDouble - clockOffset;
        /// <summary>Seconds into the film.</summary>
        private float FilmTime => frames / (float)Fps;

        private GuardianRobot Robot => sim.Robots.FirstOrDefault(r => r != null && r.RobotId == Target);
        private GuardianRobot RobotById(string id) => sim.Robots.FirstOrDefault(r => r != null && r.RobotId == id);

        private void Update()
        {
            if (!recording) return;
            if (Story == GuardianFilmStory.Main) MainStep(); else RunCues();
        }

        // ---- recording ----

        private IEnumerator Run()
        {
            yield return null;  // after every component has started
            if (sim.PitchMode == null) { Debug.LogError("[Robomori] Film needs the presentation mode"); Status("error presentation mode unavailable"); Busy = false; yield break; }
            sim.Settings.RandomFaults = false;
            sim.DemoInterface?.Close();
            if (Story == GuardianFilmStory.Main) PrepareMain();
            warming = true; warmEnds = Time.realtimeSinceStartup + WarmupSeconds / WarmupSpeed;
            Time.timeScale = WarmupSpeed;
            int shown = -1;
            while (Time.realtimeSinceStartup < warmEnds)
            {
                int left = Mathf.CeilToInt(warmEnds - Time.realtimeSinceStartup);
                if (left != shown) { shown = left; Status("warming " + left); }
                yield return null;
            }
            Time.timeScale = 1;
            if (Story == GuardianFilmStory.Main)
            {
                // Unrecorded: B takes a soup order, so the film opens with soup on its way to B.
                float until = Time.realtimeSinceStartup + 15;
                Time.timeScale = WarmupSpeed;
                while (!MainReady() && Time.realtimeSinceStartup < until) yield return null;
                Time.timeScale = 1;
            }
            warming = false;

            int width = Screen.width & ~1, height = Screen.height & ~1;
            string folder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Recordings");
            Directory.CreateDirectory(folder);
            string name = Path.Combine(folder, "robomori_demo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Time.captureFramerate = Fps;
            bool sound = AudioRenderer.Start();
            int channels = Channels(AudioSettings.speakerMode), rate = sound ? AudioSettings.outputSampleRate : 0;
            var sink = Sink ?? new FileSink();
            try { Output = sink.Begin(name, width, height, Fps, rate); }
            catch (Exception e)
            {
                Debug.LogException(e);
                sink = new FileSink(); Output = sink.Begin(name, width, height, Fps, rate);
            }
            Debug.Log("[Robomori] Filming " + width + "x" + height + " to " + Output + (sound ? " with sound" : " without sound"));
            frame = new Texture2D(width, height, TextureFormat.RGBA32, false);
            clockAtStart = Clock(); frames = 0; recording = true;
            Time.timeScale = Speed;
            if (Story == GuardianFilmStory.Main) MainStep(); else RunCues();
            int total = Mathf.RoundToInt(Seconds * Fps);
            try
            {
                while (frames < total)
                {
                    yield return new WaitForEndOfFrame();
                    frame.ReadPixels(new Rect(0, 0, Mathf.Min(width, Screen.width), Mathf.Min(height, Screen.height)), 0, 0, false);
                    frame.Apply(false);
                    sink.Frame(frame);
                    if (frames % Fps == 0) Status("recording " + frames / Fps + "/" + Mathf.RoundToInt(Seconds));
                    if (sound)
                    {
                        int n = AudioRenderer.GetSampleCountForCaptureFrame();
                        if (n > 0)
                        {
                            var raw = new NativeArray<float>(n * channels, Allocator.Temp);
                            var stereo = new NativeArray<float>(n * 2, Allocator.Temp);
                            AudioRenderer.Render(raw);
                            Downmix(raw, channels, stereo, n);
                            sink.Sound(stereo);
                            raw.Dispose(); stereo.Dispose();
                        }
                    }
                    frames++;
                }
            }
            finally
            {
                double now = Clock();
                recording = false;
                Time.timeScale = 1;
                clockOffset = Time.realtimeSinceStartupAsDouble - now;  // the clock carries on from where the film left it
                if (sound) AudioRenderer.Stop();
                Time.captureFramerate = 0;
                try { sink.End(); } catch (Exception e) { Debug.LogException(e); }
                if (Story == GuardianFilmStory.Pitch) sim.PitchMode.End(); else FinishMain();
                Destroy(frame);
            }
            Debug.Log("[Robomori] Film written: " + Output);
            Status("done " + Output);
            Busy = false;
            Finished?.Invoke(Output);
        }

        private static int Channels(AudioSpeakerMode mode) =>
            mode == AudioSpeakerMode.Mono ? 1 : mode == AudioSpeakerMode.Quad ? 4 : mode == AudioSpeakerMode.Surround ? 5
            : mode == AudioSpeakerMode.Mode5point1 ? 6 : mode == AudioSpeakerMode.Mode7point1 ? 8 : 2;

        /// <summary>Folds whatever the speakers are set to (mono to 7.1) into stereo: centre and rears at -3 dB.</summary>
        private static void Downmix(NativeArray<float> src, int channels, NativeArray<float> dst, int n)
        {
            const float half = .7071f;
            for (int i = 0; i < n; i++)
            {
                int b = i * channels;
                float l = src[b], r = channels > 1 ? src[b + 1] : l;
                if (channels == 4) { l += half * src[b + 2]; r += half * src[b + 3]; }
                else if (channels == 5) { l += half * (src[b + 2] + src[b + 3]); r += half * (src[b + 2] + src[b + 4]); }
                else if (channels >= 6)
                {
                    l += half * (src[b + 2] + src[b + 4]); r += half * (src[b + 2] + src[b + 5]);
                    if (channels >= 8) { l += half * src[b + 6]; r += half * src[b + 7]; }
                }
                dst[i * 2] = Mathf.Clamp(l, -1, 1); dst[i * 2 + 1] = Mathf.Clamp(r, -1, 1);
            }
        }

        // ---- on screen: a notice while warming up (not filmed); while filming, what the story draws ----

        private void OnGUI()
        {
            if (!warming && !recording) return;
            GUI.depth = -1000;
            if (arrow == null) MakeTextures();
            if (warming)
            {
                int left = Mathf.CeilToInt(Mathf.Max(0, warmEnds - Time.realtimeSinceStartup));
                var box = new Rect(Screen.width / 2f - 230, Screen.height / 2f - 40, 460, 80);
                GUI.color = new Color(0, 0, 0, .8f); GUI.DrawTexture(box, Texture2D.whiteTexture); GUI.color = Color.white;
                GUI.Label(box, "動画の準備中です（お店を開けています）\n" + (left > 0 ? "あと " + left + " 秒で、" : "まもなく ") + Mathf.RoundToInt(Seconds) + " 秒の録画を始めます", noticeStyle);
                return;
            }
            if (Story == GuardianFilmStory.Main) DrawMain(); else DrawPointer();
        }

        private void MakeTextures()
        {
            font = GuardianFonts.Create(20);
            noticeStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            noticeStyle.normal.textColor = Color.white;
            // A mouse arrow: white with a dark edge.
            var shape = new[] { new Vector2(0, 0), new Vector2(0, 17), new Vector2(4.2f, 13.2f), new Vector2(7, 19.5f), new Vector2(9.6f, 18.4f), new Vector2(6.8f, 12.2f), new Vector2(12, 12) };
            const int aw = 28, ah = 42; const float k = 2;
            arrow = new Texture2D(aw, ah, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < ah; y++)
                for (int x = 0; x < aw; x++)
                {
                    Vector2 p = new Vector2((x + .5f) / k - .5f, (y + .5f) / k - .5f);
                    bool inside = Inside(shape, p), edge = !inside && (Inside(shape, p + new Vector2(.6f, 0)) || Inside(shape, p - new Vector2(.6f, 0)) || Inside(shape, p + new Vector2(0, .6f)) || Inside(shape, p - new Vector2(0, .6f)));
                    arrow.SetPixel(x, ah - 1 - y, inside ? Color.white : edge ? new Color(.05f, .05f, .08f, 1) : Color.clear);
                }
            arrow.Apply();
            const int rs = 96;
            ring = new Texture2D(rs, rs, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            for (int y = 0; y < rs; y++)
                for (int x = 0; x < rs; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(rs / 2f, rs / 2f)) / (rs / 2f);
                    ring.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(1 - Mathf.Abs(d - .82f) / .1f)));
                }
            ring.Apply();
            dot = new Texture2D(rs, rs, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            for (int y = 0; y < rs; y++)
                for (int x = 0; x < rs; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(rs / 2f, rs / 2f)) / (rs / 2f);
                    dot.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((1 - d) / .08f)));
                }
            dot.Apply();
        }

        private static bool Inside(Vector2[] poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        private void OnDestroy()
        {
            if (Busy) { Busy = false; Status("error stopped before the film was finished"); }
            if (GuardianEvidenceInput.ClockOverride == (Func<double>)Clock) GuardianEvidenceInput.ClockOverride = null;
            if (recording) { AudioRenderer.Stop(); Time.captureFramerate = 0; }
            if (Time.timeScale != 1 && (warming || recording)) Time.timeScale = 1;
            foreach (var t in new[] { arrow, ring, dot, frame }) if (t != null) Destroy(t);
            GuardianFonts.Release(font);
            if (Story == GuardianFilmStory.Main) DestroyMain();
        }

        /// <summary>Without the editor's encoder (a built player): numbered PNG frames and a WAV beside them.</summary>
        private sealed class FileSink : IGuardianFilmSink
        {
            private string folder;
            private int index, rate;
            private FileStream wav;
            private long samples;

            public string Begin(string path, int width, int height, int fps, int sampleRate)
            {
                folder = path; rate = sampleRate; Directory.CreateDirectory(folder);
                if (rate > 0) { wav = File.Create(Path.Combine(folder, "sound.wav")); wav.Write(new byte[44], 0, 44); }
                File.WriteAllText(Path.Combine(folder, "make-mp4.txt"),
                    "ffmpeg -framerate " + fps + " -i frame_%05d.png" + (rate > 0 ? " -i sound.wav -c:a aac" : "") + " -c:v libx264 -pix_fmt yuv420p robomori_demo.mp4\n");
                return folder;
            }

            public void Frame(Texture2D f) => File.WriteAllBytes(Path.Combine(folder, "frame_" + (index++).ToString("D5") + ".png"), f.EncodeToPNG());

            public void Sound(NativeArray<float> stereo)
            {
                if (wav == null) return;
                var bytes = new byte[stereo.Length * 2];
                for (int i = 0; i < stereo.Length; i++) { short v = (short)Mathf.RoundToInt(Mathf.Clamp(stereo[i], -1, 1) * 32767); bytes[i * 2] = (byte)v; bytes[i * 2 + 1] = (byte)(v >> 8); }
                wav.Write(bytes, 0, bytes.Length); samples += stereo.Length / 2;
            }

            public void End()
            {
                if (wav == null) return;
                long data = samples * 4;
                var w = new BinaryWriter(wav);
                wav.Position = 0;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write((int)(36 + data)); w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write((int)data);
                w.Flush(); wav.Dispose(); wav = null;
            }
        }
    }
}
