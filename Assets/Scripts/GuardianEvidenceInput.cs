using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuardianInputOrigin { Mock, Measured }
    // Timestamp is acquisition time mapped to the receiver's monotonic clock.
    // Device clocks/units require an explicit calibrated adapter; no device or transport is assumed.
    public struct GuardianEvidenceSample {public double Timestamp;public string Source,Metric,Unit,Clock;public float Value;public bool Valid;public GuardianInputOrigin Origin;}
    public interface IGuardianEvidenceAdapter {bool TryRead(out string robotId,out GuardianEvidenceSample sample);}
    public sealed class GuardianEvidenceChannel
    {
        public string RobotId,Source="mock";public GuardianInputOrigin Origin=GuardianInputOrigin.Mock;
        public string Metric="condition-index",Unit="1";
        public bool Connected=true,Calibrated=true;public float RawMin=0,RawMax=1,Warning=.3f,Limited=.7f,MockValue=.12f;
        public double LastSourceTime=double.NegativeInfinity,ReceivedAt=double.NegativeInfinity;
        public string Status="No sample";public bool Fresh;public float LastValue;
        internal GuardianMotionInterlock Hold;internal GuardianStatusIndicator Indicator;
        internal bool UnavailableNotified;
        /// <summary>The mock producer sends nothing, as if the sensor wire were pulled (presentation mode).</summary>
        public bool Silenced;
        /// <summary>
        /// GuardianNetworkBridge sets this robot's state. The mock producer sends nothing for it, so a mock value left
        /// from before the link cannot fight the linked state, and going quiet is the link's to handle (it holds the robots).
        /// </summary>
        public bool Linked;
    }
    public sealed class GuardianEvidenceInput
    {
        public const string ReceiverClock="unity-realtime-seconds";
        public const double MaximumAgeSeconds=1.5;
        /// <summary>
        /// The receiver's monotonic clock: real time, unless a film is being recorded. Recording slows the frames
        /// down in real time, and that must not read as a silent sensor, so the film supplies its frame clock.
        /// </summary>
        public static Func<double> ClockOverride;
        public static double ReceiverNow=>ClockOverride!=null?ClockOverride():Time.realtimeSinceStartupAsDouble;
        public bool NotifyOperator;
        /// <summary>Samples refused by the contract check (forged, replayed, stale, mismatched), and an event per refusal (robot, status).</summary>
        public int Rejected {get;private set;}
        public event Action<string,string> SampleRejected;
        private GuardianSimulator sim;private double nextMock;private readonly Dictionary<string,GuardianEvidenceChannel> channels=new Dictionary<string,GuardianEvidenceChannel>();
        public IReadOnlyDictionary<string,GuardianEvidenceChannel> Channels=>channels;
        public void Initialize(GuardianSimulator simulator,bool preserveSources=false)
        {
            sim=simulator;var previous=preserveSources?new Dictionary<string,GuardianEvidenceChannel>(channels):null;channels.Clear();nextMock=0;Rejected=0;
            foreach(var r in sim.Robots)
            {
                var hold=r.GetComponent<GuardianMotionInterlock>()??r.gameObject.AddComponent<GuardianMotionInterlock>();r.PhysicsDriver.AttachSafetyInterlock(hold);
                var c=previous!=null && previous.TryGetValue(r.RobotId,out var old)?old:new GuardianEvidenceChannel{RobotId=r.RobotId};
                c.Hold=hold;c.Indicator=r.GetComponent<GuardianStatusIndicator>();c.Fresh=false;c.ReceivedAt=double.NegativeInfinity;c.UnavailableNotified=false;
                if(c.Origin==GuardianInputOrigin.Mock){c.MockValue=r.Vibration;c.Warning=sim.Settings.WarningThreshold;c.Limited=sim.Settings.LimitedThreshold;hold.ResetForScenario(sim.DemoClock);}
                else hold.Set(GuardianMotionHold.InputUnavailable,true,sim.DemoClock);
                channels.Add(r.RobotId,c);
            }
        }
        public bool SetMockValue(string id,float value)
        {if(!channels.TryGetValue(id,out var c) || c.Origin!=GuardianInputOrigin.Mock || !float.IsFinite(value))return false;c.MockValue=value;return true;}
        public void SelectSource(string id,GuardianInputOrigin origin,string source)
        {
            var c=channels[id];c.Origin=origin;c.Source=source;c.Connected=origin==GuardianInputOrigin.Mock;c.Calibrated=origin==GuardianInputOrigin.Mock;
            c.LastSourceTime=c.ReceivedAt=double.NegativeInfinity;c.Fresh=false;c.Status=origin==GuardianInputOrigin.Mock?"Mock awaiting sample":"Measured adapter NOT CONNECTED / NOT CALIBRATED";ApplyHold(c,true);
        }
        private void ApplyHold(GuardianEvidenceChannel c,bool blocked)
        {
            c.Hold.Set(GuardianMotionHold.InputUnavailable,blocked,sim.DemoClock);if(c.Indicator!=null)c.Indicator.EvidenceUnknown=blocked;
            if(blocked && !c.UnavailableNotified && NotifyOperator)sim.NotifyDemoInputUnavailable(sim.Robots.First(r=>r.RobotId==c.RobotId),c.Status);
            c.UnavailableNotified=blocked;
        }
        public bool Submit(string id,GuardianEvidenceSample sample,double receivedNow)
        {
            if(string.IsNullOrWhiteSpace(id) || !channels.TryGetValue(id,out var c))return false;
            bool good=c.Connected && c.Calibrated && sample.Valid && sample.Origin==c.Origin && sample.Source==c.Source && !string.IsNullOrWhiteSpace(c.Source)
                && sample.Metric==c.Metric && sample.Unit==c.Unit && !string.IsNullOrWhiteSpace(c.Metric) && !string.IsNullOrWhiteSpace(c.Unit) && sample.Clock==ReceiverClock
                && double.IsFinite(sample.Timestamp) && sample.Timestamp>c.LastSourceTime && double.IsFinite(receivedNow) && receivedNow>=c.ReceivedAt
                && sample.Timestamp>=0 && sample.Timestamp<=receivedNow && receivedNow-sample.Timestamp<=MaximumAgeSeconds
                && float.IsFinite(sample.Value) && float.IsFinite(c.RawMin) && float.IsFinite(c.RawMax) && c.RawMax>c.RawMin
                && sample.Value>=c.RawMin && sample.Value<=c.RawMax && c.Warning>0 && c.Limited<=1 && c.Limited>c.Warning;
            if(!good) {c.Fresh=false;c.Status="Invalid, missing, stale or mismatched evidence contract";ApplyHold(c,true);Rejected++;SampleRejected?.Invoke(id,c.Status);return false;}
            c.LastSourceTime=sample.Timestamp;c.ReceivedAt=receivedNow;c.LastValue=sample.Value;c.Fresh=true;c.Status=c.Origin==GuardianInputOrigin.Mock?"MOCK / simulated":"MEASURED / external adapter claim";ApplyHold(c,false);
            return sim.SubmitCalibratedEvidence(id,(sample.Value-c.RawMin)/(c.RawMax-c.RawMin),c.Warning,c.Limited);
        }
        /// <summary>A packet from the link named this robot: from now on its state comes from the link only.</summary>
        internal void Link(string id,double receivedNow)
        {
            if(!channels.TryGetValue(id,out var c))return;
            c.Linked=true;c.ReceivedAt=receivedNow;c.Fresh=true;c.Status="LINKED / Robomori link";ApplyHold(c,false);
        }
        public void Tick(double monotonicNow)
        {
            if(!double.IsFinite(monotonicNow)){foreach(var c in channels.Values){c.Fresh=false;c.Status="Invalid receiver clock";ApplyHold(c,true);}return;}
            if(monotonicNow>=nextMock)
            {
                nextMock=monotonicNow+.2;
                foreach(var c in channels.Values)if(c.Origin==GuardianInputOrigin.Mock && c.Connected && !c.Silenced && !c.Linked)
                    Submit(c.RobotId,new GuardianEvidenceSample{Timestamp=monotonicNow,Source=c.Source,Metric=c.Metric,Unit=c.Unit,Clock=ReceiverClock,Value=c.MockValue,Valid=true,Origin=GuardianInputOrigin.Mock},monotonicNow);
            }
            foreach(var c in channels.Values)
                if(c.Linked){c.Fresh=monotonicNow>=c.ReceivedAt && monotonicNow-c.ReceivedAt<=MaximumAgeSeconds;c.Status=c.Fresh?"LINKED / Robomori link":"LINK SILENT / the link holds the robot";}
                else if(!c.Connected || !c.Calibrated || !c.Fresh || monotonicNow-c.ReceivedAt>MaximumAgeSeconds || monotonicNow<c.ReceivedAt
                    || monotonicNow-c.LastSourceTime>MaximumAgeSeconds || monotonicNow<c.LastSourceTime)
                {c.Fresh=false;if(monotonicNow-c.ReceivedAt>MaximumAgeSeconds || monotonicNow-c.LastSourceTime>MaximumAgeSeconds)c.Status="STALE / no trusted reading";ApplyHold(c,true);}
        }
        // Adapters must return immediately and never read hardware on the Unity thread.
        // This bounded drain accepts the same envelope used by the mock producer.
        public int Poll(IGuardianEvidenceAdapter adapter,double receivedNow)
        {
            if(adapter==null)return 0;int accepted=0;
            try {for(int i=0;i<32 && adapter.TryRead(out string id,out var sample);i++)if(Submit(id,sample,receivedNow))accepted++;}
            catch(Exception){foreach(var c in channels.Values){c.Fresh=false;c.Status="Adapter unavailable";ApplyHold(c,true);}}
            return accepted;
        }
    }
}
