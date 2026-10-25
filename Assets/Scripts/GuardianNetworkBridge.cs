using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Receives the sensor relay's version-1 JSON Lines snapshots over loopback TCP.
    /// Holds belong to this link; releasing them never releases an operator STOP.</summary>
    public sealed class GuardianNetworkBridge : MonoBehaviour
    {
#pragma warning disable 0649
        [Serializable] private sealed class RobotState
        { public string id, final, capability, trust; public bool measured; public string[] reasons; }
        [Serializable] private sealed class Packet
        { public int v; public string boot; public long seq; public RobotState[] robots; }
#pragma warning restore 0649

        public const int MaximumLineLength=8192;
        public int Port=47610;
        public int FallbackPort=18610;
        public float SilenceSeconds=1.5f;
        public bool HasSnapshot { get; private set; }
        public bool Silent { get; private set; }
        public bool Connected=>connected;
        public bool Receiving=>worker!=null && worker.IsAlive;
        public int RejectedPackets=>rejected+Volatile.Read(ref oversized);

        private readonly ConcurrentQueue<KeyValuePair<int,string>> inbox=new ConcurrentQueue<KeyValuePair<int,string>>();
        private readonly Dictionary<string,RobotState> latest=new Dictionary<string,RobotState>();
        private readonly object gate=new object();
        private GuardianSimulator sim;
        private Thread worker;
        private TcpClient connection;
        private volatile bool running,connected;
        private int queued,rejected,oversized,sessionVersion,acceptedVersion;
        private bool autoStarted;
        private string boot;
        private long lastSequence;
        private float lastPacket;

        private void Start()
        {
            // Batch suites use an isolated relay explicitly, never a sensor already running on this PC.
            autoStarted=!Application.isBatchMode;
            if (autoStarted) StartReceiving();
        }
        private void OnEnable() { if (autoStarted) StartReceiving(); }

        public void StartReceiving()
        {
            if (Receiving) return;
            sim=GetComponent<GuardianSimulator>();
            if (sim==null) throw new InvalidOperationException("A GuardianSimulator is required");
            foreach (string arg in Application.isBatchMode?Array.Empty<string>():Environment.GetCommandLineArgs())
                if (arg.StartsWith("-robomoriPort=",StringComparison.Ordinal))
                {
                    if (!int.TryParse(arg.Substring("-robomoriPort=".Length),out int selected))
                        throw new ArgumentException("Invalid -robomoriPort");
                    Port=selected;
                }
            if (Port<1 || Port>65535 || FallbackPort<0 || FallbackPort>65535 ||
                float.IsNaN(SilenceSeconds) || float.IsInfinity(SilenceSeconds) || SilenceSeconds<=0)
                throw new ArgumentException("Invalid sensor link configuration");
            running=true;
            worker=new Thread(Receive){IsBackground=true,Name="Guardian sensor link"};
            worker.Start();
        }

        private void Receive()
        {
            while (running)
            {
                if (!Session(Port) && running && FallbackPort>0 && FallbackPort!=Port) Session(FallbackPort);
                for (int i=0;i<10 && running;i++) Thread.Sleep(100);
            }
        }

        private bool Session(int port)
        {
            int version=Interlocked.Increment(ref sessionVersion);
            try
            {
                using (var tcp=new TcpClient())
                {
                    lock (gate) { if (!running) return false; connection=tcp; }
                    tcp.Connect(IPAddress.Loopback,port);
                    connected=true;
                    tcp.NoDelay=true;
                    // A different program may occupy the primary port. A TCP connection alone is
                    // not a relay handshake; try the fallback if no valid snapshot arrives.
                    tcp.ReceiveTimeout=3000;
                    using (var reader=new StreamReader(tcp.GetStream(),Encoding.UTF8))
                    {
                        string line;
                        while (running && (line=ReadBoundedLine(reader))!=null)
                        {
                            inbox.Enqueue(new KeyValuePair<int,string>(version,line));
                            if (Interlocked.Increment(ref queued)>16 && inbox.TryDequeue(out _))
                                Interlocked.Decrement(ref queued);
                        }
                    }
                }
            }
            catch (SocketException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { connected=false; lock (gate) connection=null; }
            return Volatile.Read(ref acceptedVersion)==version;
        }

        private string ReadBoundedLine(StreamReader reader)
        {
            var text=new StringBuilder(512);
            bool tooLong=false;
            while (running)
            {
                int c=reader.Read();
                if (c<0) return null; // A partial line at EOF is not a packet.
                if (c=='\n')
                {
                    if (tooLong) { tooLong=false; text.Clear(); continue; }
                    if (text.Length>0 && text[text.Length-1]=='\r') text.Length--;
                    return text.ToString();
                }
                if (tooLong) continue;
                if (text.Length==MaximumLineLength)
                { tooLong=true; text.Clear(); Interlocked.Increment(ref oversized); }
                else text.Append((char)c);
            }
            return null;
        }

        private void Update() { Tick(Time.realtimeSinceStartup); }

        /// <summary>Processes queued IO and the watchdog on the Unity thread. The clock is local/unscaled.</summary>
        public void Tick(float now)
        {
            for (int i=0;i<16 && inbox.TryDequeue(out var line);i++)
            {
                Interlocked.Decrement(ref queued);
                if (ApplyPacket(line.Value,now)) Volatile.Write(ref acceptedVersion,line.Key);
            }
            if (HasSnapshot && !Silent && now-lastPacket>SilenceSeconds) HoldAll();
        }

        /// <summary>Validates the complete snapshot before changing any robot. Also used by the batch suite.</summary>
        public bool ApplyPacket(string json,float now)
        {
            sim=sim!=null?sim:GetComponent<GuardianSimulator>();
            if (sim==null || json==null || json.Length>MaximumLineLength || float.IsNaN(now) ||
                float.IsInfinity(now) || now<0 || (HasSnapshot && now<lastPacket)) return Reject();
            Packet packet;
            try { packet=JsonUtility.FromJson<Packet>(json); }
            catch (ArgumentException) { return Reject(); }
            if (packet==null || packet.v!=1 || string.IsNullOrWhiteSpace(packet.boot) || packet.boot.Length>128 ||
                packet.seq<1 || packet.robots==null || packet.robots.Length==0 || packet.robots.Length>sim.Robots.Count)
                return Reject();
            var ids=new HashSet<string>();
            foreach (RobotState state in packet.robots)
            {
                if (state==null || string.IsNullOrEmpty(state.id) || !ids.Add(state.id) ||
                    !sim.Robots.Any(r=>r.RobotId==state.id) || !TryCondition(state.capability,out _) ||
                    (state.final!="Hold" && state.final!=state.capability) ||
                    (state.trust!="Trusted" && state.trust!="Untrusted") ||
                    (state.trust=="Untrusted" && state.final!="Hold") || state.reasons==null || state.reasons.Length>8 ||
                    state.reasons.Any(reason=>reason==null || reason.Length>512)) return Reject();
            }
            if (HasSnapshot && packet.boot==boot &&
                (packet.seq<=lastSequence || !ids.SetEquals(latest.Keys))) return Reject();
            if (HasSnapshot && packet.boot!=boot && now-lastPacket<=SilenceSeconds) return Reject();
            // A new packet after a gap must not bypass the watchdog by arriving before Update runs.
            if (HasSnapshot && !Silent && now-lastPacket>SilenceSeconds) HoldAll();
            boot=packet.boot; lastSequence=packet.seq; lastPacket=now;
            foreach (RobotState state in packet.robots)
                if (!latest.TryGetValue(state.id,out RobotState before) || before.final!=state.final ||
                    before.capability!=state.capability || before.trust!=state.trust)
                    sim.Log("Sensor "+state.id+" "+state.final+" ("+state.trust+"): "+
                        string.Join(" / ",state.reasons.Select(reason=>reason.Replace('\r',' ').Replace('\n',' '))));
            latest.Clear();
            foreach (RobotState state in packet.robots) latest.Add(state.id,state);
            if (!HasSnapshot) sim.Log("Sensor relay linked; seeded condition events suspended");
            HasSnapshot=true;
            if (!Silent) foreach (RobotState state in latest.Values) Apply(state);
            return true;
        }

        private bool Reject() { rejected++; return false; }
        private static bool TryCondition(string word,out RobotCondition condition)
        {
            condition=RobotCondition.Normal;
            switch (word)
            {
                case "Normal": return true;
                case "Warning": condition=RobotCondition.Warning; return true;
                case "Limited": condition=RobotCondition.Limited; return true;
                default: return false;
            }
        }

        private void Apply(RobotState state)
        {
            if (state.final=="Hold") sim.SetLinkedEmergencyStop(state.id,true);
            TryCondition(state.capability,out RobotCondition condition);
            sim.SetLinkedCondition(state.id,condition);
            if (state.final!="Hold") sim.SetLinkedEmergencyStop(state.id,false);
        }

        public bool Drives(string id)=>HasSnapshot && (Silent || latest.ContainsKey(id));

        private void HoldAll()
        {
            Silent=true;
            foreach (GuardianRobot robot in sim.Robots)
                if (robot!=null) sim.SetLinkedEmergencyStop(robot.RobotId,true);
            sim.Log("Sensor relay silent; all robots held until a person confirms recovery");
        }

        public bool ResumeAfterSilence(float now)
        {
            if (!Silent || !HasSnapshot || float.IsNaN(now) || float.IsInfinity(now) ||
                now<lastPacket || now-lastPacket>SilenceSeconds) return false;
            Silent=false;
            // Retain an explicit Hold from the latest packet and all independent operator stops.
            foreach (GuardianRobot robot in sim.Robots)
                if (robot!=null) sim.SetLinkedEmergencyStop(robot.RobotId,
                    latest.TryGetValue(robot.RobotId,out RobotState state) && state.final=="Hold");
            foreach (RobotState state in latest.Values) Apply(state);
            sim.Log("Sensor relay recovery confirmed by a person");
            return true;
        }

        public void ReapplySnapshot()
        {
            if (!HasSnapshot || sim==null) return;
            if (Silent) HoldAll();
            else foreach (RobotState state in latest.Values) Apply(state);
        }

        public void StopReceiving()
        {
            running=false;
            lock (gate) connection?.Close();
            if (worker!=null && worker.IsAlive) worker.Join(1500);
            if (HasSnapshot && !Silent && sim!=null) HoldAll();
        }
        private void OnDisable() { StopReceiving(); }
        private void OnDestroy() { StopReceiving(); }

        private void OnGUI()
        {
            if (!HasSnapshot) return;
            GUILayout.BeginArea(new Rect(12,12,345,100+latest.Count*22),GUI.skin.box);
            GUILayout.Label(Silent?"SENSOR LINK: ALL HELD":"SENSOR LINK: receiving");
            foreach (RobotState state in latest.Values)
                GUILayout.Label(state.id+"  "+state.final+" ("+state.capability+") / "+state.trust);
            if (RejectedPackets>0) GUILayout.Label("Rejected packets: "+RejectedPackets);
            if (Silent && GUILayout.Button("Resume (checked by a person)")) ResumeAfterSilence(Time.realtimeSinceStartup);
            GUILayout.EndArea();
        }
    }
}
