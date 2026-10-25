using System;
using System.Linq;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianSimulator
    {
        public int ScenarioRevision {get;private set;}
        private GuardianLegacyPedestrianSystem legacyPedestrian;
        private bool legacyInteraction;

        private void ConfigurePublicDemonstrations()
        {
            var args=Environment.GetCommandLineArgs();
            legacyInteraction=args.Contains("-guardianInteractionDemo");
            if(args.Contains("-guardianPedestrianDemo"))Settings.PedestrianDemoEnabled=true;
            if(!legacyInteraction && !Settings.PedestrianDemoEnabled)return;
            DeveloperMode=true;OperatorSupervisionEnabled=false;_regressionBaseline=true;
            Settings.Traffic=GuardianTrafficStyle.SingleTrackAisle;
            Settings.BusyOrders=Settings.Diners=Settings.WalkingGuests=Settings.KeepServing=false;
        }

        private void ConfigureLegacyPedestrian()
        {
            legacyPedestrian=GetComponent<GuardianLegacyPedestrianSystem>();
            if(legacyPedestrian==null && Settings.PedestrianDemoEnabled)
                legacyPedestrian=gameObject.AddComponent<GuardianLegacyPedestrianSystem>();
            if(legacyPedestrian!=null && legacyPedestrian.Configure(this,Mode==GuardianSimulationMode.Physics && Settings.PedestrianDemoEnabled))
            { _physicsWorld.Invalidate();_physicsWorld.Ensure(this,Mode==GuardianSimulationMode.Physics); }
            if(legacyInteraction)
                (GetComponent<GuardianInteractionPanel>()??gameObject.AddComponent<GuardianInteractionPanel>()).Initialize(this);
        }
    }
}
