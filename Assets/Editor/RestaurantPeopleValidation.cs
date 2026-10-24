using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    public static class RestaurantPeopleValidation
    {
        public static List<string> Validate(Transform root,bool prototype)
        {
            var report=new List<string>();
            RestaurantPerson[] people=root.GetComponentsInChildren<RestaurantPerson>();
            if (people.Count(p=>!p.Standing)!=(prototype?1:4) || people.Count(p=>p.Standing)!=1)
                throw new InvalidOperationException("Unexpected static people count");
            var occupied=new HashSet<RestaurantFixture>();
            for (int i=0;i<people.Length;i++)
            for (int j=i+1;j<people.Length;j++)
                if (RestaurantClearance.BoundsOf(people[i]).Intersects(RestaurantClearance.BoundsOf(people[j])))
                    throw new InvalidOperationException("People overlap: "+people[i].name+" / "+people[j].name);
            foreach (RestaurantPerson person in people)
            {
                Bounds bounds=RestaurantClearance.BoundsOf(person);
                if (Mathf.Abs(bounds.min.y-.015f)>.012f)
                    throw new InvalidOperationException("Person shoes not on floor: "+person.name+" "+bounds);
                if (!person.Standing)
                {
                    if (person.Seat==null || person.Seat.AssetId!="DiningChair" || !occupied.Add(person.Seat))
                        throw new InvalidOperationException("Missing/duplicate support chair: "+person.name);
                    if (Vector3.Distance(person.transform.position,person.Seat.transform.position)>.02f
                        || Quaternion.Angle(person.transform.rotation,person.Seat.transform.rotation)>1)
                        throw new InvalidOperationException("Person not aligned with assigned seat: "+person.name);
                    Renderer pelvis=person.GetComponentsInChildren<Renderer>().Single(r=>r.name=="Pelvis trousers");
                    Renderer cushion=person.Seat.GetComponentsInChildren<Renderer>().Single(r=>r.name=="Sage seat pad");
                    float contact=pelvis.bounds.min.y-cushion.bounds.max.y;
                    if (contact<-.015f || contact>.015f)
                        throw new InvalidOperationException("Pelvis floats above or penetrates cushion: "+person.name+" gap="+contact);
                    report.Add(person.name+" support="+person.Seat.name+" pelvis/cushion gap="+contact.ToString("F4")+"m");
                }
                float allowedDepth=0;
                foreach (RestaurantFixture fixture in root.GetComponentsInChildren<RestaurantFixture>())
                {
                    if (!fixture.BlocksRoutes || fixture.GetComponent<RestaurantPerson>()!=null
                        || !bounds.Intersects(RestaurantClearance.BoundsOf(fixture))) continue;
                    foreach (MeshFilter part in person.GetComponentsInChildren<MeshFilter>())
                    foreach (MeshFilter furnishing in fixture.GetComponentsInChildren<MeshFilter>())
                    {
                        Renderer a=part.GetComponent<Renderer>(), b=furnishing.GetComponent<Renderer>();
                        if (a==null || b==null || !a.bounds.Intersects(b.bounds)) continue;
                        float depth=Penetration(part,furnishing);
                        bool seatContact=fixture==person.Seat && part.name=="Pelvis trousers"
                            && (furnishing.name=="Sage seat pad" || furnishing.name=="Rounded timber seat");
                        if (depth>(seatContact ? .015f : .005f))
                            throw new InvalidOperationException("Person penetrates furniture: "+person.name+" / "+part.name+
                                " / "+fixture.name+" / "+furnishing.name+" depth="+depth);
                        if (seatContact) allowedDepth=Mathf.Max(allowedDepth,depth);
                    }
                }
                report.Add(person.name+" rendererBounds="+bounds+" permitted seat compression="+allowedDepth.ToString("F4")+"m");
            }
            report.Add("PASS: original people floor/seat anchors and actual convex-part furniture penetration checks");
            return report;
        }

        internal static float Penetration(MeshFilter person,MeshFilter furniture)
        {
            GameObject a=null,b=null;
            try
            {
                // The authored chair/desk parts are individually convex. Hulls also
                // catch contained overlaps that triangle-only colliders can miss.
                a=ColliderObject(person,true); b=ColliderObject(furniture,true);
                bool overlap=Physics.ComputePenetration(a.GetComponent<Collider>(),a.transform.position,a.transform.rotation,
                    b.GetComponent<Collider>(),b.transform.position,b.transform.rotation,out Vector3 direction,out float depth);
                return overlap?depth:0;
            }
            finally
            {
                DestroyCollider(a); DestroyCollider(b);
            }
        }
        private static GameObject ColliderObject(MeshFilter source,bool convex)
        {
            var go=new GameObject("Temporary people contact check") { hideFlags=HideFlags.HideAndDontSave };
            // Cook in meter units so FBX unit transforms cannot change tolerances.
            Vector3 center=source.GetComponent<Renderer>().bounds.center;
            go.transform.position=center;
            var mesh=new Mesh { name="Temporary meter-space contact mesh",hideFlags=HideFlags.HideAndDontSave };
            mesh.vertices=source.sharedMesh.vertices.Select(v=>source.transform.TransformPoint(v)-center).ToArray();
            mesh.triangles=source.sharedMesh.triangles; mesh.RecalculateBounds(); mesh.RecalculateNormals();
            var collider=go.AddComponent<MeshCollider>();
            collider.sharedMesh=mesh; collider.convex=convex;
            return go;
        }
        private static void DestroyCollider(GameObject go)
        {
            if (go==null) return;
            Mesh mesh=go.GetComponent<MeshCollider>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(go);
            if (mesh!=null) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
