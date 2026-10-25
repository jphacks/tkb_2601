using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// Link to the phone gateway (phone/RobomoriPhone.cs, started with scripts/start-phone.cmd) on this PC.
    /// Connects to 127.0.0.1 and accepts no connections itself; idle when the gateway is not running.
    /// Sends each robot's state twice a second (so phones can raise an alarm when a robot stays stopped, and
    /// so the gateway can answer questions about a robot) and applies the only command a phone can give: stop
    /// one robot, or all of them. Releasing a stop stays with a person at the PC or the robot.
    /// Talking: Unity asks the gateway to listen on this PC's microphone ("listen"); the gateway returns what it
    /// heard ("heard"), and tells Unity which recorded phrases a robot should say to a phone's question ("say").
    /// </summary>
    public sealed class GuardianPhoneLink : MonoBehaviour
    {
#pragma warning disable 0649 // filled in by JsonUtility
        [Serializable] private sealed class Command { public string t, robot, by, via, heard, text, error, keys, q; public float conf; }
#pragma warning restore 0649

        public int Port = 47611;
        /// <summary>Raised on the main thread when a phone stops a robot: robot ("all" for every robot), phone name, "button" or "voice", heard words.</summary>
        public static event Action<string, string, string, string> RemoteStop;
        /// <summary>The gateway heard a question on the PC microphone: robot, words (empty if nothing), confidence 0..1, error.</summary>
        public static event Action<string, string, float, string> Heard;
        /// <summary>A phone asked a robot something: robot, recorded phrase keys to say, the question, phone name.</summary>
        public static event Action<string, string, string, string> PhoneAsked;
        public bool Connected { get { lock (gate) return writer != null; } }

        // Stops have their own queue and are applied first; everything else is capped. If stops ever pile up
        // (a misbehaving sender), every robot is stopped instead: the safe way to drop them.
        private readonly ConcurrentQueue<string> stops = new ConcurrentQueue<string>(), inbox = new ConcurrentQueue<string>();
        private int stopCount, otherCount, stopAll;
        private readonly StringBuilder line = new StringBuilder(512);
        private readonly object gate = new object();
        private GuardianSimulator sim;
        private Thread thread;
        private TcpClient connection;
        private StreamWriter writer;
        private volatile bool running;
        private float nextSend;

        private void Start()
        {
            sim = GetComponent<GuardianSimulator>();
            running = true;
            thread = new Thread(Receive) { IsBackground = true, Name = "Robomori phone link" };
            thread.Start();
        }

        private void Receive()
        {
            while (running)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        tcp.Connect(IPAddress.Loopback, Port);
                        var stream = tcp.GetStream();
                        var w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                        // The gateway trusts us only with the token it wrote to this user's data folder.
                        w.WriteLine("{\"t\":\"hello\",\"token\":\"" + Token() + "\"}");
                        lock (gate) { connection = tcp; writer = w; }
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string text;
                            while (running && (text = reader.ReadLine()) != null)
                            {
                                if (text.Length > 4096) continue;
                                if (text.StartsWith("{\"t\":\"stop\"", StringComparison.Ordinal))
                                {
                                    if (Interlocked.Increment(ref stopCount) <= 32) stops.Enqueue(text);
                                    else { Interlocked.Decrement(ref stopCount); Interlocked.Exchange(ref stopAll, 1); }
                                }
                                else if (Interlocked.Increment(ref otherCount) <= 32) inbox.Enqueue(text);
                                else Interlocked.Decrement(ref otherCount);
                            }
                        }
                    }
                }
                catch (SocketException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                finally { lock (gate) { connection = null; writer = null; } }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(100);  // the gateway may start later
            }
        }

        private static string Token()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Robomori", "unity-link.token");
                string token = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
                foreach (char c in token) if (!Uri.IsHexDigit(c)) return "";
                return token;
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private void Update()
        {
            if (sim == null) return;
            GuardianFrameMarker.Enter("phone link");
            if (Interlocked.Exchange(ref stopAll, 0) == 1) Apply("{\"t\":\"stop\",\"robot\":\"all\",\"by\":\"(too many stop messages)\",\"via\":\"link\"}");
            while (stops.TryDequeue(out string stop)) { Interlocked.Decrement(ref stopCount); Apply(stop); }
            for (int i = 0; i < 8 && inbox.TryDequeue(out string json); i++) { Interlocked.Decrement(ref otherCount); Apply(json); }
            if (Time.unscaledTime >= nextSend) { nextSend = Time.unscaledTime + .5f; SendState(); }
        }

        private void Apply(string json)
        {
            Command command;
            try { command = JsonUtility.FromJson<Command>(json); }
            catch (ArgumentException) { return; }
            if (command == null || string.IsNullOrEmpty(command.robot)) return;
            if (command.t == "heard") { Heard?.Invoke(command.robot, command.text ?? "", command.conf, command.error); return; }
            if (command.t == "say") { PhoneAsked?.Invoke(command.robot, command.keys ?? "", command.q ?? "", command.by ?? ""); return; }
            if (command.t != "stop") return;
            bool any = false;
            foreach (var robot in sim.Robots)
            {
                if (command.robot != "all" && robot.RobotId != command.robot) continue;
                any = true;
                if (!robot.EmergencyStopped) sim.SetEmergencyStop(robot.RobotId, true);
            }
            if (!any) return;
            sim.Log("Phone stop " + command.robot + " by " + command.by + " (" + command.via + (string.IsNullOrEmpty(command.heard) ? "" : " '" + command.heard + "'") + ")");
            RemoteStop?.Invoke(command.robot, command.by, command.via, command.heard);
        }

        /// <summary>Asks the gateway to listen on this PC's microphone for a question to one robot. False when the gateway is not running.</summary>
        public bool RequestListen(string robot) => Send("{\"t\":\"listen\",\"robot\":\"" + robot + "\"}");

        private bool Send(string text)
        {
            lock (gate)
            {
                if (writer == null) return false;
                try { writer.WriteLine(text); return true; }
                catch (IOException) { return false; }
                catch (ObjectDisposedException) { return false; }
            }
        }

        private void SendState()
        {
            lock (gate) if (writer == null) return;
            var talk = sim.RobotTalk;
            line.Clear().Append("{\"t\":\"state\",\"robots\":[");
            bool first = true;
            foreach (var r in sim.Robots)
            {
                if (r == null) continue;
                var f = talk != null ? talk.Facts(r) : null;
                bool stopped = f != null ? f.Stopped : r.EmergencyStopped || (r.PhysicsDriver != null && r.PhysicsDriver.SafetyHeld);
                if (!first) line.Append(',');
                first = false;
                line.Append("{\"id\":\"").Append(r.RobotId).Append("\",\"state\":\"").Append(stopped ? "Stopped" : r.Condition.ToString())
                    .Append("\",\"stopped\":").Append(stopped ? "true" : "false").Append(",\"task\":\"").Append(Task(r.CurrentTask)).Append('"');
                if (f != null)
                    line.Append(",\"cond\":\"").Append(f.Condition).Append("\",\"cause\":\"").Append(f.Cause).Append("\",\"since\":").Append(Mathf.RoundToInt((float)f.StoppedFor))
                        .Append(",\"kind\":\"").Append(f.Kind).Append("\",\"table\":").Append(f.Table)
                        .Append(",\"served\":").Append(f.Served).Append(",\"cleared\":").Append(f.Cleared).Append(",\"took\":").Append(f.TookOver);
                line.Append('}');
            }
            line.Append("]}");
            Send(line.ToString());
        }

        private static string Task(DeliveryTask task)
        {
            if (task == null) return "";
            string kind = task.Kind == DeliveryKind.Soup ? "スープ" : task.Kind == DeliveryKind.Food ? "料理" : "お皿の片付け";
            return kind + "（" + (task.TableIndex + 1) + "番テーブル）";
        }

        private void OnDestroy()
        {
            running = false;
            lock (gate) connection?.Close();
            thread?.Join(1000);
        }
    }
}
