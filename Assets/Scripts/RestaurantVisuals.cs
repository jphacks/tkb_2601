using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Guardian.UnitySim
{
    /// <summary>Original Blender-authored kit. Separate from actors and policy.</summary>
    public static class RestaurantVisuals
    {
        public const string RootName = "Restaurant Environment A";
        public static void Build(Transform world)
        {
            if (world.Find(RootName) == null) Create(world, false);
        }

        public static Transform Create(Transform world, bool prototype)
        {
            var catalog = Resources.Load<RestaurantCatalog>("RestaurantCatalog");
            if (catalog == null) throw new InvalidOperationException(
                "RestaurantCatalog missing. Generate restaurant assets in Unity Editor first.");
            Transform root = new GameObject(RootName).transform;
            root.SetParent(world, false);
            try
            {
                Place(catalog, root, "RoomShell", "Room Shell", Vector3.zero);
                root.gameObject.AddComponent<RestaurantLighting>();
                Place(catalog, root, "KitchenRange", "Kitchen Range", new Vector3(-8.75f, 0, 2.7f), -90);
                Place(catalog, root, "KitchenSink", "Kitchen Sink", new Vector3(-8.75f, 0, 4.4f), -90);
                Place(catalog, root, "KitchenPrep", "Kitchen Prep", new Vector3(-7f, 0, 5.5f));
                Place(catalog, root, "PickupCounter", "Food Pickup", new Vector3(-7.55f, 0, 3.6f));
                Place(catalog, root, "ReturnCounter", "Dish Return", new Vector3(-7.65f, 0, -3.6f));
                Place(catalog, root, "BoothBench", "Sofa Booth 1", new Vector3(2.9f, 0, 5.55f), 180);
                if (!prototype) Place(catalog, root, "BoothBench", "Sofa Booth 2", new Vector3(6f, 0, 5.55f), 180);
                float[] rows = prototype ? new[] { 0f } : new[] { -3.4f, 0f, 3.4f };
                for (int i = 0; i < rows.Length; i++)
                {
                    float z = rows[i];
                    Transform table = Place(catalog, root, "DiningTable", "Dining Table " + (i + 1), new Vector3(6.1f, 0, z));
                    for (int side = 0; side < 2; side++)
                    {
                        float x = side == 0 ? 4.8f : 7.5f;
                        for (int seat = 0; seat < 2; seat++)
                            Place(catalog, root, "DiningChair", "Chair " + (i + 1) + "-" + side + "-" + seat,
                                new Vector3(x, 0, z + (seat == 0 ? -.46f : .46f)), side == 0 ? 90 : -90);
                        float settingX = side == 0 ? -.45f : .45f;
                        Place(catalog, table, "Plate", "Place Setting " + side, new Vector3(settingX, .82f, 0));
                        Place(catalog, table, "Cup", "Tea Cup " + side, new Vector3(settingX, .82f, .52f));
                    }
                    Place(catalog, table, "Bowl", "Soup Bowl", new Vector3(-.45f, .855f, 0));
                    Place(catalog, table, "TableDetails", "Menu and Condiments", new Vector3(0,.82f,0));
                }
                if (prototype)
                    SeatGuest(catalog,root,"GuestSage","Guest 1","Chair 1-1-0");
                else
                {
                    SeatGuest(catalog,root,"GuestSage","Guest 1","Chair 1-1-0");
                    SeatGuest(catalog,root,"GuestClay","Guest 2","Chair 1-1-1");
                    SeatGuest(catalog,root,"GuestNavy","Guest 3","Chair 2-1-0");
                    SeatGuest(catalog,root,"GuestSage","Guest 4","Chair 3-1-1");
                }
                Place(catalog,root,"KitchenStaff","Kitchen Staff",new Vector3(-7.55f,0,2.15f),-90);
                return root;
            }
            catch
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(root.gameObject);
                else UnityEngine.Object.DestroyImmediate(root.gameObject);
                throw;
            }
        }

        private static void SeatGuest(RestaurantCatalog catalog,Transform root,string id,string name,string seatName)
        {
            Transform chair=root.Find(seatName);
            if (chair==null) throw new InvalidOperationException("Missing seat "+seatName);
            Transform guest=Place(catalog,root,id,name,chair.localPosition,chair.localEulerAngles.y);
            guest.GetComponent<RestaurantPerson>().Seat=chair.GetComponent<RestaurantFixture>();
        }

        private static Transform Place(RestaurantCatalog catalog, Transform parent, string id,
            string name, Vector3 position, float yaw = 0)
        {
            GameObject instance;
#if UNITY_EDITOR
            instance = !Application.isPlaying
                ? (GameObject)PrefabUtility.InstantiatePrefab(catalog.Get(id), parent)
                : UnityEngine.Object.Instantiate(catalog.Get(id), parent);
#else
            instance = UnityEngine.Object.Instantiate(catalog.Get(id), parent);
#endif
            instance.name = name;
            instance.transform.localPosition = position;
            instance.transform.localRotation = Quaternion.Euler(0, yaw, 0);
            return instance.transform;
        }
    }
}
