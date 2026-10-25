using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Guardian.UnitySim.Editor
{
    public static class GuardianNetworkBatch
    {
        private static int passed;
        public static void Verify() { GuardianBatchRunner.Execute("sensor-link",RunChecks); }

        private static string State(string id,string final,string capability=null,string trust="Trusted")
        { return "{\"id\":\""+id+"\",\"final\":\""+final+"\",\"capability\":\""+(capability??final)+
            "\",\"trust\":\""+trust+"\",\"measured\":true,\"reasons\":[\"batch test\"]}"; }
        private static string Packet(long seq,string state,string boot="batch-run")
        { return "{\"v\":1,\"boot\":\""+boot+"\",\"seq\":"+seq+",\"robots\":["+state+"]}"; }
        private static string Normal(long seq=1,string boot="batch-run")=>Packet(seq,State("B","Normal"),boot);
        private static void Require(bool result,string message)
        { if (!result) throw new InvalidOperationException(message); }

        public static void RunChecks()
        {
            passed=0;
            Scene previous=SceneManager.GetActiveScene();
            Scene scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                Case("unlinked demo unchanged",(sim,link)=>
                {
                    link.Tick(100);
                    Require(!link.HasSnapshot && !link.Silent && sim.Robots.All(r=>!r.EmergencyStopped),"An unused link held the demo");
                    sim.SetCondition("B",RobotCondition.Limited);
                    Require(sim.Robots.Single(r=>r.RobotId=="B").Condition==RobotCondition.Limited,"Unlinked controls changed");
                });
                Case("normal warning limited and KEY2 recovery",(sim,link)=>
                {
                    var b=sim.Robots.Single(r=>r.RobotId=="B");
                    Require(link.ApplyPacket(Normal(),0),"Normal rejected");
                    Require(link.ApplyPacket(Packet(2,State("B","Warning")),.2f),"Warning rejected");
                    Require(!b.EmergencyStopped && !GuardianCapabilityPolicy.Allows(b.Condition,DeliveryKind.Soup) &&
                        GuardianCapabilityPolicy.Allows(b.Condition,DeliveryKind.Food),"Warning stopped capable work");
                    Require(link.ApplyPacket(Packet(3,State("B","Hold","Limited")),.4f),"Limited rejected");
                    Require(b.EmergencyStopped && GuardianCapabilityPolicy.Allows(b.Condition,DeliveryKind.EmptyDishes) &&
                        !GuardianCapabilityPolicy.Allows(b.Condition,DeliveryKind.Food),"Limited permits changed");
                    Require(link.ApplyPacket(Normal(4),.6f) && b.Condition==RobotCondition.Normal,"KEY2 recovery failed");
                });
                Case("ineligible soup reassigned",(sim,link)=>
                {
                    link.ApplyPacket(Normal(),0);
                    sim.SetDispatchEnabled(sim.Robots.Single(r=>r.RobotId=="A"),false);
                    sim.SetDispatchEnabled(sim.Robots.Single(r=>r.RobotId=="C"),false);
                    var soup=sim.QueueTask(DeliveryKind.Soup,0);
                    Require(soup.AssignedTo?.RobotId=="B","Soup was not initially assigned to B");
                    link.ApplyPacket(Packet(2,State("B","Warning")),.2f);
                    Require(soup.AssignedTo==null,"Warning kept an ineligible soup job");
                    sim.SetDispatchEnabled(sim.Robots.Single(r=>r.RobotId=="A"),true);
                    Require(soup.AssignedTo?.RobotId=="A","Soup was not reassigned to A");
                });
                Case("manual condition cannot override sensor",(sim,link)=>
                {
                    link.ApplyPacket(Packet(1,State("B","Limited")),0);
                    sim.SetCondition("B",RobotCondition.Normal);
                    Require(sim.Robots.Single(r=>r.RobotId=="B").Condition==RobotCondition.Limited,"A demo input overrode the sensor");
                });
                Case("operator stop before sensor hold remains",(sim,link)=>
                {
                    sim.SetEmergencyStop("B",true);
                    link.ApplyPacket(Packet(1,State("B","Hold","Warning","Untrusted")),0);
                    link.ApplyPacket(Normal(2),.2f);
                    var b=sim.Robots.Single(r=>r.RobotId=="B");
                    Require(b.OperatorStopped && b.EmergencyStopped && !b.LinkedStopped,"The sensor released an operator STOP");
                });
                Case("operator stop during sensor hold remains",(sim,link)=>
                {
                    link.ApplyPacket(Packet(1,State("B","Hold","Limited","Untrusted")),0);
                    sim.SetEmergencyStop("B",true);
                    link.ApplyPacket(Normal(2),.2f);
                    Require(sim.Robots.Single(r=>r.RobotId=="B").OperatorStopped,"STOP pressed during Hold was lost");
                });
                Case("operator recovery does not release sensor hold",(sim,link)=>
                {
                    link.ApplyPacket(Packet(1,State("B","Hold","Warning","Untrusted")),0);
                    sim.SetEmergencyStop("B",false);
                    Require(sim.Robots.Single(r=>r.RobotId=="B").EmergencyStopped,"Operator recovery bypassed a sensor Hold");
                });
                Case("silence requires fresh data and human confirmation",(sim,link)=>
                {
                    link.ApplyPacket(Normal(),0);
                    sim.SetEmergencyStop("A",true);
                    link.Tick(2);
                    Require(link.Silent && sim.Robots.All(r=>r.EmergencyStopped),"Silence did not hold the fleet");
                    Require(!link.ResumeAfterSilence(2),"A stale link resumed");
                    link.ApplyPacket(Normal(2),2.1f);
                    Require(sim.Robots.All(r=>r.EmergencyStopped),"Incoming data auto-resumed the fleet");
                    Require(link.ResumeAfterSilence(2.1f),"Fresh link did not resume after confirmation");
                    Require(sim.Robots.Single(r=>r.RobotId=="A").OperatorStopped &&
                        sim.Robots.Where(r=>r.RobotId!="A").All(r=>!r.EmergencyStopped),"Recovery cleared the wrong stops");
                    link.Tick(4);
                    link.ApplyPacket(Packet(3,State("B","Hold","Warning","Untrusted")),4.1f);
                    Require(link.ResumeAfterSilence(4.1f) && sim.Robots.Single(r=>r.RobotId=="B").LinkedStopped,
                        "Transport recovery released an explicit sensor Hold");
                });
                Case("packet after gap cannot bypass watchdog",(sim,link)=>
                {
                    link.ApplyPacket(Normal(),0);
                    link.ApplyPacket(Normal(2),2);
                    Require(link.Silent && sim.Robots.All(r=>r.EmergencyStopped),"A late packet bypassed silence Hold");
                });
                Case("replay and competing boot rejected",(sim,link)=>
                {
                    link.ApplyPacket(Normal(),0);
                    Require(!link.ApplyPacket(Normal(),1),"Duplicate sequence accepted");
                    Require(!link.ApplyPacket(Normal(1,"other-run"),1),"A competing boot replaced a live link");
                    link.Tick(2);
                    Require(link.Silent,"Rejected packets kept a link fresh");
                    Require(link.ApplyPacket(Normal(1,"other-run"),2.1f) && link.Silent,"Restart bypassed human recovery");
                });
                Case("invalid snapshot rejected atomically",(sim,link)=>
                {
                    link.ApplyPacket(Normal(),0);
                    string[] invalid={"not-json","{}",Normal(2).Replace("\"v\":1","\"v\":2"),
                        Packet(2,State("B","Normal","Warning")),Packet(2,State("B","Normal",trust:"Untrusted")),
                        Packet(2,State("B","Warning")+","+State("Z","Normal")),
                        Packet(2,State("B","Warning")+","+State("B","Normal")),Packet(2,State("B","9")),
                        new string('x',GuardianNetworkBridge.MaximumLineLength+1)};
                    foreach (string json in invalid) Require(!link.ApplyPacket(json,.5f),"Malformed packet accepted: "+json.Substring(0,Math.Min(80,json.Length)));
                    Require(sim.Robots.Single(r=>r.RobotId=="B").Condition==RobotCondition.Normal,"Partial invalid snapshot changed B");
                });
                Case("scenario reset retains sensor state and silence",(sim,link)=>
                {
                    link.ApplyPacket(Packet(1,State("B","Limited")),0);
                    sim.ResetScenario();
                    Require(sim.Robots.Single(r=>r.RobotId=="B").Condition==RobotCondition.Limited,"Reset discarded sensor capability");
                    link.Tick(2);
                    sim.ResetScenario();
                    Require(sim.Robots.All(r=>r.EmergencyStopped),"Reset bypassed silence Hold");
                });
                Case("Japanese recovery rejects a sensor hold",(sim,link)=>
                {
                    link.ApplyPacket(Packet(1,State("B","Hold","Normal","Untrusted")),0);
                    var conversation=new GuardianLegacyRuleConversation(sim);
                    Require(conversation.Submit("B 復帰").Contains("センサー"),"Sensor hold was not explained to the operator");
                    sim.SetEmergencyStop("B",true);
                    conversation.Submit("B 復帰"); conversation.Submit("実行");
                    Require(conversation.ExecutedCommands==0 && sim.Robots.Single(r=>r.RobotId=="B").EmergencyStopped,"Japanese recovery bypassed Hold");
                });
                Case("real loopback TCP framing and shutdown",(sim,link)=>TcpCheck(sim,link,false));
                Case("fallback when another listener occupies primary port",(sim,link)=>TcpCheck(sim,link,true));
                Debug.Log("[Guardian] GUARDIAN_SENSOR_LINK_OK: "+passed+" cases");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene,true);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            }
        }

        private static void Case(string label,Action<GuardianSimulator,GuardianNetworkBridge> test)
        {
            var root=new GameObject("Sensor test: "+label);
            try
            {
                var sim=root.AddComponent<GuardianSimulator>();
                sim.DeveloperMode=true;sim.OperatorSupervisionEnabled=false;
                sim.Settings.Traffic=GuardianTrafficStyle.SingleTrackAisle;sim.Settings.BusyOrders=false;sim.Settings.Diners=false;sim.Settings.KeepServing=false;
                sim.AutomaticTasks=false; sim.Settings.RandomFaults=false;
                sim.EnsureWorld(); sim.ResetScenario();
                var link=root.AddComponent<GuardianNetworkBridge>();
                test(sim,link);
                passed++;
                Debug.Log("[Guardian sensor] PASS: "+label);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void WaitFor(Func<bool> ready,string label)
        {
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow<deadline) { if (ready()) return; Thread.Sleep(10); }
            throw new TimeoutException(label);
        }

        private static void TcpCheck(GuardianSimulator sim,GuardianNetworkBridge link,bool fallback)
        {
            var listener=new TcpListener(IPAddress.Loopback,0);
            listener.Start();
            int port=((IPEndPoint)listener.LocalEndpoint).Port;
            TcpListener occupied=null;
            if (fallback)
            {
                occupied=new TcpListener(IPAddress.Loopback,0); occupied.Start();
                link.Port=((IPEndPoint)occupied.LocalEndpoint).Port; link.FallbackPort=port;
            }
            else { link.Port=port; link.FallbackPort=0; }
            try
            {
                link.StartReceiving();
                WaitFor(()=>listener.Pending(),"Unity did not connect to the relay");
                using (TcpClient peer=listener.AcceptTcpClient())
                {
                    NetworkStream stream=peer.GetStream();
                    byte[] first=Encoding.UTF8.GetBytes(Normal());
                    stream.Write(first,0,first.Length/2);
                    link.Tick(0);
                    Require(!link.HasSnapshot,"Partial TCP line was applied");
                    stream.Write(first,first.Length/2,first.Length-first.Length/2);
                    stream.WriteByte((byte)'\n');
                    WaitFor(()=>{link.Tick(0);return link.HasSnapshot;},"TCP snapshot not applied");
                    byte[] next=Encoding.UTF8.GetBytes(new string('x',GuardianNetworkBridge.MaximumLineLength+1)+"\n"+Packet(2,State("B","Limited"))+"\r\n");
                    stream.Write(next,0,next.Length);
                    WaitFor(()=>{link.Tick(.1f);return sim.Robots.Single(r=>r.RobotId=="B").Condition==RobotCondition.Limited;},"Valid packet after oversized line was lost");
                    Require(link.RejectedPackets>0,"Oversized line was not rejected");
                }
                link.Tick(2);
                Require(link.Silent,"TCP disconnect did not expire");
                link.StopReceiving();
                Require(!link.Receiving,"Receiver thread survived shutdown");
            }
            finally { link.StopReceiving(); listener.Stop(); occupied?.Stop(); }
        }
    }
}
