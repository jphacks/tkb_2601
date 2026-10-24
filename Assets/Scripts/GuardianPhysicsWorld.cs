using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed class GuardianPhysicsObstacle : MonoBehaviour { public bool Floor; }

    /// <summary>Conservative static collision envelopes, generated only for Physics mode.</summary>
    public sealed class GuardianPhysicsWorld : MonoBehaviour
    {
        private readonly List<Collider> generated=new List<Collider>();
        public IReadOnlyList<Collider> Obstacles=>generated;
        private Transform environment;
        public void Ensure(GuardianSimulator sim,bool active)
        {
            Transform root=sim.transform.Find("Generated 3D World/"+RestaurantVisuals.RootName);
            if (root==null) throw new InvalidOperationException("Restaurant root missing");
            if (environment!=root || generated.Any(c=>c==null) || generated.Count==0)
            {
                foreach (Collider c in generated) if (c!=null) Destroy(c);
                generated.Clear(); environment=root;
                foreach (RestaurantFixture fixture in root.GetComponentsInChildren<RestaurantFixture>())
                {
                    if (fixture.BlocksRoutes) Add(fixture.transform,LocalBounds(fixture.transform));
                    else if (fixture.AssetId=="RoomShell")
                    {
                        foreach (Renderer r in fixture.GetComponentsInChildren<Renderer>())
                            if (r.name=="Beige back wall" || r.name=="Beige side wall") Add(r.transform,LocalBounds(r.transform));
                        Transform floor=fixture.transform.Find("Physics floor");
                        if (floor==null) { floor=new GameObject("Physics floor").transform; floor.SetParent(fixture.transform,false); }
                        var c=Add(floor,new Bounds(new Vector3(0,-.01f,0),new Vector3(20,.05f,12.5f)));
                        c.GetComponent<GuardianPhysicsObstacle>().Floor=true;
                    }
                }
            }
            foreach (Collider c in generated) if (c!=null) c.enabled=active;
        }
        private BoxCollider Add(Transform owner,Bounds bounds)
        {
            var box=owner.gameObject.AddComponent<BoxCollider>(); box.center=bounds.center; box.size=bounds.size;
            box.contactOffset=.015f;
            var marker=owner.GetComponent<GuardianPhysicsObstacle>()??owner.gameObject.AddComponent<GuardianPhysicsObstacle>();
            generated.Add(box); return box;
        }
        public static Bounds LocalBounds(Transform root)
        {
            bool first=true; Bounds result=default;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.GetComponent<TextMesh>()!=null) continue;
                Bounds b=renderer.bounds;
                for (int x=-1;x<=1;x+=2) for (int y=-1;y<=1;y+=2) for (int z=-1;z<=1;z+=2)
                {
                    Vector3 p=root.InverseTransformPoint(b.center+Vector3.Scale(b.extents,new Vector3(x,y,z)));
                    if (first) { result=new Bounds(p,Vector3.zero); first=false; } else result.Encapsulate(p);
                }
            }
            if (first) throw new InvalidOperationException("No physical visual bounds "+root.name);
            return result;
        }
    }
}
