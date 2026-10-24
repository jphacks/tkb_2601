using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Read-only rendered-envelope measurements, usable in Editor and local player.</summary>
    public static class RestaurantClearance
    {
        public static Bounds BoundsOf(Component component)
        {
            Renderer[] renderers=component.GetComponentsInChildren<Renderer>(true).Where(r=>r.GetComponent<TextMesh>()==null && r.enabled && r.gameObject.activeInHierarchy).ToArray();
            if (renderers.Length==0) throw new InvalidOperationException("No renderer: "+component.name);
            Bounds bounds=renderers[0].bounds;
            foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }
        public static float Measure(GuardianSimulator simulator,Dictionary<string,float> minima=null)
        {
            Transform root=simulator.transform.Find("Generated 3D World/"+RestaurantVisuals.RootName);
            if (root==null) throw new InvalidOperationException("Environment missing");
            RestaurantFixture[] fixtures=root.GetComponentsInChildren<RestaurantFixture>();
            float minimum=float.PositiveInfinity;
            foreach (GuardianRobot robot in simulator.GetComponentsInChildren<GuardianRobot>())
            {
                Bounds actor=BoundsOf(robot);
                float robotMinimum=float.PositiveInfinity;
                foreach (RestaurantFixture fixture in fixtures)
                {
                    if (!fixture.BlocksRoutes) continue;
                    Bounds furniture=BoundsOf(fixture);
                    if (actor.Intersects(furniture))
                        throw new InvalidOperationException("Moving robot overlaps furniture: "+robot.RobotId+" / "+fixture.name);
                    float dx=Mathf.Max(0,Mathf.Max(actor.min.x-furniture.max.x,furniture.min.x-actor.max.x));
                    float dz=Mathf.Max(0,Mathf.Max(actor.min.z-furniture.max.z,furniture.min.z-actor.max.z));
                    float gap=Mathf.Sqrt(dx*dx+dz*dz);
                    robotMinimum=Mathf.Min(robotMinimum,gap);
                    if (minima!=null && fixture.GetComponent<RestaurantPerson>()!=null)
                    {
                        string key=robot.RobotId+" / person "+fixture.name;
                        minima[key]=minima.ContainsKey(key)?Mathf.Min(minima[key],gap):gap;
                    }
                }
                minimum=Mathf.Min(minimum,robotMinimum);
                if (minima!=null) minima[robot.RobotId]=minima.ContainsKey(robot.RobotId)?Mathf.Min(minima[robot.RobotId],robotMinimum):robotMinimum;
            }
            return minimum;
        }
    }
}
