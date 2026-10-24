using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    public static class RestaurantAssetPipeline
    {
        public const string Root = "Assets/Art/Restaurant";
        private static readonly string[] Ids = { "DiningTable", "DiningChair", "BoothBench", "KitchenPrep",
            "KitchenSink", "KitchenRange", "PickupCounter", "ReturnCounter", "Plate", "Bowl", "Cup", "RoomShell", "TableDetails",
            "GuestSage","GuestClay","GuestNavy","KitchenStaff","FoodSoup","FoodCurryRice","FoodNoodles","FoodHamburg" };
        private struct Surface
        {
            public string Name; public Color Color; public float Metal, Smooth;
            public Surface(string n, float r, float g, float b, float metal, float smooth)
            { Name = n; Color = new Color(r, g, b); Metal = metal; Smooth = smooth; }
        }
        private static readonly Surface[] Surfaces = {
            new Surface("Oak", .68f,.46f,.27f,0,.42f), new Surface("Walnut",.27f,.15f,.095f,0,.35f),
            new Surface("Cream",.87f,.82f,.71f,0,.25f), new Surface("Steel",.60f,.65f,.65f,.65f,.42f),
            new Surface("SteelDark",.22f,.28f,.29f,.65f,.45f), new Surface("Ceramic",.83f,.81f,.75f,0,.50f),
            new Surface("Sage",.33f,.44f,.33f,0,.17f), new Surface("Charcoal",.055f,.075f,.08f,.15f,.35f),
            new Surface("Soup",.64f,.30f,.08f,0,.7f), new Surface("Tile",.72f,.75f,.72f,0,.4f),
            new Surface("FloorOakA",.73f,.57f,.43f,0,.15f), new Surface("FloorOakB",.78f,.62f,.47f,0,.15f),
            new Surface("FloorOakC",.76f,.59f,.44f,0,.15f), new Surface("FloorOakD",.71f,.54f,.40f,0,.15f),
            new Surface("MenuPaper",.95f,.93f,.88f,0,.15f), new Surface("SoyBottle",.21f,.11f,.055f,0,.45f),
            new Surface("SkinWarm",.65f,.44f,.31f,0,.28f), new Surface("SkinLight",.83f,.67f,.53f,0,.28f),
            new Surface("SkinDeep",.41f,.29f,.22f,0,.28f), new Surface("HairDark",.075f,.065f,.055f,0,.17f),
            new Surface("HairBrown",.20f,.13f,.085f,0,.17f), new Surface("ShirtSage",.38f,.45f,.37f,0,.14f),
            new Surface("ShirtClay",.55f,.35f,.28f,0,.14f), new Surface("ShirtNavy",.25f,.32f,.41f,0,.14f),
            new Surface("PantsBeige",.47f,.43f,.35f,0,.15f), new Surface("PantsNavy",.18f,.23f,.28f,0,.15f),
            new Surface("LipMuted",.40f,.23f,.19f,0,.15f),
            new Surface("FoodRice",.88f,.83f,.68f,0,.22f), new Surface("CurrySauce",.43f,.23f,.07f,0,.38f),
            new Surface("NoodleDough",.73f,.55f,.24f,0,.28f), new Surface("MeatBrown",.30f,.13f,.065f,0,.25f),
            new Surface("VegetableGreen",.22f,.40f,.14f,0,.20f), new Surface("TomatoRed",.66f,.16f,.09f,0,.30f)
        };

        public static void EnsureAssets()
        {
            if (AssetDatabase.LoadAssetAtPath<RestaurantCatalog>(Root + "/Resources/RestaurantCatalog.asset") == null)
                BuildAssets();
        }

        [MenuItem("Guardian/Restaurant/Build Assets")]
        public static void BuildAssets()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before building restaurant assets");
            foreach (string folder in new[] { "Materials", "Prefabs", "Resources" })
                Directory.CreateDirectory(Root + "/" + folder);
            AssetDatabase.Refresh();
            foreach (string name in new[]{"OakGrain","MenuArtwork"})
            {
                string texturePath=Root+"/Textures/"+name+".png";
                var textureImporter=AssetImporter.GetAtPath(texturePath) as TextureImporter;
                if (textureImporter==null) throw new FileNotFoundException("Original texture missing",texturePath);
                textureImporter.wrapMode=name=="OakGrain"?TextureWrapMode.Repeat:TextureWrapMode.Clamp;
                textureImporter.filterMode=FilterMode.Trilinear;
                textureImporter.anisoLevel=8;
                textureImporter.sRGBTexture=true;
                textureImporter.SaveAndReimport();
            }
            Shader shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("Built-in Standard shader missing");
            var materials = new Dictionary<string, Material>();
            foreach (Surface s in Surfaces)
            {
                string path = Root + "/Materials/" + s.Name + ".mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
                material.shader = shader; material.color = s.Color;
                material.SetFloat("_Metallic", s.Metal); material.SetFloat("_Glossiness", s.Smooth);
                material.mainTexture=s.Name.StartsWith("FloorOak")
                    ? AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Textures/OakGrain.png")
                    : s.Name=="MenuPaper"?AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Textures/MenuArtwork.png"):null;
                EditorUtility.SetDirty(material);
                materials.Add(s.Name, material);
            }
            var entries = new List<RestaurantCatalog.Entry>();
            foreach (string id in Ids)
            {
                string path = Root + "/Models/" + id + ".fbx";
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) throw new FileNotFoundException("Generate FBX with Source/generate_restaurant.py first", path);
                // Never request a Blender installation at Unity import time: only FBX is imported.
                if (importer.importAnimation || importer.addCollider || !Mathf.Approximately(importer.globalScale, 1f))
                {
                    importer.importAnimation = false; importer.addCollider = false; importer.globalScale = 1;
                    importer.SaveAndReimport();
                }
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var wrapper = new GameObject(id);
                try
                {
                    var fixture = wrapper.AddComponent<RestaurantFixture>();
                    fixture.AssetId = id;
                    if (id.StartsWith("Guest") || id=="KitchenStaff")
                        wrapper.AddComponent<RestaurantPerson>().Standing=id=="KitchenStaff";
                    fixture.BlocksRoutes = id != "RoomShell" && id != "Plate" && id != "Bowl" && id != "Cup" && id != "TableDetails" && !id.StartsWith("Food");
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, wrapper.transform);
                    // FBX axis conversion flips Blender X/Y in Unity. Normalize the visual
                    // inside the prefab while preserving single-mesh FBX unit/axis transforms.
                    instance.transform.localRotation = Quaternion.Euler(0, 180, 0) * instance.transform.localRotation;
                    foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                    {
                        Material[] slots = renderer.sharedMaterials;
                        for (int i = 0; i < slots.Length; i++)
                        {
                            string name = slots[i] != null ? slots[i].name : "";
                            if (!materials.TryGetValue(name, out Material replacement))
                                throw new InvalidOperationException("Unknown FBX material: " + name + " in " + id);
                            slots[i] = replacement;
                        }
                        renderer.sharedMaterials = slots;
                    }
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(wrapper, Root + "/Prefabs/" + id + ".prefab");
                    if (prefab == null) throw new IOException("Could not save prefab " + id);
                    entries.Add(new RestaurantCatalog.Entry { Id = id, Prefab = prefab });
                }
                finally { UnityEngine.Object.DestroyImmediate(wrapper); }
            }
            string catalogPath = Root + "/Resources/RestaurantCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<RestaurantCatalog>(catalogPath);
            if (catalog == null) { catalog = ScriptableObject.CreateInstance<RestaurantCatalog>(); AssetDatabase.CreateAsset(catalog, catalogPath); }
            catalog.Entries = entries.ToArray(); EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log("[Guardian] GUARDIAN_RESTAURANT_ASSETS_OK: " + entries.Count + " reusable prefabs");
        }
    }
}
