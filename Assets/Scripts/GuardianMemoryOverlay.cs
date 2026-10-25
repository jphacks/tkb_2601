using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace Guardian.UnitySim
{
    /// <summary>
    /// Names what the main thread is doing, so a freeze can be traced to it (GuardianMemoryOverlay records
    /// stalls with the section they happened in). Pass constant strings only: no allocation per call.
    /// </summary>
    public static class GuardianFrameMarker
    {
        private static string section = "start";
        private static long since;
        public static void Enter(string name) { Volatile.Write(ref section, name); Interlocked.Exchange(ref since, Stopwatch.GetTimestamp()); }
        internal static string Section => Volatile.Read(ref section);
        internal static double SecondsIn => (Stopwatch.GetTimestamp() - Interlocked.Read(ref since)) / (double)Stopwatch.Frequency;
    }

    /// <summary>
    /// Top-left memory and frame readout for checking the demo PC: this process's memory and how fast it
    /// grows, Unity's own heaps, frame times (p50/p95/p99 and the worst of the last 10 s), physics steps
    /// per frame, and the PC's free memory. Scales with the screen height so it stays readable
    /// at QHD and above. Hidden at first so the demo screen stays clean: F10 shows or hides it, and
    /// -robomoriMemory starts it shown. Samples once a second into fixed buffers, and the verdict uses the
    /// same thresholds as scripts/measure-memory.ps1. Attaches itself after the first scene loads, so
    /// copying this one file into any Unity project is enough.
    /// Freezes: a watch thread notices when the main thread stops for half a second or more and writes
    /// what it was doing (GuardianFrameMarker) to memory-logs/freezes.txt (in a build: the persistent data
    /// folder), even if it never comes back. While the box is shown, frame and memory figures go to
    /// memory-logs/frames-*.csv every 5 seconds, for before/after comparisons.
    /// </summary>
    public sealed class GuardianMemoryOverlay : MonoBehaviour
    {
        public KeyCode ToggleKey = KeyCode.F10;
        public bool Visible;
        public float WarmupMinutes = 3;

        internal const int Capacity = 1800;  // 30 minutes at one sample a second
        internal const float SlopeOk = .5f, SlopeWarn = 2f, ConfirmMinutes = 10;
        private const float FreeOk = 2048, FreeWarn = 1024, Mb = 1024f * 1024f;
        private const int Bars = 120;
        private static readonly Color Good = new Color(.45f, .9f, .5f), Watch = new Color(1f, .78f, .25f), Bad = new Color(1f, .42f, .36f),
            Dim = new Color(1f, 1f, 1f, .65f);

        /// <summary>Bottom edge of the box in screen pixels (0 when hidden), so other top-left overlays can sit below it.</summary>
        internal static float ScreenBottom { get; private set; }

        private readonly float[] minutes = new float[Capacity], privateMb = new float[Capacity];
        private int count, next;
        private float startedAt, nextSampleAt, frameWindowStart, firstPrivate = -1, peakPrivate, lowestFree = float.MaxValue;
        private int frames;
        private string header;
        private readonly string[] lines = new string[3];
        private string graphLabel = "";
        private float graphMin, graphMax;
        private Color headerColor = Color.white, freeColor = Color.white;
        private Texture2D pixel;
        private GUIStyle headerStyle, style;

        // Frame times of the last 600 frames (10 s at 60 fps), sorted once a second for the percentiles.
        private const int FrameWindow = 600;
        private readonly float[] frameMs = new float[FrameWindow], sortedMs = new float[FrameWindow];
        private int frameCount, frameNext, stepsThisFrame, stepsMax;
        private float p50, p95, p99, worst;
        private string frameLine = "", stallLine = "";
        private Color frameColor = Color.white;

        // Freeze watch.
        private Thread watcher;
        private volatile bool watching, paused;
        private long heartbeat;
        private int stallOpen, gcAtFrame;
        private string stallSection, folder, framesPath;
        private double stallSectionSeconds, worstStall;
        private readonly object fileGate = new object();
        private float nextCsvAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach()
        {
            if (FindAnyObjectByType<GuardianMemoryOverlay>() != null) return;
            var host = new GameObject("Robomori memory overlay");
            DontDestroyOnLoad(host);
            host.AddComponent<GuardianMemoryOverlay>();
        }

        private void Start()
        {
            startedAt = frameWindowStart = nextSampleAt = Time.unscaledTime;
            Visible = Array.IndexOf(Environment.GetCommandLineArgs(), "-robomoriMemory") >= 0;
            folder = Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "memory-logs")) : Application.persistentDataPath;
            Interlocked.Exchange(ref heartbeat, Stopwatch.GetTimestamp());
            gcAtFrame = GC.CollectionCount(0);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.pauseStateChanged += OnPause;
#endif
            if (Application.isBatchMode) return;  // automated runs time themselves
            watching = true;
            watcher = new Thread(WatchMainThread) { IsBackground = true, Name = "Robomori freeze watch" };
            watcher.Start();
        }

