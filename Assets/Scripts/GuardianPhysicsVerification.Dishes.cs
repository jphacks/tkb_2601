using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed partial class GuardianPhysicsVerification
    {
        private sealed class DishSnapshot
        { public int Instance,Parent; public bool Active; public GuardianDishOwner Owner; }
        private readonly Dictionary<string,DishSnapshot> dishSnapshots=new Dictionary<string,DishSnapshot>();
        private readonly HashSet<int> checkedBodies=new HashSet<int>();
        private int dishEventCursor,verifiedPickups,verifiedReturns,envelopeCorners;
        private float maximumEnvelopeExcess;
        private void ResetDishVisibilityChecks() { dishSnapshots.Clear(); dishEventCursor=0; checkedBodies.Clear(); }
        private void VerifyDishVisibility()
        {
            var inventory=sim.Inventory;
            if(inventory.Items.Any(p=>dishSnapshots.TryGetValue(p.Key,out var old) && old.Instance!=p.Value.GetInstanceID()))
            { dishSnapshots.Clear(); dishEventCursor=0; }
            var moves=inventory.Events.Skip(dishEventCursor).Where(e=>e.Action=="move").ToArray();
            var moved=new HashSet<string>(moves.Select(e=>e.Id));
            foreach(var e in moves)
            {
                RecordTrayHandoff(e);
                var item=inventory.Items[e.Id];
                if(e.InstanceId!=item.GetInstanceID()) throw new InvalidOperationException("Dish instance changed across handoff "+e.Id);
                if(e.From==GuardianDishOwner.Table && e.To==GuardianDishOwner.Robot)
                {
                    var robot=sim.Robots.Single(r=>r.RobotId==e.ToKey);
                    if(!e.Active || !e.RenderVisible || item.transform.parent!=robot.GetComponent<GuardianCargoTray>().Anchors[item.TraySlot] || e.ParentName!=item.transform.parent.name)
                        throw new InvalidOperationException("Picked dish did not leave table visibly on carrier "+e.Id);
                    VerifyMeshEnvelope(robot,item.transform); verifiedPickups++;
                    foreach(var p in dishSnapshots.Where(p=>p.Key!=e.Id && !moved.Contains(p.Key) && p.Value.Owner==GuardianDishOwner.Table))
                    {
                        var other=inventory.Items[p.Key];
                        if(other.GetInstanceID()!=p.Value.Instance || other.transform.parent.GetInstanceID()!=p.Value.Parent
                            || other.gameObject.activeInHierarchy!=p.Value.Active)
                            throw new InvalidOperationException("Unrelated table dish changed at pickup "+p.Key);
                    }
                }
                if(e.From==GuardianDishOwner.Robot && e.To==GuardianDishOwner.ReturnCounter)
                {
                    if(e.Active || e.RenderVisible || item.gameObject.activeInHierarchy || e.ParentName!=item.transform.parent.name)
                        throw new InvalidOperationException("Returned dish remained visible or lost its counter parent "+e.Id);
                    verifiedReturns++;
                }
            }
            dishEventCursor=inventory.Events.Count;
            foreach(var p in inventory.Items)
                dishSnapshots[p.Key]=new DishSnapshot{Instance=p.Value.GetInstanceID(),Parent=p.Value.transform.parent.GetInstanceID(),
                    Active=p.Value.gameObject.activeInHierarchy,Owner=p.Value.Owner};
        }
        private void VerifyMeshEnvelope(GuardianRobot robot,Transform root)
        {
            var shape=robot.PhysicsDriver.Shape;
            foreach(var renderer in root.GetComponentsInChildren<Renderer>())
            {
                Bounds bounds;
                if(renderer is SkinnedMeshRenderer skin) bounds=skin.localBounds;
                else
                {
                    var filter=renderer.GetComponent<MeshFilter>();
                    if(filter==null || filter.sharedMesh==null) continue;
                    bounds=filter.sharedMesh.bounds;
                }
                for(int x=-1;x<=1;x+=2) for(int y=-1;y<=1;y+=2) for(int z=-1;z<=1;z+=2)
                {
                    Vector3 point=robot.transform.InverseTransformPoint(renderer.transform.TransformPoint(bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z))));
                    Vector3 excess=point-shape.center; excess=new Vector3(Mathf.Abs(excess.x),Mathf.Abs(excess.y),Mathf.Abs(excess.z))-shape.size*.5f;
                    float outside=Mathf.Max(0,excess.x,excess.y,excess.z); maximumEnvelopeExcess=Mathf.Max(maximumEnvelopeExcess,outside); envelopeCorners++;
                    if(outside>.0001f) throw new InvalidOperationException("Body/cargo mesh bounds exceed collision envelope "+robot.RobotId+" / "+renderer.name+" by "+outside);
                }
            }
        }
    }
}
