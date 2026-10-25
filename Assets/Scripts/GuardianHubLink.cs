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
    /// <summary>
    /// Door 1 (store network ⇄ standalone) and the supervisory AI's hands in Unity. Connects to the hub on
    /// this PC (scripts/guardian_hub, 127.0.0.1:47612; accepts no connections itself), sends what the robots
    /// are doing every 0.5 s and applies the hub's advice.
    /// The network may only lower speeds: a slowdown comes off only when a person restores it here (or
    /// approves the AI's proposal to). Unusable advice closes the door by itself; only a person opens it
    /// again. Standalone, or with no hub running, the simulation runs exactly as before.
    /// </summary>
    public sealed class GuardianHubLink : MonoBehaviour
    {
#pragma warning disable 0649 // filled in by JsonUtility
        // says: what the robot would answer to "今の状況は？" (RobomoriTalk); table: 1-3 for its job, 0 for none.
        [Serializable] private sealed class RobotOut { public string id, condition, task, phase, wait, says; public float x, z, speed, vibration, speed_scale; public bool stopped, held, carrying; public int table; }
        [Serializable] private sealed class PersonOut { public string id, role, state; public float x, z; public bool busy; }
        [Serializable] private sealed class SeatOut { public string id, state; public int table; }
        // The last question a person asked a robot and its answer; n goes up by one per exchange (0: none yet).
        [Serializable] private sealed class TalkOut { public string robot, question, answer, by; public int n; }
        [Serializable] private sealed class TelemetryOut { public int v = 1; public string type = "telemetry", boot, door; public long seq; public float t; public RobotOut[] robots; public PersonOut[] people; public SeatOut[] seats; public TalkOut talk; public string[] approved, declined, restored; }
        // The floor as it stands in this scene, read from the objects themselves (not a copy kept in the core):
        // each thing is a labelled footprint (centre x, z and size w, d in metres), like a camera's detections.
        [Serializable] private sealed class ThingOut { public string id, kind, label; public float x, z, w, d; }
        [Serializable] private sealed class SpotOut { public string id, kind, label; public float x, z; }
        [Serializable] private sealed class SceneOut { public int v = 1; public string type = "scene", boot; public long seq; public float west, east, south, north; public ThingOut[] things; public SpotOut[] spots; }
        [Serializable] private sealed class RobotAdvice { public string id, reason; public float speed_scale; }
        [Serializable] private sealed class ZoneAdvice { public string id, name, reason; public float x, z, r, speed_scale; }
        [Serializable] private sealed class ProposalIn { public string id, robot, zone, reason; public float speed_scale; }
        [Serializable] private sealed class PointIn { public float x, z; }
        // No nested arrays: JsonUtility cannot read them, so the hub sends points as {x, z} objects.
        [Serializable] private sealed class DispatchIn { public string robot, situation, role, person, backup; public float eta_s; public string[] route; public PointIn[] points; public PointIn @from; public bool busy; }
        [Serializable] private sealed class AdviceIn { public int v; public string type, boot, gate, ai; public long seq; public RobotAdvice[] robots; public ZoneAdvice[] zones; public ProposalIn[] proposals; public DispatchIn[] dispatch; }
#pragma warning restore 0649

        private sealed class Slowdown { public string Key, Robot, Label, Reason; public float Scale; public bool Zone; public Vector3 Center; public float Radius; }
        private sealed class Decision { public string Kind, Value; public int SendsLeft = 4; }  // the hub treats repeats as no-ops

        public int Port = 47612;
        public float SendInterval = .5f;
        /// <summary>No advice for this long: the AI counts as not answering (its slowdowns stay).</summary>
        public float SilenceSeconds = 3f;
        private const int MaxLine = 16384;
        private const float RestoreGrace = 5f;

        // IO thread ⇄ main thread
        private readonly ConcurrentQueue<KeyValuePair<int, string>> inbox = new ConcurrentQueue<KeyValuePair<int, string>>();
        private readonly ConcurrentQueue<byte[]> outbox = new ConcurrentQueue<byte[]>();
        private int inQueued, outQueued, generation, oversized;
        private volatile bool running, closed, connected;
        private Thread thread;

        // main thread only
        private GuardianSimulator sim;
        private readonly string boot = "unity-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        private long seq;
        private float nextSend, lastAdvice = -999;
        private int seenGeneration = -1;
        private string hubBoot;
        private long lastSeq = -1;
        private bool everLinked;
        private bool scaled;  // ApplyRules lowered some robot's speed last time
        private string gate = "store", ai = "";
        private readonly Dictionary<string, Slowdown> slowdowns = new Dictionary<string, Slowdown>();
        private readonly Dictionary<string, float> restoredAt = new Dictionary<string, float>();
        private readonly HashSet<string> declinedIds = new HashSet<string>();
        private readonly List<ProposalIn> proposals = new List<ProposalIn>();
        private readonly List<DispatchIn> dispatch = new List<DispatchIn>();
        private readonly List<Decision> decisions = new List<Decision>();
        private readonly Queue<float> rejections = new Queue<float>();
        private readonly List<string> notes = new List<string>();
        private int rejected;
        // the scene message: sent on each new connection, and again when the floor changes (checked every few seconds)
        private const float SceneCheckSeconds = 5f;
        private const int MaxThings = 160, MaxGuests = 20;
        private int sceneGeneration = -1;
        private long sceneSeq;
        private float nextSceneCheck;
        private string sceneSignature;
        private readonly List<GuardianDinerFlow.GuestView> guestViews = new List<GuardianDinerFlow.GuestView>();
        private readonly List<GuardianDinerFlow.SeatView> seatViews = new List<GuardianDinerFlow.SeatView>();

        /// <summary>Door 1 is closed: nothing goes to or comes from the store network.</summary>
        public bool Standalone { get; private set; }
        public string DoorReason { get; private set; } = "";
        /// <summary>The hub is connected and has advised within SilenceSeconds.</summary>
        public bool Linked => !Standalone && connected && Time.unscaledTime - lastAdvice <= SilenceSeconds;

        private void Start()
        {
            sim = GetComponent<GuardianSimulator>();
            string[] args = Environment.GetCommandLineArgs();
            // Recorded verifications run in batch mode and expect the robots' own speeds.
            if (Application.isBatchMode && !args.Contains("-robomoriHub")) { enabled = false; return; }
            foreach (string arg in args)
                if (arg.StartsWith("-robomoriHubPort=") && int.TryParse(arg.Substring("-robomoriHubPort=".Length), out int port) && port > 0 && port < 65536) Port = port;
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "Robomori hub link" };
            thread.Start();
        }

        // ---- IO thread ---------------------------------------------------------------------------------

        private void Run()
        {
            var buffer = new byte[4096];
            var line = new MemoryStream();
            while (running)
            {
                if (closed) { Thread.Sleep(200); continue; }
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        tcp.Connect(IPAddress.Loopback, Port);
                        tcp.NoDelay = true;
                        NetworkStream stream = tcp.GetStream();
                        stream.WriteTimeout = 500;
                        int gen = Interlocked.Increment(ref generation);
                        while (outbox.TryDequeue(out _)) Interlocked.Decrement(ref outQueued);  // old snapshots
                        line.SetLength(0);
                        bool skipping = false;
                        connected = true;
                        while (running && !closed)
                        {
                            while (outbox.TryDequeue(out byte[] data)) { Interlocked.Decrement(ref outQueued); stream.Write(data, 0, data.Length); }
                            if (!tcp.Client.Poll(20000, SelectMode.SelectRead)) continue;  // 20 ms
                            int available = tcp.Available;
                            if (available == 0) break;  // the hub closed the connection
                            int n = stream.Read(buffer, 0, Math.Min(buffer.Length, available));
                            if (n <= 0) break;
                            for (int i = 0; i < n; i++)
                            {
                                byte b = buffer[i];
                                if (b == (byte)'\n')
                                {
                                    if (!skipping && line.Length > 0) Enqueue(gen, Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                                    line.SetLength(0); skipping = false;
                                }
                                else if (skipping) { }
                                else if (line.Length >= MaxLine) { line.SetLength(0); skipping = true; Interlocked.Increment(ref oversized); }
                                else line.WriteByte(b);
                            }
                        }
                    }
                }
                catch (SocketException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                finally { connected = false; }
                for (int i = 0; i < 10 && running && !closed; i++) Thread.Sleep(100);  // try again every second
            }
        }

        private void Enqueue(int gen, string text)
        {
            // Each advice is a whole snapshot, so only the newest few matter: a flood cannot grow memory.
            inbox.Enqueue(new KeyValuePair<int, string>(gen, text));
            if (Interlocked.Increment(ref inQueued) > 16 && inbox.TryDequeue(out _)) Interlocked.Decrement(ref inQueued);
        }

        // ---- main thread -------------------------------------------------------------------------------

        private void Update()
        {
            if (sim == null) return;
            // No hub is connected and none has ever answered: nothing to do this frame (the IO thread only
            // tries to connect). Once connected, telemetry goes out so the hub can start advising.
            if (!connected && !everLinked && !Standalone && inbox.IsEmpty && Volatile.Read(ref oversized) == 0) return;
            GuardianFrameMarker.Enter("Robomori hub link");
            float now = Time.unscaledTime;
            if (Interlocked.Exchange(ref oversized, 0) > 0 && !Standalone) Reject("1 行が長すぎる");
            for (int i = 0; i < 16 && inbox.TryDequeue(out var item); i++)
            {
                Interlocked.Decrement(ref inQueued);
                if (Standalone) continue;  // the door is closed: nothing from the network is used
                if (item.Key != seenGeneration) { seenGeneration = item.Key; hubBoot = null; lastSeq = -1; }  // a new connection
                Accept(item.Value, now);
            }
            if (!Standalone && connected)
            {
                int gen = Volatile.Read(ref generation);
                if (gen != sceneGeneration || now >= nextSceneCheck) SendScene(gen, now);
            }
            if (!Standalone && connected && now >= nextSend) { nextSend = now + SendInterval; Send(now); }
            // Proposals and recommendations are the AI's current view; drop them when it stops answering.
            if (now - lastAdvice > SilenceSeconds) { proposals.Clear(); dispatch.Clear(); }
            ApplyRules();
            DrawRoutes();
        }

        private void Accept(string json, float now)
        {
            AdviceIn advice;
            try { advice = JsonUtility.FromJson<AdviceIn>(json); }
            catch (ArgumentException) { Reject("JSON として読めない"); return; }
            if (advice == null || advice.v != 1 || advice.type != "advice" || string.IsNullOrEmpty(advice.boot)) { Reject("ハブの助言の形ではない"); return; }
            if (hubBoot != null && advice.boot != hubBoot) { Reject("同じ接続に別のハブの助言が混ざった"); return; }
            if (advice.seq <= lastSeq) { Reject("古い助言（順番が戻った）"); return; }
            if (advice.gate != "store" && advice.gate != "external") { Reject("扉 2 の状態が読めない"); return; }
            hubBoot = advice.boot; lastSeq = advice.seq; lastAdvice = now;
            if (!everLinked)
            {
                everLinked = true;
                // Keep simulating (and advising) while another window has focus, only once a hub is in use:
                // without a hub, Unity behaves exactly as before.
                Application.runInBackground = true;
                Note("監視ハブとつながった");
                sim.Log("Hub linked on 127.0.0.1:" + Port);
            }
            if (gate != advice.gate) Note(advice.gate == "external" ? "扉 2 が開いた（Google Drive へ書き出し中）" : "扉 2 が閉じた");
            gate = advice.gate;
            ai = advice.ai == "learning" ? "learning" : "ok";

            var listed = new HashSet<string>();
            bool raised = false;
            foreach (RobotAdvice r in advice.robots ?? Array.Empty<RobotAdvice>())
            {
                if (r == null || Find(r.id) == null) continue;  // not on the floor now
                if (!Lowers(r.speed_scale)) { raised = true; continue; }
                string key = "robot:" + r.id; listed.Add(key);
                Tighten(new Slowdown { Key = key, Robot = r.id, Label = "ロボット " + r.id, Reason = Clip(r.reason, 120), Scale = r.speed_scale }, now);
            }
            foreach (ZoneAdvice z in advice.zones ?? Array.Empty<ZoneAdvice>())
            {
                if (z == null) continue;
                string name = !string.IsNullOrEmpty(z.name) ? z.name : z.id;
                if (string.IsNullOrEmpty(name) || name.Length > 32 || !Finite(z.x) || !Finite(z.z) || Mathf.Abs(z.x) > 100 || Mathf.Abs(z.z) > 100
                    || !Finite(z.r) || z.r < .2f || z.r > 6) { raised = true; continue; }
                if (!Lowers(z.speed_scale)) { raised = true; continue; }
                string key = "zone:" + (string.IsNullOrEmpty(z.id) ? name : z.id); listed.Add(key);
                Tighten(new Slowdown { Key = key, Label = name, Reason = Clip(z.reason, 120), Scale = z.speed_scale, Zone = true, Center = new Vector3(z.x, 0, z.z), Radius = z.r }, now);
            }
            if (raised) Reject("速度を上げる、または範囲外の指示（ネットワークからは下げることしかできない）");
            // A slowdown a person restored here stays off until the hub has heard about it.
            foreach (string key in restoredAt.Keys.ToArray())
                if (!listed.Contains(key) || now - restoredAt[key] > RestoreGrace) restoredAt.Remove(key);

            proposals.Clear();
            var offered = new HashSet<string>();
            foreach (ProposalIn p in advice.proposals ?? Array.Empty<ProposalIn>())
            {
                if (p == null || string.IsNullOrEmpty(p.id) || p.id.Length > 32) continue;
                offered.Add(p.id);
                string key = KeyOf(p);
                if (key == null || !slowdowns.ContainsKey(key) || declinedIds.Contains(p.id) || proposals.Count >= 4) continue;
                p.reason = Clip(p.reason, 120);
                proposals.Add(p);
            }
            declinedIds.IntersectWith(offered);

            var before = new HashSet<string>(dispatch.Select(d => d.robot + "→" + d.person));
            dispatch.Clear();
            foreach (DispatchIn d in advice.dispatch ?? Array.Empty<DispatchIn>())
            {
                if (d == null || dispatch.Count >= 3 || Find(d.robot) == null || string.IsNullOrEmpty(d.person) || !Finite(d.eta_s) || d.eta_s < 0 || d.eta_s > 3600) continue;
                if (d.points == null || d.points.Length > 16 || d.points.Any(p => p == null || !Finite(p.x) || !Finite(p.z) || Mathf.Abs(p.x) > 100 || Mathf.Abs(p.z) > 100))
                    d.points = Array.Empty<PointIn>();
                if (d.@from != null && (!Finite(d.@from.x) || !Finite(d.@from.z) || Mathf.Abs(d.@from.x) > 100 || Mathf.Abs(d.@from.z) > 100)) d.@from = null;
                if (d.route == null || d.route.Length > 16) d.route = Array.Empty<string>();
                d.situation = Clip(d.situation, 32); d.person = Clip(d.person, 32); d.backup = Clip(d.backup, 32);
                dispatch.Add(d);
                if (!before.Contains(d.robot + "→" + d.person))
                {
                    Note("対応のおすすめ: " + d.robot + "（" + d.situation + "）→ " + d.person);
                    sim.Log("Hub recommends " + d.person + " for " + d.robot + " (" + d.situation + ", about " + d.eta_s.ToString("0") + " s)");
                }
            }
        }

        private static bool Lowers(float scale) => Finite(scale) && scale > 0 && scale < 1;

        private void Tighten(Slowdown wanted, float now)
        {
            if (restoredAt.TryGetValue(wanted.Key, out float at) && now - at <= RestoreGrace) return;
            wanted.Scale = Mathf.Max(.3f, wanted.Scale);
            if (slowdowns.TryGetValue(wanted.Key, out Slowdown current))
            {
                if (wanted.Scale < current.Scale - .001f) { current.Scale = wanted.Scale; current.Reason = wanted.Reason; Note(current.Label + " をさらに ×" + wanted.Scale.ToString("0.0#") + " に下げた（監視AI）"); }
                return;  // the network never raises a limit: only a person here does
            }
            slowdowns[wanted.Key] = wanted;
            Note(wanted.Label + " の速度上限を ×" + wanted.Scale.ToString("0.0#") + " に下げた（監視AI）");
            sim.Log("Hub: " + wanted.Key + " speed x" + wanted.Scale.ToString("0.0#") + " (" + wanted.Reason + ")");
        }

        private void Reject(string reason)
        {
            float now = Time.unscaledTime;
            rejected++;
            rejections.Enqueue(now);
            while (rejections.Count > 0 && now - rejections.Peek() > 10) rejections.Dequeue();
            Note("使えない助言を捨てた: " + reason);
            if (rejections.Count >= 3) CloseDoor("10 秒に 3 回、使えない助言が届いた（" + reason + "）");
        }

        /// <summary>Close door 1: run standalone. Done automatically on bad advice, or by a person.</summary>
        public void CloseDoor(string reason)
        {
            if (Standalone) return;
            Standalone = true; closed = true; DoorReason = reason;
            proposals.Clear(); dispatch.Clear(); rejections.Clear();
            Note("扉 1 を閉じた（単独で動作）: " + reason);
            sim?.Log("Hub door closed, standalone: " + reason);
        }

        /// <summary>Open door 1 again. Only a person does this, after checking why it closed.</summary>
        public void OpenDoor()
        {
            if (!Standalone) return;
            Standalone = false; closed = false; DoorReason = "";
            hubBoot = null; lastSeq = -1;
            Note("扉 1 を開けた（人が確かめて、店内ネットワークへ）");
            sim?.Log("Hub door opened by a person");
        }

        private void Restore(string key)
        {
            if (!slowdowns.TryGetValue(key, out Slowdown s)) return;
            slowdowns.Remove(key);
            restoredAt[key] = Time.unscaledTime;
            ProposalIn proposal = proposals.FirstOrDefault(p => KeyOf(p) == key);
            if (proposal != null)
            {
                proposals.Remove(proposal);
                Decide("approved", proposal.id);
                Note("承認: " + s.Label + " を元の速度に戻した（AI の提案を人が承認）");
            }
            else Note(s.Label + " を元の速度に戻した（人の判断）");
            Decide("restored", key);  // also when approving, in case the hub restarted and lost the proposal
            sim.Log("Hub: a person restored " + key);
        }

        private void Decline(ProposalIn proposal)
        {
            proposals.Remove(proposal);
            declinedIds.Add(proposal.id);
            Decide("declined", proposal.id);
            Note("見送り: " + proposal.reason + "。速度は下げたまま");
        }

        private void Decide(string kind, string value)
        {
            if (decisions.Count >= 32) decisions.RemoveAt(0);
            decisions.Add(new Decision { Kind = kind, Value = value });
        }

        private void Send(float now)
        {
            var robots = new List<RobotOut>();
            GuardianRobotTalk talk = sim.RobotTalk;
            foreach (GuardianRobot robot in sim.Robots)
            {
                if (robot == null || !robot.gameObject.activeInHierarchy || string.IsNullOrEmpty(robot.RobotId)) continue;
                GuardianPhysicsDriver driver = robot.PhysicsDriver;
                bool body = driver != null && driver.Body != null;
                Vector3 p = body ? driver.Body.position : robot.transform.position;
                if (!Finite(p.x) || !Finite(p.z)) continue;  // not placed yet
                DeliveryTask task = robot.CurrentTask;
                robots.Add(new RobotOut
                {
                    id = robot.RobotId, x = p.x, z = p.z,
                    speed = Clean(body ? driver.PlanarVelocity.magnitude : 0, 0, 20),
                    condition = robot.Condition.ToString(), stopped = robot.EmergencyStopped, held = driver != null && driver.SafetyHeld,
                    task = task != null ? task.Kind.ToString() : "", phase = task != null ? task.Phase.ToString() : "",
                    table = task != null ? Mathf.Clamp(task.TableIndex + 1, 0, 99) : 0,
                    carrying = robot.Carrying, vibration = Clean(robot.Vibration, 0, 1),
                    speed_scale = driver != null ? Clean(driver.SpeedScale, .3f, 1) : 1,
                    wait = Clip(driver != null ? driver.WaitReason : "", 120),
                    says = Clip(Safely(() => talk != null ? talk.Explain(robot) : ""), 160),
                });
            }
            if (robots.Count == 0) return;
            var people = new List<PersonOut>();
            if (sim.Diners != null && sim.Diners.TryGetStaff(out Vector3 at, out bool busy) && Finite(at.x) && Finite(at.z))
                people.Add(new PersonOut { id = "staff", role = "floor", state = busy ? "ご案内" : "待機", x = at.x, z = at.z, busy = busy });
            var seats = new List<SeatOut>();
            if (sim.Diners != null)
            {
                sim.Diners.Snapshot(guestViews, seatViews);
                foreach (GuardianDinerFlow.GuestView g in guestViews)
                    if (people.Count <= MaxGuests && Finite(g.At.x) && Finite(g.At.z))
                        people.Add(new PersonOut { id = Clip(g.Id, 32), role = "guest", state = g.State, x = g.At.x, z = g.At.z });
                foreach (GuardianDinerFlow.SeatView c in seatViews) seats.Add(new SeatOut { id = Clip(c.Id, 32), state = c.State, table = c.Table });
            }
            var exchange = new TalkOut();
            if (talk != null)
            {
                exchange.n = talk.LastExchange(out string who, out string question, out string answer, out string by);
                if (exchange.n > 0) { exchange.robot = Clip(who, 8); exchange.question = Clip(question, 120); exchange.answer = Clip(answer, 300); exchange.by = Clip(by, 32); }
            }
            var message = new TelemetryOut
            {
                boot = boot, door = "store", seq = ++seq, t = now, robots = robots.ToArray(), people = people.ToArray(),
                seats = seats.ToArray(), talk = exchange,
                approved = Decisions("approved"), declined = Decisions("declined"), restored = Decisions("restored"),
            };
            foreach (Decision d in decisions) d.SendsLeft--;
            decisions.RemoveAll(d => d.SendsLeft <= 0);
            byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message) + "\n");
            if (data.Length > MaxLine)
            {
                // The robots and the decisions must always get through: leave the screen-only extras out this time.
                message.people = people.Where(x => x.role != "guest").ToArray(); message.seats = new SeatOut[0]; message.talk = new TalkOut();
                foreach (RobotOut r in message.robots) r.says = "";
                data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message) + "\n");
                if (data.Length > MaxLine) return;
            }
            Enqueue(data);
        }

        private void Enqueue(byte[] data)
        {
            outbox.Enqueue(data);
            if (Interlocked.Increment(ref outQueued) > 4 && outbox.TryDequeue(out _)) Interlocked.Decrement(ref outQueued);
        }

        private static string Safely(Func<string> read)
        {
            try { return read() ?? ""; }
            catch (Exception) { return ""; }  // an explanation is a nicety: it must never stop the telemetry
        }

        // ---- the scene: what is on the floor, read from the objects in this scene ----

        private void SendScene(int gen, float now)
        {
            nextSceneCheck = now + SceneCheckSeconds;
            SceneOut scene;
            try { scene = BuildScene(); }
            catch (Exception e) { if (gen != sceneGeneration) { sceneGeneration = gen; Debug.LogWarning("[GuardianHubLink] scene not read: " + e.Message); } return; }
            if (scene == null) { sceneGeneration = gen; sceneSignature = null; return; }  // no restaurant yet: look again at the next check
            string json = JsonUtility.ToJson(scene);
            // Send it to each new connection, and whenever the floor itself changed.
            if (gen == sceneGeneration && json == sceneSignature) return;
            sceneGeneration = gen; sceneSignature = json;
            scene.boot = boot; scene.seq = ++sceneSeq;
            byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(scene) + "\n");
            if (data.Length > MaxLine) { Debug.LogWarning("[GuardianHubLink] scene too large to send (" + data.Length + " bytes)"); return; }
            Enqueue(data);
        }

        private SceneOut BuildScene()
        {
            Transform environment = sim.transform.Find("Generated 3D World/" + RestaurantVisuals.RootName);
            if (environment == null) return null;
            var things = new List<ThingOut>();
            var spots = new List<SpotOut>();
            foreach (Transform child in environment)
            {
                if (things.Count >= MaxThings || !child.gameObject.activeInHierarchy) continue;
                RestaurantFixture fixture = child.GetComponent<RestaurantFixture>();
                if (fixture == null) continue;
                if (fixture.AssetId == "KitchenStaff") { spots.Add(Spot("cook", "cook", "調理担当", child.position)); continue; }
                if (child.GetComponent<RestaurantPerson>() != null) continue;  // the seated figures are people, not furniture
                if (fixture.AssetId == "RoomShell") continue;  // the room is given by its walls below
                if (Footprint(child, out Rect area)) things.Add(Thing(child.name, KindOf(fixture.AssetId), LabelOf(fixture.AssetId, child.name), area));
            }
            Transform room = environment.Find(RestaurantEnclosure.RootName);
            if (room != null)
                foreach (Transform child in room)
                {
                    if (things.Count >= MaxThings || !child.gameObject.activeInHierarchy) continue;
                    string kind = child.name == RestaurantEnclosure.KioskName ? "kiosk" : child.name == "Waiting board" ? "board"
                        : child.name.StartsWith("Waiting mark ") && !child.name.StartsWith("Waiting mark inner") ? "mark" : null;
                    if (kind == null || !Footprint(child, out Rect area)) continue;
                    string label = kind == "kiosk" ? "セルフレジ" : kind == "board" ? "案内板" : child.name.Substring("Waiting mark ".Length);
                    things.Add(Thing(child.name, kind, label, area));
                }
            float half = RestaurantEnclosure.DoorWidth / 2;
            things.Add(Thing(RestaurantEnclosure.EntranceName, "door", "入口", new Rect(RestaurantEnclosure.EntranceX - half, RestaurantEnclosure.South - .12f, half * 2, .24f)));
            things.Add(Thing(RestaurantEnclosure.ExitName, "door", "出口", new Rect(RestaurantEnclosure.ExitX - half, RestaurantEnclosure.South - .12f, half * 2, .24f)));
            int index = 0;
            foreach (Vector3 point in GuardianSimulator.ServicePoints)
            {
                string id = index == 0 ? "kitchen" : index == 1 ? "return" : "table" + (index - 1);
                string label = index == 0 ? "料理の受け取り" : index == 1 ? "皿の返却" : "テーブル" + (index - 1) + " の受け渡し";
                spots.Add(Spot("service-" + id, "service", label, point));
                index++;
            }
            foreach (GuardianRobot robot in sim.Robots)
                if (robot != null && robot.gameObject.activeInHierarchy && !string.IsNullOrEmpty(robot.RobotId))
                    spots.Add(Spot("home-" + Clip(robot.RobotId, 8), "home", Clip(robot.RobotId, 8) + " 待機", robot.Home));
            if (sim.Diners != null && sim.Diners.Running) spots.Add(Spot("staff", "staff", "店員の待機場所", GuardianDinerFlow.StaffPost));
            return new SceneOut
            {
                west = RestaurantEnclosure.West, east = RestaurantEnclosure.East, south = RestaurantEnclosure.South, north = RestaurantEnclosure.North,
                things = things.ToArray(), spots = spots.ToArray(),
            };
        }

        /// <summary>The object's footprint on the floor: the box around its renderers, seen from above.</summary>
        private static bool Footprint(Transform t, out Rect area)
        {
            area = default;
            bool any = false;
            Bounds all = default;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                if (!any) { all = r.bounds; any = true; } else all.Encapsulate(r.bounds);
            }
            if (!any || !Finite(all.min.x) || !Finite(all.min.z) || all.size.x <= 0 || all.size.z <= 0) return false;
            area = new Rect(all.min.x, all.min.z, all.size.x, all.size.z);
            return true;
        }

        private static float Round(float v) => Mathf.Round(v * 100) / 100;

        private static ThingOut Thing(string id, string kind, string label, Rect area) => new ThingOut
        {
            id = Clip(id, 48), kind = kind, label = Clip(label, 24),
            x = Round(area.center.x), z = Round(area.center.y), w = Round(area.width), d = Round(area.height),
        };

        private static SpotOut Spot(string id, string kind, string label, Vector3 at) => new SpotOut { id = Clip(id, 48), kind = kind, label = Clip(label, 24), x = Round(at.x), z = Round(at.z) };

        private static string KindOf(string asset)
        {
            switch (asset)
            {
                case "DiningTable": return "table";
                case "DiningChair": return "chair";
                case "BoothBench": return "booth";
                case "KitchenRange": case "KitchenSink": case "KitchenPrep": return "kitchen";
                case "PickupCounter": return "pickup";
                case "ReturnCounter": return "return";
                default: return "other";  // anything added later still shows, with its own name
            }
        }

        private static string LabelOf(string asset, string name)
        {
            switch (asset)
            {
                case "DiningTable": return name.StartsWith("Dining Table ") ? "テーブル" + name.Substring("Dining Table ".Length) : "テーブル";
                case "DiningChair": return "";
                case "BoothBench": return "ソファ席";
                case "KitchenRange": return "コンロ";
                case "KitchenSink": return "シンク";
                case "KitchenPrep": return "調理台";
                case "PickupCounter": return "受け取り口";
                case "ReturnCounter": return "返却台";
                default: return name;
            }
        }

        private string[] Decisions(string kind) => decisions.Where(d => d.Kind == kind).Select(d => d.Value).Distinct().ToArray();

        /// <summary>Each robot's speed limit: the lowest of its own slowdown and the places it is in.</summary>
        private void ApplyRules()
        {
            if (slowdowns.Count == 0 && !scaled) return;  // nothing lowered, nothing to put back
            scaled = slowdowns.Count > 0;
            foreach (GuardianRobot robot in sim.Robots)
            {
                if (robot == null) continue;
                GuardianPhysicsDriver driver = robot.PhysicsDriver;
                if (driver == null || driver.Body == null) continue;
                float scale = 1;
                if (slowdowns.Count > 0)
                {
                    Vector3 p = driver.Body.position;
                    foreach (Slowdown s in slowdowns.Values)
                    {
                        bool applies = s.Zone ? new Vector2(p.x - s.Center.x, p.z - s.Center.z).sqrMagnitude <= s.Radius * s.Radius : s.Robot == robot.RobotId;
                        if (applies) scale = Mathf.Min(scale, s.Scale);
                    }
                }
                driver.SpeedScale = Mathf.Clamp(scale, .3f, 1);
            }
        }

        private GuardianRobot Find(string id) => string.IsNullOrEmpty(id) ? null : sim.Robots.FirstOrDefault(r => r != null && r.RobotId == id && r.gameObject.activeInHierarchy);
        private static string KeyOf(ProposalIn p) => !string.IsNullOrEmpty(p.robot) ? "robot:" + p.robot : !string.IsNullOrEmpty(p.zone) ? "zone:" + p.zone : null;
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static float Clean(float v, float low, float high) => Finite(v) ? Mathf.Clamp(v, low, high) : low;
        private static string Clip(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\n', ' ').Replace('\r', ' ');
            return text.Length > limit ? text.Substring(0, limit) : text;
        }

        private void Note(string text)
        {
            notes.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            if (notes.Count > 3) notes.RemoveAt(0);
        }

        // ---- display -----------------------------------------------------------------------------------

        private readonly List<LineRenderer> routes = new List<LineRenderer>();
        private Material routeMaterial;

        /// <summary>The recommended person's walk to each broken robot, drawn on the floor.</summary>
        private void DrawRoutes()
        {
            for (int i = 0; i < Mathf.Max(routes.Count, dispatch.Count); i++)
            {
                if (i >= dispatch.Count || !Shown) { if (i < routes.Count) routes[i].enabled = false; continue; }
                DispatchIn d = dispatch[i];
                GuardianRobot robot = Find(d.robot);
                var points = new List<Vector3>();
                if (d.@from != null) points.Add(new Vector3(d.@from.x, .06f, d.@from.z));
                foreach (PointIn p in d.points) points.Add(new Vector3(p.x, .06f, p.z));
                if (robot != null) points.Add(new Vector3(robot.transform.position.x, .06f, robot.transform.position.z));
                if (i >= routes.Count && !AddRoute()) return;
                LineRenderer line = routes[i];
                line.positionCount = points.Count;
                line.SetPositions(points.ToArray());
                line.enabled = points.Count >= 2;
            }
        }

        private bool AddRoute()
        {
            if (routeMaterial == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null) return false;
                routeMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
            }
            var go = new GameObject("Hub route " + routes.Count);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = routeMaterial;
            line.useWorldSpace = true;
            line.widthMultiplier = .07f;
            line.numCapVertices = 2;
            line.startColor = line.endColor = new Color(.35f, .9f, .5f, .9f);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            routes.Add(line);
            return true;
        }

        private bool Shown => sim != null && enabled && (everLinked || Standalone) && !sim.DeveloperMode && !sim.CustomPresentationActive && sim.Mode == GuardianSimulationMode.Physics;

        private Font font;
        private GUIStyle title, text, small, button;
        private Texture2D pixel;
        private Rect panel;  // in GUI units (screen pixels / s), for IsUi

        /// <summary>The panel, so a click there does not select or drag in the 3D view.</summary>
        public bool IsUi(Vector2 mouse)
        {
            if (!Shown) return false;
            float s = Mathf.Max(1f, Screen.height / 720f);
            return panel.Contains(new Vector2(mouse.x, Screen.height - mouse.y) / s);
        }

        private void Fill(Rect r, Color c) { Color old = GUI.color; GUI.color = c; GUI.DrawTexture(r, pixel); GUI.color = old; }

        private void OnGUI()
        {
            if (!Shown) { panel = Rect.zero; return; }
            if (title == null)
            {
                font = GuardianFonts.Create(16);
                title = new GUIStyle(GUI.skin.label) { font = font, fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = false };
                title.normal.textColor = Color.white;
                text = new GUIStyle(GUI.skin.label) { font = font, fontSize = 13, wordWrap = true };
                text.normal.textColor = new Color(.9f, .92f, .95f);
                small = new GUIStyle(text) { fontSize = 11 };
                small.normal.textColor = new Color(.68f, .72f, .78f);
                button = new GUIStyle(GUI.skin.button) { font = font, fontSize = 12, fontStyle = FontStyle.Bold };
                pixel = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave }; pixel.SetPixel(0, 0, Color.white); pixel.Apply();
            }
            float s = Mathf.Max(1f, Screen.height / 720f), w = Screen.width / s;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            const float width = 372;
            float inner = width - 20;
            Rect board = GuardianCapabilityBoard.ScreenRect;
            float top = board.height > 0 ? board.yMax / s + 8 : 170;
            bool silent = !Standalone && !Linked;
            Color accent = Standalone ? new Color(1f, .45f, .4f) : silent || ai == "learning" ? new Color(1f, .75f, .3f) : new Color(.35f, .85f, .5f);
            string act = null; ProposalIn decline = null; bool toggleDoor = false;

            if (folded)
            {
                // Folded away: only a tab that opens it again (the slowdowns and the door keep working as before).
                panel = new Rect(w - 150 - 12, top, 150, 30);
                Fill(panel, new Color(.05f, .06f, .08f, .86f));
                Fill(new Rect(panel.x, panel.y, panel.width, 3), accent);
                if (GUI.Button(new Rect(panel.x + 6, panel.y + 5, panel.width - 12, 22), "監視AI を開く ◂", button)) folded = false;
                GUI.matrix = saved;
                return;
            }

            // Measure first, then draw (IMGUI has no auto-height box for hand-placed rects). Every line is measured
            // with its own style and width, so a wrapped line pushes the next one down instead of being cut.
            string door = Standalone ? "扉1　閉（単独で動作中）" : "扉1　店内ネットワーク　開";
            string gateLine = Standalone || silent ? "扉2　インターネット（Google Drive）　―" : gate == "external" ? "扉2　Google Drive へ書き出し中（自動で閉じます）" : "扉2　インターネット（Google Drive）　閉";
            string aiLine = Standalone ? "監視AI　使っていない（単独）" : !connected ? "監視AI　ハブを待っています（減速はそのまま）"
                : silent ? "監視AI　応答なし（減速はそのまま）" : ai == "learning" ? "監視AI　学習中（ふだんの揺れを覚えています）" : "監視AI　見張り中";
            const string rule = "閉じるのは自動、開けるのは人（理由を確かめてから戻す）";
            const string slowTitle = "速度を下げているところ（厳しくするのは AI、緩めるのは人）";
            const string dispatchTitle = "対応のおすすめ（推薦です。決めるのは人）";
            var shownSlowdowns = slowdowns.Values.OrderBy(x => x.Key).ToList();
            float doorWidth = inner - 112;
            float height = 6 + 26 + Mathf.Max(24, H(text, door, doorWidth) + 2);
            if (Standalone) height += H(text, DoorReason, inner) + H(small, rule, inner) + 2;
            height += H(text, gateLine, inner) + H(text, aiLine, inner) + 10;
            if (shownSlowdowns.Count > 0) height += H(small, slowTitle, inner) + 2;
            foreach (Slowdown sd in shownSlowdowns)
            {
                ProposalIn p = proposals.FirstOrDefault(q => KeyOf(q) == sd.Key);
                height += H(text, SlowdownLine(sd), inner) + H(small, sd.Reason, inner) + (p != null ? H(small, "提案: " + p.reason, inner) : 0) + 30;
            }
            if (dispatch.Count > 0) height += H(small, dispatchTitle, inner) + 2;
            foreach (DispatchIn d in dispatch) height += H(text, DispatchLine(d), inner) + H(small, RouteText(d), inner) + 4;
            foreach (string n in notes) height += H(small, n, inner);
            height += 8;

            panel = new Rect(w - width - 12, top, width, height);
            Fill(panel, new Color(.05f, .06f, .08f, .86f));
            Fill(new Rect(panel.x, panel.y, panel.width, 3), accent);
            float x = panel.x + 10, y = panel.y + 6;

            GUI.Label(new Rect(x, y, inner - 40, 24), "監視AI・接続ゲート", title);
            if (GUI.Button(new Rect(x + inner - 34, y, 34, 22), "−", button)) folded = true;  // fold the panel away
            y += 26;
            float dh = Mathf.Max(24, H(text, door, doorWidth) + 2);
            GUI.Label(new Rect(x, y, doorWidth, dh), door, text);
            if (GUI.Button(new Rect(x + inner - 104, y, 104, 22), Standalone ? "店内に戻す" : "単独にする", button)) toggleDoor = true;
            y += dh;
            if (Standalone)
            {
                y = Line(text, DoorReason, x, y, inner);
                y = Line(small, rule, x, y, inner) + 2;
            }
            y = Line(text, gateLine, x, y, inner);
            y = Line(text, aiLine, x, y, inner);
            Fill(new Rect(x, y + 4, inner, 1), new Color(1, 1, 1, .15f)); y += 10;

            if (shownSlowdowns.Count > 0) y = Line(small, slowTitle, x, y, inner) + 2;
            foreach (Slowdown sd in shownSlowdowns)
            {
                ProposalIn p = proposals.FirstOrDefault(q => KeyOf(q) == sd.Key);
                y = Line(text, SlowdownLine(sd), x, y, inner);
                y = Line(small, sd.Reason, x, y, inner);
                if (p != null)
                {
                    y = Line(small, "提案: " + p.reason, x, y, inner);
                    if (GUI.Button(new Rect(x, y + 3, 170, 24), "承認して元に戻す", button)) act = sd.Key;
                    if (GUI.Button(new Rect(x + 178, y + 3, 90, 24), "見送る", button)) decline = p;
                }
                else if (GUI.Button(new Rect(x, y + 3, 170, 24), "人の判断で元に戻す", button)) act = sd.Key;
                y += 30;
            }
            if (dispatch.Count > 0) y = Line(small, dispatchTitle, x, y, inner) + 2;
            foreach (DispatchIn d in dispatch)
            {
                y = Line(text, DispatchLine(d), x, y, inner);
                y = Line(small, RouteText(d), x, y, inner) + 4;
            }
            foreach (string n in notes) y = Line(small, n, x, y, inner);
            GUI.matrix = saved;

            if (toggleDoor) { if (Standalone) OpenDoor(); else CloseDoor("人が単独に切り替えた"); }
            if (act != null) Restore(act);
            if (decline != null) Decline(decline);
        }

        private bool folded;  // the panel is folded away to a tab (a person's choice; nothing else changes)

        /// <summary>The height a label needs at this width, never less than one full line of its font.</summary>
        private static float H(GUIStyle style, string content, float width)
            => Mathf.Ceil(Mathf.Max(style.CalcHeight(new GUIContent(content ?? ""), width), style.lineHeight) + 3);

        private static float Line(GUIStyle style, string content, float x, float y, float width)
        {
            float h = H(style, content, width);
            GUI.Label(new Rect(x, y, width, h), content ?? "", style);
            return y + h;
        }

        private static string SlowdownLine(Slowdown sd) => sd.Label + "　速度上限 ×" + sd.Scale.ToString("0.0#");
        private static string DispatchLine(DispatchIn d) => d.robot + "　" + d.situation + " → " + d.person + "（約 " + Mathf.CeilToInt(d.eta_s) + " 秒）";

        private static string RouteText(DispatchIn d)
        {
            string route = d.route != null && d.route.Length > 0 ? "経路 " + string.Join(" → ", d.route) : "";
            string backup = string.IsNullOrEmpty(d.backup) ? "" : "　次点 " + d.backup;
            return route + (d.busy ? "　（手がふさがっている分を含む）" : "") + backup;
        }

        private void OnDestroy()
        {
            running = false;
            thread?.Join(1000);
            if (sim != null)
                foreach (GuardianRobot robot in sim.Robots)
                    if (robot != null && robot.PhysicsDriver != null) robot.PhysicsDriver.SpeedScale = 1;
            foreach (LineRenderer line in routes) if (line != null) Destroy(line.gameObject);
            if (routeMaterial != null) Destroy(routeMaterial);
            if (pixel != null) Destroy(pixel);
            GuardianFonts.Release(font);
        }
    }
}