#if UNITY_EDITOR
        private void OnPause(UnityEditor.PauseState state) { paused = state == UnityEditor.PauseState.Paused; }
#endif

        private void FixedUpdate() { stepsThisFrame++; }

        private void Update()
        {
            long now = Stopwatch.GetTimestamp();
            double gap = (now - Interlocked.Exchange(ref heartbeat, now)) / (double)Stopwatch.Frequency;
            if (watching && (Interlocked.Exchange(ref stallOpen, 0) == 1 || gap >= .5)) RecordStall(gap);
            gcAtFrame = GC.CollectionCount(0);
            if (Input.GetKeyDown(ToggleKey)) Visible = !Visible;
            frames++;
            frameMs[frameNext] = Time.unscaledDeltaTime * 1000f;
            frameNext = (frameNext + 1) % FrameWindow;
            if (frameCount < FrameWindow) frameCount++;
            stepsMax = Mathf.Max(stepsMax, stepsThisFrame);
            stepsThisFrame = 0;
            if (Time.unscaledTime >= nextSampleAt) Sample(Time.unscaledTime);
        }

        // ---- freezes ----

        private void WatchMainThread()
        {
            double reportAt = 0;
            while (watching)
            {
                Thread.Sleep(100);
                if (paused) { Interlocked.Exchange(ref heartbeat, Stopwatch.GetTimestamp()); continue; }
                double stalled = (Stopwatch.GetTimestamp() - Interlocked.Read(ref heartbeat)) / (double)Stopwatch.Frequency;
                if (stalled < .5) { reportAt = 0; continue; }
                if (Interlocked.CompareExchange(ref stallOpen, 1, 0) == 0)
                {
                    // Snapshot what the main thread is doing while it is still stuck there.
                    stallSection = GuardianFrameMarker.Section; stallSectionSeconds = GuardianFrameMarker.SecondsIn;
                    reportAt = 2;
                }
                if (reportAt > 0 && stalled >= reportAt)
                {
                    // Still stuck: write it now, in case the main thread never returns.
                    Append("freezes.txt", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} STILL FROZEN {stalled:0.0} s | in: {stallSection} (entered {stallSectionSeconds:0.00} s before the stall was noticed)");
                    reportAt = reportAt < 5 ? 5 : reportAt * 2;
                }
            }
        }

        private void RecordStall(double seconds)
        {
            string section = stallSection ?? GuardianFrameMarker.Section;
            double inSection = stallSection != null ? stallSectionSeconds : GuardianFrameMarker.SecondsIn;
            stallSection = null;
            if (seconds < .5) return;
            int gcs = GC.CollectionCount(0) - gcAtFrame;
            string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} stall {seconds:0.00} s | in: {section} ({inSection:0.00} s there) | physics steps that frame: {stepsThisFrame} | GC runs: {gcs} | C# heap {Profiler.GetMonoUsedSizeLong() / Mb:N0} MB" +
                (Application.isFocused ? "" : " | window not focused");
            Append("freezes.txt", text);
            Debug.LogWarning("[Robomori frames] " + text);
            if (seconds >= worstStall)
            {
                worstStall = seconds;
                stallLine = $"FREEZE {seconds:0.0} s at {DateTime.Now:HH:mm:ss}: {section}" + (gcs > 0 ? $" (GC x{gcs})" : "");
            }
        }

        private void Append(string file, string line)
        {
            try { lock (fileGate) { Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, file), line + Environment.NewLine); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void FrameStats()
        {
            int n = frameCount;
            if (n == 0) return;
            for (int i = 0; i < n; i++) sortedMs[i] = frameMs[i];
            Array.Sort(sortedMs, 0, n);
            p50 = sortedMs[n / 2]; p95 = sortedMs[Mathf.Min(n - 1, (int)(n * .95f))]; p99 = sortedMs[Mathf.Min(n - 1, (int)(n * .99f))]; worst = sortedMs[n - 1];
            frameColor = p99 <= 20 ? Good : p99 <= 34 ? Watch : Bad;
            frameLine = $"FRAME p50 {p50:0.0}  p95 {p95:0.0}  p99 {p99:0.0}  worst {worst:0} ms   physics steps/frame up to {stepsMax}";
        }

        private void Sample(float now)
        {
            nextSampleAt = now + 1;
            float fps = frames / Mathf.Max(.001f, now - frameWindowStart);
            frames = 0; frameWindowStart = now;
            float minute = (now - startedAt) / 60f;

            bool processKnown = ProcessMemory(out float workingMb, out float privMb);
            if (!processKnown) privMb = Profiler.GetTotalReservedMemoryLong() / Mb;
            float managed = Profiler.GetMonoUsedSizeLong() / Mb, native = Profiler.GetTotalAllocatedMemoryLong() / Mb;
            float free = FreeMemoryMb();
            Add(minute, privMb);
            if (firstPrivate < 0) firstPrivate = privMb;
            peakPrivate = Mathf.Max(peakPrivate, privMb);

            float slope = Trend(out float span);
            if (minute < WarmupMinutes || float.IsNaN(slope))
            {
                header = $"MEMORY  measuring... {minute:0.0} / {WarmupMinutes:0} min";
                headerColor = Color.white;
            }
            else
            {
                int verdict = Verdict(slope);
                headerColor = verdict == 0 ? Good : verdict == 1 ? Watch : Bad;
                header = $"MEMORY {(verdict == 0 ? "OK" : verdict == 1 ? "WATCH" : "LEAK?")}  {slope:+0.00;-0.00} MB/min" +
                    (span < ConfirmMinutes ? $"  (judging {span:0}/10 min)" : $"  (2 h: {slope * 120:+#,0;-#,0;0} MB)");
            }
            lines[0] = (processKnown ? "Unity " : "Unity reserved ") + $"{privMb:N0} MB   start {firstPrivate:N0}   peak {peakPrivate:N0}" +
                (processKnown ? $"   in RAM {workingMb:N0}" : "");
            if (free < 0) { lines[1] = $"PC free memory: unknown   FPS {fps:0}"; freeColor = Color.white; }
            else
            {
                lowestFree = Mathf.Min(lowestFree, free);
                freeColor = lowestFree >= FreeOk ? Color.white : lowestFree >= FreeWarn ? Watch : Bad;
                lines[1] = $"PC free {free:N0} MB   lowest {lowestFree:N0}" + (lowestFree >= FreeOk ? "" : lowestFree >= FreeWarn ? " (under 2 GB)" : " (under 1 GB)") +
                    $"   FPS {fps:0}";
            }
            lines[2] = $"C# heap {managed:N0} MB   Unity native {native:N0} MB   {ToggleKey} hides";
            graphMin = float.MaxValue; graphMax = float.MinValue;
            for (int i = 0; i < count; i++) { float v = At(i); graphMin = Mathf.Min(graphMin, v); graphMax = Mathf.Max(graphMax, v); }
            graphLabel = $"{graphMin:N0}-{graphMax:N0} MB, last {minute - minutes[Index(0)]:0} min";
            FrameStats();
            if (Visible && now >= nextCsvAt)
            {
                nextCsvAt = now + 5;
                if (framesPath == null)
                {
                    framesPath = $"frames-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                    Append(framesPath, "time,minute,fps,frame_p50_ms,frame_p95_ms,frame_p99_ms,frame_worst_ms,physics_steps_max,private_mb,csharp_heap_mb,unity_native_mb,pc_free_mb,gc_runs");
                }
                Append(framesPath, $"{DateTime.Now:HH:mm:ss},{minute:0.00},{fps:0},{p50:0.0},{p95:0.0},{p99:0.0},{worst:0.0},{stepsMax},{privMb:0},{managed:0},{native:0},{free:0},{GC.CollectionCount(0)}");
            }
            stepsMax = 0;
        }

        internal void Add(float minute, float value)
        {
            minutes[next] = minute; privateMb[next] = value;
            next = (next + 1) % Capacity;
            if (count < Capacity) count++;
        }

        private int Index(int i) => (next - count + i + Capacity) % Capacity;  // 0 = oldest kept sample
        private float At(int i) => privateMb[Index(i)];

        /// <summary>Least-squares growth in MB per minute over the kept samples after the warm-up; NaN if too few.</summary>
        internal float Trend(out float span)
        {
            double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
            float first = float.NaN, last = float.NaN;
            for (int i = 0; i < count; i++)
            {
                float x = minutes[Index(i)];
                if (x < WarmupMinutes) continue;
                double y = privateMb[Index(i)];
                if (float.IsNaN(first)) first = x;
                last = x; n++; sx += x; sy += y; sxx += x * (double)x; sxy += x * y;
            }
            span = n > 0 ? last - first : 0;
            double den = n * sxx - sx * sx;
            return n < 3 || den <= 0 ? float.NaN : (float)((n * sxy - sx * sy) / den);
        }

        internal static int Verdict(float slope) => slope <= SlopeOk ? 0 : slope <= SlopeWarn ? 1 : 2;

        private void OnGUI()
        {
            if (!Visible || header == null) { ScreenBottom = 0; return; }
            if (pixel == null) { pixel = new Texture2D(1, 1); pixel.SetPixel(0, 0, Color.white); pixel.Apply(); }
            if (style == null)
            {
                headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
                style = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            }
            // Lay out at 720p and scale up, so the box is the same share of the screen at any resolution.
            float scale = Mathf.Max(1f, Screen.height / 720f);
            Matrix4x4 savedMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            const float width = 470, headerHeight = 26, lineHeight = 19, graphHeight = 34;
            int extra = stallLine.Length > 0 ? 2 : 1;
            var box = new Rect(10, 10, width, 10 + headerHeight + (lines.Length + extra) * lineHeight + graphHeight + 20);
            Color saved = GUI.color;
            GUI.color = new Color(0, 0, 0, .8f);
            GUI.DrawTexture(box, pixel);
            GUI.color = saved;
            float y = box.y + 6;
            headerStyle.normal.textColor = headerColor;
            GUI.Label(new Rect(box.x + 10, y, width - 20, headerHeight), header, headerStyle);
            y += headerHeight;
            for (int i = 0; i < lines.Length; i++)
            {
                style.normal.textColor = i == 1 ? freeColor : i == 2 ? Dim : Color.white;
                GUI.Label(new Rect(box.x + 10, y, width - 20, lineHeight), lines[i], style);
                y += lineHeight;
            }
            style.normal.textColor = frameColor;
            GUI.Label(new Rect(box.x + 10, y, width - 20, lineHeight), frameLine, style);
            y += lineHeight;
            if (stallLine.Length > 0)
            {
                style.normal.textColor = Bad;
                GUI.Label(new Rect(box.x + 10, y, width - 20, lineHeight), stallLine, style);
                y += lineHeight;
            }
            var graph = new Rect(box.x + 10, y + 2, width - 20, graphHeight);
            DrawGraph(graph);
            style.normal.textColor = Dim;
            GUI.Label(new Rect(graph.x, graph.yMax, width - 20, 18), graphLabel, style);
            GUI.matrix = savedMatrix;
            ScreenBottom = box.yMax * scale;
        }

        private void DrawGraph(Rect area)
        {
            if (count == 0) return;
            Color saved = GUI.color;
            GUI.color = new Color(1, 1, 1, .08f);
            GUI.DrawTexture(area, pixel);
            int step = Mathf.Max(1, Mathf.CeilToInt(count / (float)Bars)), bars = Mathf.Max(1, count / step);
            float range = Mathf.Max(1, graphMax - graphMin), barWidth = area.width / Bars;
            GUI.color = new Color(headerColor.r, headerColor.g, headerColor.b, .85f);
            for (int b = 0; b < bars; b++)
            {
                float h = 2 + (At(Mathf.Min(count - 1, b * step + step - 1)) - graphMin) / range * (area.height - 2);
                GUI.DrawTexture(new Rect(area.x + b * barWidth, area.yMax - h, Mathf.Max(1, barWidth - 1), h), pixel);
            }
            GUI.color = saved;
        }

        private void OnDestroy()
        {
            watching = false;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.pauseStateChanged -= OnPause;
#endif
            if (count > 0) Debug.Log("[Robomori memory] " + header + " / " + lines[0] + " / " + lines[1] + " / " + frameLine);
            ScreenBottom = 0;
            if (pixel != null) Destroy(pixel);
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct Counters { public uint cb, faults; public UIntPtr peakWorking, working, peakPaged, paged, peakNonpaged, nonpaged, pagefile, peakPagefile, privateBytes; }
        [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr process, out Counters counters, uint size);
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus { public uint length, load; public ulong totalPhys, availPhys, totalPageFile, availPageFile, totalVirtual, availVirtual, availExtended; }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        private static bool ProcessMemory(out float workingMb, out float privateBytesMb)
        {
            // The same numbers PowerShell's Get-Process shows as WorkingSet64 and PrivateMemorySize64.
            bool ok = GetProcessMemoryInfo(new IntPtr(-1), out Counters c, (uint)Marshal.SizeOf(typeof(Counters)));
            workingMb = ok ? c.working.ToUInt64() / Mb : -1;
            privateBytesMb = ok ? c.privateBytes.ToUInt64() / Mb : -1;
            return ok;
        }

        private static float FreeMemoryMb()
        {
            var status = new MemoryStatus { length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            return GlobalMemoryStatusEx(ref status) ? status.availPhys / Mb : -1;
        }
#else
        private static bool ProcessMemory(out float workingMb, out float privateBytesMb) { workingMb = privateBytesMb = -1; return false; }
        private static float FreeMemoryMb() => -1;
#endif
    }
}
