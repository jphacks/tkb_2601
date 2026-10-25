using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Runtime anchors derived from the loaded model's separate thin shelf meshes. No private mesh data is serialized.</summary>
    public sealed class GuardianCargoTray : MonoBehaviour
    {
        private readonly List<Bounds> shelves=new List<Bounds>();
        private readonly List<Transform> anchors=new List<Transform>();
        public IReadOnlyList<Bounds> Shelves=>shelves;
        public IReadOnlyList<Transform> Anchors=>anchors;
        public bool LocalModel { get; private set; }
        public Vector3 FaceCenter { get; private set; }
        public void Configure()
        {
            if(anchors.Count==GuardianDishInventory.TrayCapacity) return;
            Transform visual=transform.Find(NekobotVisualFactory.VisualRootName);
            LocalModel=visual.Find("Nekobot Model")!=null;
            if(!LocalModel) EnsureOwnedShelves(visual);
            var found=new List<Bounds>();
            foreach(var renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                if(renderer.GetComponent<TextMesh>()!=null) continue;
                if(renderer.name.Equals("face",StringComparison.OrdinalIgnoreCase) || renderer.name=="Cat Face") FaceCenter=RendererBounds(transform,renderer).center;
                foreach(Bounds b in Components(transform,renderer))
                    if(b.size.x>.5f && b.size.z>.25f && b.size.y<.055f && b.size.y>.002f && b.size.x<1.2f && b.size.z<.85f)
                        found.Add(b);
            }
            foreach(var b in found.OrderBy(b=>b.max.y))
                if(!shelves.Any(s=>Mathf.Abs(s.max.y-b.max.y)<.06f)) shelves.Add(b);
            if(shelves.Count!=GuardianDishInventory.TrayCapacity)
                throw new InvalidOperationException("Expected four separate usable cargo shelves on "+name+"; found="+shelves.Count);
            if(LocalModel && FaceCenter.z<.05f) throw new InvalidOperationException("Loaded model front/rear axis is not the expected +Z/-Z");
            var root=new GameObject("Rear Tray Anchors").transform; root.SetParent(transform,false);
            foreach(var b in shelves)
            {
                var anchor=new GameObject("Rear Tray "+(anchors.Count+1)).transform; anchor.SetParent(root,false);
                anchor.localPosition=new Vector3(b.center.x,b.max.y,b.center.z-.008f); anchors.Add(anchor);
            }
        }
        private static void EnsureOwnedShelves(Transform visual)
        {
            if(visual.Find("Guardian Rear Shelf 1")!=null) return;
            // Original fallback geometry: an open rear rack instead of a solid
            // block through its cargo. Upgrade old saved scenes at runtime too.
            var body=visual.Find("Body"); if(body!=null){body.localPosition=new Vector3(0,.91f,.27f);body.localScale=new Vector3(.78f,1.08f,.12f);}
            var face=visual.Find("Cat Face"); if(face!=null){face.localPosition=new Vector3(0,1.64f,.28f);face.localScale=new Vector3(.75f,.53f,.19f);}
            var old=visual.Find("Delivery Tray"); if(old!=null){old.gameObject.SetActive(false);old.SetParent(null,false);Destroy(old.gameObject);}
            for(int i=0;i<4;i++) GuardianSimulator.CreatePart(visual,"Guardian Rear Shelf "+(i+1),PrimitiveType.Cube,
                new Vector3(0,.54f+i*.28f,-.02f),new Vector3(.78f,.025f,.62f),new Color(.72f,.80f,.87f));
            foreach(int side in new[]{-1,1}) GuardianSimulator.CreatePart(visual,"Guardian Rear Rack Post "+side,PrimitiveType.Cube,
                new Vector3(side*.37f,.96f,-.02f),new Vector3(.04f,.85f,.62f),Color.white);
        }
        public void Mount(Transform cargo,int slot,float? fixedScale=null)
        {
            if(slot<0 || slot>=anchors.Count) throw new InvalidOperationException("Invalid physical tray slot");
            cargo.localScale=Vector3.one; Bounds b=VisualBounds(cargo); Bounds shelf=shelves[slot];
            float scale=Mathf.Min(1,(shelf.size.x-.04f)/b.size.x,(shelf.size.z-.04f)/b.size.z);
            if(slot+1<shelves.Count)scale=Mathf.Min(scale,(shelves[slot+1].min.y-shelf.max.y-.02f)/b.size.y);
            if(scale<.5f) throw new InvalidOperationException("Cargo cannot fit a real tray shelf");
            if(fixedScale.HasValue)
            {
                if(fixedScale.Value>scale+.00001f || fixedScale.Value<=0) throw new InvalidOperationException("Fixed physical dish size does not fit the shelf");
                scale=fixedScale.Value;
            }
            cargo.SetParent(anchors[slot],false); cargo.localRotation=Quaternion.identity; cargo.localScale=Vector3.one*scale;
            cargo.localPosition=new Vector3(-b.center.x*scale,.003f-b.min.y*scale,-b.center.z*scale);
            ValidateMount(cargo,slot);
        }
        public void ValidateMount(Transform cargo,int slot)
        {
            if(slot<0 || slot>=anchors.Count || cargo.parent!=anchors[slot]) throw new InvalidOperationException("Cargo lost its physical tray anchor");
            Bounds local=VisualBounds(cargo),actual=default; bool first=true;
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
            {
                var p=transform.InverseTransformPoint(cargo.TransformPoint(local.center+Vector3.Scale(local.extents,new Vector3(x,y,z))));
                if(first){actual=new Bounds(p,Vector3.zero);first=false;}else actual.Encapsulate(p);
            }
            Bounds support=shelves[slot];
            if(actual.min.x<support.min.x+.009f || actual.max.x>support.max.x-.009f || actual.min.z<support.min.z+.009f || actual.max.z>support.max.z-.009f
                || Mathf.Abs(actual.min.y-support.max.y-.003f)>.0005f
                || (slot+1<shelves.Count && actual.max.y>shelves[slot+1].min.y-.009f))
                throw new InvalidOperationException("Cargo exceeds shelf support/height on "+name+" slot="+slot);
        }
        public static Bounds VisualBounds(Transform root)
        {
            bool first=true; Bounds result=default;
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if(renderer.GetComponent<TextMesh>()!=null) continue;
                Bounds b=RendererBounds(root,renderer);
                if(first){result=b;first=false;}else result.Encapsulate(b);
            }
            if(first) throw new InvalidOperationException("Cargo visual has no mesh"); return result;
        }
        public void ValidateSurfaceSeparation(Transform cargo)
        {
            // Test-time conservative triangle/AABB SAT: a dish's complete mesh
            // bounds must not intersect any shelf/chassis rendered surface.
            // This reads local CPU geometry only and serializes no vertices.
            Bounds local=VisualBounds(cargo),box=default; bool first=true;
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
            {
                Vector3 p=transform.InverseTransformPoint(cargo.TransformPoint(local.center+Vector3.Scale(local.extents,new Vector3(x,y,z))));
                if(first){box=new Bounds(p,Vector3.zero);first=false;}else box.Encapsulate(p);
            }
            foreach(var renderer in transform.Find(NekobotVisualFactory.VisualRootName).GetComponentsInChildren<Renderer>(true))
            {
                if(renderer.GetComponent<TextMesh>()!=null)continue;
                bool baked=renderer is SkinnedMeshRenderer; Mesh mesh;
                if(baked){mesh=new Mesh();((SkinnedMeshRenderer)renderer).BakeMesh(mesh);}else mesh=renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if(mesh==null)continue;
                try
                {
                    var points=mesh.vertices.Select(v=>transform.InverseTransformPoint(renderer.transform.TransformPoint(v))).ToArray(); var triangles=mesh.triangles;
                    for(int i=0;i<triangles.Length;i+=3)
                        if(TriangleIntersects(box,points[triangles[i]],points[triangles[i+1]],points[triangles[i+2]]))
                            throw new InvalidOperationException("Cargo mesh bounds intersect a shelf/chassis surface: "+name+" / "+renderer.name);
                }
                finally{if(baked)Destroy(mesh);}
            }
        }
        private static bool TriangleIntersects(Bounds box,Vector3 a,Vector3 b,Vector3 c)
        {
            a-=box.center;b-=box.center;c-=box.center;
            Vector3 e0=b-a,e1=c-b,e2=a-c,half=box.extents;
            bool Separated(Vector3 axis)
            {
                if(axis.sqrMagnitude<1e-14f)return false; axis.Normalize();
                float pa=Vector3.Dot(a,axis),pb=Vector3.Dot(b,axis),pc=Vector3.Dot(c,axis);
                float radius=half.x*Mathf.Abs(axis.x)+half.y*Mathf.Abs(axis.y)+half.z*Mathf.Abs(axis.z);
                return Mathf.Min(pa,pb,pc)>radius+.00001f || Mathf.Max(pa,pb,pc)<-radius-.00001f;
            }
            if(Separated(Vector3.right)||Separated(Vector3.up)||Separated(Vector3.forward)||Separated(Vector3.Cross(e0,e1)))return false;
            foreach(var edge in new[]{e0,e1,e2})
                if(Separated(Vector3.Cross(edge,Vector3.right))||Separated(Vector3.Cross(edge,Vector3.up))||Separated(Vector3.Cross(edge,Vector3.forward)))return false;
            return true;
        }
        private static Bounds RendererBounds(Transform frame,Renderer renderer)
        {
            Bounds source;
            if(renderer is SkinnedMeshRenderer skin) source=skin.localBounds;
            else { var filter=renderer.GetComponent<MeshFilter>(); if(filter==null||filter.sharedMesh==null) return default; source=filter.sharedMesh.bounds; }
            bool first=true; Bounds result=default;
            for(int x=-1;x<=1;x+=2) for(int y=-1;y<=1;y+=2) for(int z=-1;z<=1;z+=2)
            {
                Vector3 point=frame.InverseTransformPoint(renderer.transform.TransformPoint(source.center+Vector3.Scale(source.extents,new Vector3(x,y,z))));
                if(first){result=new Bounds(point,Vector3.zero);first=false;}else result.Encapsulate(point);
            }
            return result;
        }
        private static List<Bounds> Components(Transform frame,Renderer renderer)
        {
            Mesh mesh=null; bool baked=false;
            if(renderer is SkinnedMeshRenderer skin){ mesh=new Mesh(); skin.BakeMesh(mesh); baked=true; }
            else mesh=renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var result=new List<Bounds>(); if(mesh==null) return result;
            if(!mesh.isReadable) throw new InvalidOperationException("Tray discovery requires readable local CPU geometry: "+renderer.name);
            Vector3[] vertices=mesh.vertices; int[] triangles=mesh.triangles; var parents=Enumerable.Range(0,vertices.Length).ToArray();
            int Find(int n){while(parents[n]!=n){parents[n]=parents[parents[n]];n=parents[n];}return n;}
            void Join(int a,int b){parents[Find(a)]=Find(b);}
            var weld=new Dictionary<Vector3Int,int>(); var points=new Vector3[vertices.Length];
            for(int i=0;i<vertices.Length;i++)
            {
                points[i]=frame.InverseTransformPoint(renderer.transform.TransformPoint(vertices[i]));
                Vector3 p=points[i]*100000; var key=new Vector3Int(Mathf.RoundToInt(p.x),Mathf.RoundToInt(p.y),Mathf.RoundToInt(p.z));
                if(weld.TryGetValue(key,out int other))Join(i,other);else weld[key]=i;
            }
            for(int i=0;i<triangles.Length;i+=3){Join(triangles[i],triangles[i+1]);Join(triangles[i],triangles[i+2]);}
            var groups=new Dictionary<int,Bounds>();
            for(int i=0;i<points.Length;i++){int key=Find(i);if(groups.TryGetValue(key,out var b)){b.Encapsulate(points[i]);groups[key]=b;}else groups[key]=new Bounds(points[i],Vector3.zero);}
            result.AddRange(groups.Values); if(baked)Destroy(mesh); return result;
        }
    }
}
