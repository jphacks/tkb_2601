using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    public static class RiggedGuestPipeline
    {
        internal const string Root="Assets/Art/Restaurant/Characters";
        internal const string Model=Root+"/Models/GuestAdult01.fbx";
        internal const string Prefab=Root+"/Prefabs/GuestAdult01.prefab";
        internal const string Controller=Root+"/Controllers/GuestAdult01.controller";
        internal static readonly string[] States=Enum.GetNames(typeof(GuestPose));
        internal static bool Loops(string state) => state=="Idle" || state=="Walk" || state=="Waiting" || state=="SeatReady" || state=="Seated";
        private static readonly string[] MatNames={"GuestSkin","GuestHair","GuestShirt","GuestTrousers","GuestShoe","GuestEye","GuestIvory"};
        private static readonly Color[] Colors={new Color(.65f,.44f,.31f),new Color(.075f,.065f,.055f),new Color(.38f,.45f,.37f),new Color(.18f,.23f,.28f),new Color(.055f,.06f,.07f),new Color(.035f,.035f,.03f),new Color(.93f,.91f,.85f)};

        [MenuItem("Guardian/Guests/Rebuild original adult assets")]
        public static void Rebuild()
        { BuildProfile("GuestAdult01"); }
        internal static readonly string[] Profiles={"GuestAdult01","GuestFemale01","GuestSenior01","GuestChild01"};
        [MenuItem("Guardian/Guests/Rebuild four original guests")]
        public static void RebuildFamily() {foreach(string id in Profiles)BuildProfile(id);}
        public static void BuildProfile(string id)
        {
            if(Application.unityVersion!="6000.3.12f1") throw new InvalidOperationException("Unity 6000.3.12f1 required");
            foreach(string folder in new[]{"Materials","Prefabs","Controllers","Resources"}) Directory.CreateDirectory(Root+"/"+folder);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            GuestRigSettings settings=JsonUtility.FromJson<GuestRigSettings>(File.ReadAllText(Root+"/Source/"+id+"-settings.json"));
            Color[] colors={settings.skinColor,settings.hairColor,settings.shirtColor,settings.trousersColor,settings.shoeColor,settings.eyeColor,settings.ivoryColor};
            string modelPath=Root+"/Models/"+id+".fbx",prefabPath=Root+"/Prefabs/"+id+".prefab",controllerPath=Root+"/Controllers/"+id+".controller";
            Shader shader=Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if(shader==null) throw new InvalidOperationException("No supported lit shader");
            for(int i=0;i<MatNames.Length;i++)
            {
                string path=Root+"/Materials/"+MaterialName(id,i)+".mat";
                Material mat=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(mat==null) {mat=new Material(shader);AssetDatabase.CreateAsset(mat,path);}
                mat.shader=shader; mat.color=colors[i];
                if(mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor",colors[i]);
                if(mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness",.24f);
                EditorUtility.SetDirty(mat);
            }
            Configure(modelPath,false,null,null,id);
            GameObject model=AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            Avatar generic=AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().Single(a=>a.isValid);
            BuildHumanCandidate(model,id);
            foreach(string state in States) Configure(Root+"/Models/"+id+"@"+state+".fbx",true,generic,state,id);
            AnimatorController controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if(controller==null) controller=AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            AnimatorStateMachine sm=controller.layers[0].stateMachine;
            // Preserve asset GUID and existing state subassets on repeated generation.
            foreach(string state in States)
            {
                AnimatorState node=sm.states.Select(s=>s.state).FirstOrDefault(s=>s.name==state) ?? sm.AddState(state);
                node.motion=LoadClip(id,state);node.speed=1;node.writeDefaultValues=false;
                if(state=="Idle")sm.defaultState=node;
            }
            EditorUtility.SetDirty(controller);
            GameObject wrapper=new GameObject(id);
            try
            {
                var frame=new GameObject("Forward frame");frame.transform.SetParent(wrapper.transform,false);frame.transform.localRotation=Quaternion.Euler(0,180,0);
                GameObject instance=(GameObject)PrefabUtility.InstantiatePrefab(model);instance.transform.SetParent(frame.transform,false);
                Animator animator=instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
                animator.avatar=generic;animator.runtimeAnimatorController=controller;animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                foreach(SkinnedMeshRenderer skin in instance.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.updateWhenOffscreen=true;
                var guest=wrapper.AddComponent<RiggedRestaurantGuest>();
                guest.Configure(animator,Find(instance.transform,"HandSocket_Left"),Find(instance.transform,"HandSocket_Right"),Find(instance.transform,"SeatContact"),settings);
                PrefabUtility.SaveAsPrefabAsset(wrapper,prefabPath);
            }
            finally {UnityEngine.Object.DestroyImmediate(wrapper);}
            AssetDatabase.SaveAssets();
            if(settings.RequiresFootrest) BuildFootrest();
            Debug.Log("RIGGED_GUEST_ASSETS_READY "+prefabPath);
        }

        private static string MaterialName(string id,int i) => id=="GuestAdult01"?MatNames[i]:id+"_"+MatNames[i].Substring(5);
        private static void Configure(string path,bool animation,Avatar avatar,string state=null,string id="GuestAdult01")
        {
            var importer=(ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable=true;importer.importCameras=false;importer.importLights=false;
            importer.optimizeGameObjects=false;importer.meshCompression=ModelImporterMeshCompression.Off;importer.preserveHierarchy=true;
            // Keep original joint translations and contact anchors. Humanoid retargeting is an additional candidate,
            // not silently substituted for the per-body authored Generic runtime skeleton.
            importer.animationType=ModelImporterAnimationType.Generic;importer.importAnimation=animation;
            importer.avatarSetup=avatar==null?ModelImporterAvatarSetup.CreateFromThisModel:ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar=avatar;importer.motionNodeName="MotionRoot";
            importer.animationCompression=ModelImporterAnimationCompression.Off;importer.resampleCurves=true;
            importer.materialImportMode=ModelImporterMaterialImportMode.ImportStandard;
            for(int i=0;i<MatNames.Length;i++) importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material),MaterialName(id,i)),AssetDatabase.LoadAssetAtPath<Material>(Root+"/Materials/"+MaterialName(id,i)+".mat"));
            importer.SaveAndReimport();
            if(animation)
            {
                var clips=importer.defaultClipAnimations;
                if(clips.Length!=1)throw new InvalidOperationException("Expected one authored take at "+path+", found "+clips.Length);
                var clip=clips[0];clip.name=state;clip.loopTime=Loops(state);clip.loopPose=false;
                clip.lockRootRotation=true;clip.lockRootPositionXZ=true;clip.lockRootHeightY=true;
                clip.keepOriginalOrientation=true;clip.keepOriginalPositionXZ=true;clip.keepOriginalPositionY=true;
                importer.clipAnimations=new[]{clip};importer.SaveAndReimport();
            }
        }
        private static void BuildHumanCandidate(GameObject model,string id)
        {
            GameObject instance=UnityEngine.Object.Instantiate(model);
            try
            {
                var transforms=instance.GetComponentsInChildren<Transform>();
                var human=new List<HumanBone>();
                foreach(string name in HumanTrait.BoneName)
                {
                    string bone=name.Replace(" ","");
                    if(transforms.Any(t=>t.name==bone))human.Add(new HumanBone {boneName=bone,humanName=name,limit=new HumanLimit{useDefaultValues=true}});
                }
                var description=new HumanDescription {human=human.ToArray(),skeleton=transforms.Select(t=>new SkeletonBone{name=t.name,position=t.localPosition,rotation=t.localRotation,scale=t.localScale}).ToArray(),
                    armStretch=0,legStretch=0,upperArmTwist=.5f,lowerArmTwist=.5f,upperLegTwist=.5f,lowerLegTwist=.5f,feetSpacing=0,hasTranslationDoF=true};
                Avatar candidate=AvatarBuilder.BuildHumanAvatar(instance,description);
                candidate.name=id+" Humanoid candidate";
                if(!candidate.isValid || !candidate.isHuman) throw new InvalidOperationException("Adult humanoid candidate invalid");
                string legacy=Root+"/Resources/GuestAdult01Humanoid.avatar";
                if(File.Exists(legacy)) AssetDatabase.DeleteAsset(legacy);
                string path=Root+"/Resources/"+id+"Humanoid.asset";
                Avatar old=AssetDatabase.LoadAssetAtPath<Avatar>(path);
                if(old==null)AssetDatabase.CreateAsset(candidate,path);
                else {EditorUtility.CopySerialized(candidate,old);UnityEngine.Object.DestroyImmediate(candidate);EditorUtility.SetDirty(old);}
            }
            finally {UnityEngine.Object.DestroyImmediate(instance);}
        }
        internal static Transform Find(Transform root,string name) => root.GetComponentsInChildren<Transform>(true).Single(t=>t.name==name);
        internal static AnimationClip LoadClip(string state) => LoadClip("GuestAdult01",state);
        internal static AnimationClip LoadClip(string id,string state) => AssetDatabase.LoadAllAssetsAtPath(Root+"/Models/"+id+"@"+state+".fbx").OfType<AnimationClip>().Single(c=>!c.name.StartsWith("__"));
        private static void BuildFootrest()
        {
            string path=Root+"/Models/ChildFootrest.fbx";var importer=(ModelImporter)AssetImporter.GetAtPath(path);importer.isReadable=true;importer.importAnimation=false;
            foreach(string name in new[]{"Oak","Walnut"})importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material),name),AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Restaurant/Materials/"+name+".mat"));
            importer.SaveAndReimport();var go=new GameObject("Child foot support");go.AddComponent<GuestFootSupport>();
            try {GameObject visual=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));visual.transform.SetParent(go.transform,false);visual.transform.localRotation=Quaternion.Euler(0,180,0)*visual.transform.localRotation;PrefabUtility.SaveAsPrefabAsset(go,Root+"/Prefabs/ChildFootrest.prefab");}
            finally {UnityEngine.Object.DestroyImmediate(go);}
        }
    }
}
