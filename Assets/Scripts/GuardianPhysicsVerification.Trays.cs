using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private readonly List<string> trayGeometry=new List<string>{"fleet,actor,slot,asset,shelf_center,shelf_size,scale,cargo_bounds"};
        private readonly List<string> trayHandoffs=new List<string>{"scenario,seed,fleet,clock,id,instance,actor,from,to,yaw,heading_error_deg,position_error_m,speed_mps,parent"};
        private void RecordTrayHandoff(GuardianDishEvent e)
        {
            if(string.IsNullOrEmpty(e.CarrierId)) return;
            if(e.ServiceHeadingError>=3 || e.ServicePositionError>=.075f || e.ServiceSpeed>=Mathf.Sqrt(.025f))
                throw new InvalidOperationException("Invalid rear-facing handoff "+e.Id);
            trayHandoffs.Add(Csv(clearanceCase,sim.Settings.Seed,sim.Robots.Count,e.Clock,e.Id,e.InstanceId,e.CarrierId,e.From,e.To,
                e.CarrierYaw,e.ServiceHeadingError,e.ServicePositionError,e.ServiceSpeed,e.ParentName));
        }
        private static string Csv(params object[] cells)=>string.Join(",",cells.Select(c=>"\""+Convert.ToString(c,CultureInfo.InvariantCulture).Replace("\"","\"\"")+"\""));
        private void SaveTrayEvidence()
        {
            if(trayGeometry.Count>1) File.WriteAllLines(Path.Combine(output,"tray-geometry.csv"),trayGeometry);
            if(trayHandoffs.Count>1) File.WriteAllLines(Path.Combine(output,"service-handoffs.csv"),trayHandoffs);
            report.Add("REAR_TRAY handoffs="+(trayHandoffs.Count-1)+" all physical position<0.075m speed<0.158114m/s rear heading<3deg; GUI/pixels untested");
        }
        private IEnumerator TrayServiceRegression()
        {
            int previousMotion=sim.MotionSpeedMultiplier;
            foreach(bool five in new[]{false,true})
            {
                Reset(2026,true); sim.SetReserveFleet(five); sim.Settings.MealSeconds=600;
                foreach(var r in sim.Robots)
                {
                    var tray=r.GetComponent<GuardianCargoTray>(); int children=r.GetComponentsInChildren<Transform>(true).Count(t=>t.name=="Rear Tray Anchors");
                    tray.Configure();
                    Check(tray.Shelves.Count==4 && tray.Anchors.Count==4 && children==1,"four idempotent rear shelf anchors "+r.RobotId+" fleet="+sim.Robots.Count);
                    var catalog=Resources.Load<RestaurantCatalog>("RestaurantCatalog");
                    foreach(string asset in new[]{"Plate","FoodSoup","FoodCurryRice","FoodNoodles","FoodHamburg"}) for(int slot=0;slot<4;slot++)
                    {
                        var cargo=Instantiate(catalog.Get(asset)); foreach(var c in cargo.GetComponentsInChildren<Collider>(true))c.enabled=false;
                        tray.Mount(cargo.transform,slot,GuardianDishItem.PhysicalScale); tray.ValidateMount(cargo.transform,slot); tray.ValidateSurfaceSeparation(cargo.transform); VerifyMeshEnvelope(r,cargo.transform);
                        trayGeometry.Add(Csv(sim.Robots.Count,r.RobotId,slot,asset,tray.Shelves[slot].center.ToString("F6"),tray.Shelves[slot].size.ToString("F6"),cargo.transform.localScale.x,GuardianCargoTray.VisualBounds(cargo.transform).ToString("F6")));
                        cargo.SetActive(false); Destroy(cargo);
                    }
                }
                foreach(int speed in new[]{1,2})
                {
                    Reset(2026,true); sim.SetReserveFleet(five); sim.TrySetMotionSpeed(speed); sim.Settings.MealSeconds=600;
                    clearanceCase="tray-stop-service-"+speed+"-"+(five?5:3);
                    OnlyActorA(); var a=sim.Robots.Single(r=>r.RobotId=="A");
                    for(int i=0;i<3;i++)TableFixture(0,true);
                    var task=sim.QueueTask(DeliveryKind.EmptyDishes,0,GuardianMenu.CurryRice,"A");
                    yield return Wait(()=>a.Carrying && task.DishIds.Count==4,240,"all four actual dishes occupy separate rear shelves");
                    if(failed)yield break;
                    string[] ids=task.DishIds.ToArray(); var instances=ids.Select(id=>sim.Inventory.Items[id].GetInstanceID()).ToArray();
                    Check(ids.Select(id=>sim.Inventory.Items[id].TraySlot).Distinct().Count()==4,"four-dish batch uses four distinct supports");
                    sim.SetEmergencyStop("A",true); yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
                    var stopped=a.PhysicsDriver.Body.position; var poses=ids.Select(id=>sim.Inventory.Items[id].transform.localPosition).ToArray();
                    sim.TrySetMotionSpeed(speed==1?2:1); yield return new WaitForSeconds(2);
                    Check(Vector3.Distance(stopped,a.PhysicsDriver.Body.position)<.001f && ids.Select((id,n)=>sim.Inventory.Items[id].Owner==GuardianDishOwner.Robot
                        && sim.Inventory.Items[id].GetInstanceID()==instances[n] && sim.Inventory.Items[id].transform.localPosition==poses[n]).All(v=>v),
                        "stopped cargo stays mounted with the same IDs/poses through a speed switch");
                    sim.SetEmergencyStop("A",false);
                    yield return Wait(Drained,600,"source-preserving stop recovery completes the same four dishes and parks");
                    if(failed)yield break;
                    Check(ids.Select((id,n)=>sim.Inventory.Items[id].Owner==GuardianDishOwner.ReturnCounter && sim.Inventory.Items[id].GetInstanceID()==instances[n]).All(v=>v),"Table -> rear trays -> physical source recovery -> counter retains all four instances");
                    SaveServeTrace("tray-"+(five?5:3)+"-"+speed);
                }
                sim.SetMode(GuardianSimulationMode.LegacyStory); yield return new WaitForSeconds(.1f); sim.SetMode(GuardianSimulationMode.Physics);
                Check(sim.Robots.All(r=>r.GetComponentsInChildren<Transform>(true).Count(t=>t.name=="Rear Tray Anchors")==1)
                    && sim.Inventory.Items.Values.All(i=>i.Owner!=GuardianDishOwner.Robot),"Physics/Legacy/Physics reset has one rack per actor and no orphaned cargo");
            }
            sim.TrySetMotionSpeed(previousMotion); report.Add("TRAY_SERVICE_OK: four-shelf geometry, 3/5 fleet, 1x/2x stop/recovery, heading at every handoff, mode reset");
        }
    }
}
