using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Guardian.UnitySim.Editor
{
    public static class RiggedGuestValidation
    {
        private static string Evidence="docs/evidence/rigged-guests/adult-baseline";
        [Serializable] private sealed class ClipResult
        {
            public string state;public int samples; public float duration, minFloor, maxFloor, seatContactY, maxTriangleEdgeRatio, hipTravel;
            public bool loops, controllerState;
        }
        [Serializable] private sealed class Result
        {
            public string unity,modelId="GuestAdult01", status="FAIL", runtimeRig="Generic, per-body authored bone translations", humanoidScope="Validity only; retargeted motion not accepted", error="";
            public bool disabledAnimatorAtomic, reenabledReleaseOnce, missingIdleAtomic, onceOnlyRelease;
            public bool genericAvatarValid, humanoidCandidateValid, idempotentGuids, seatApi;
            public int furniturePoseSamples, furnitureNarrowphasePairs;public float maxFurniturePenetration, maxSeatCompression;
            public int supportFixturePairs;public float maxSupportFixturePenetration;
            public int supportFixtureParts,negativeSupportFixturePairs;public float negativeSupportFixturePenetration;public bool negativeSupportPlacementRejected;
            public float maxBoneLengthError, walkStanceSlip, bindPoseJump, standPoseJump;
            public float maxBoundaryVertexJump;
            public bool footrestShoeFootprint;
            public int vertices, triangles, bones, blendedVertices; public float height;
            public List<ClipResult> clips=new List<ClipResult>();
        }
        [Serializable]private sealed class FamilyResult {public string status;public List<Result> guests=new List<Result>();}
        public static void HeadlessFamily()
        {
            if(!Application.isBatchMode || SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null || Application.unityVersion!="6000.3.12f1")throw new InvalidOperationException("Exact headless Unity required");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);var summary=new FamilyResult{status="PASS"};
            foreach(string id in RiggedGuestPipeline.Profiles)
            {
                Evidence="docs/evidence/rigged-guests/family/"+id;Directory.CreateDirectory(Evidence);var result=new Result{unity=Application.unityVersion,modelId=id};summary.guests.Add(result);
                try
                {
                    RiggedGuestPipeline.BuildProfile(id);string p=RiggedGuestPipeline.Root+"/Prefabs/"+id+".prefab",c=RiggedGuestPipeline.Root+"/Controllers/"+id+".controller";
                    string pg=AssetDatabase.AssetPathToGUID(p),cg=AssetDatabase.AssetPathToGUID(c);RiggedGuestPipeline.BuildProfile(id);
                    result.idempotentGuids=pg==AssetDatabase.AssetPathToGUID(p)&&cg==AssetDatabase.AssetPathToGUID(c);Require(result.idempotentGuids,"GUID changed");
                    Validate(result,id);result.status="PASS";
                }
                catch(Exception ex) {result.error=ex.ToString();summary.status="FAIL";Debug.LogException(ex);}
                File.WriteAllText(Evidence+"/unity-validation.json",JsonUtility.ToJson(result,true)+"\n");
            }
            File.WriteAllText("docs/evidence/rigged-guests/family/family-validation.json",JsonUtility.ToJson(summary,true)+"\n");AssetDatabase.SaveAssets();
            if(summary.status=="PASS")RiggedGuestPlaymodeEvidence.Begin();else EditorApplication.Exit(1);
        }
        public static void Headless()
        {
            var result=new Result{unity=Application.unityVersion};int exit=1;
            try
            {
                if(!Application.isBatchMode || SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)
                    throw new InvalidOperationException("Use batchmode/nographics, never a GUI launcher");
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                RiggedGuestPipeline.Rebuild();
                string prefabGuid=AssetDatabase.AssetPathToGUID(RiggedGuestPipeline.Prefab),controllerGuid=AssetDatabase.AssetPathToGUID(RiggedGuestPipeline.Controller);
                RiggedGuestPipeline.Rebuild();
                result.idempotentGuids=prefabGuid==AssetDatabase.AssetPathToGUID(RiggedGuestPipeline.Prefab) && controllerGuid==AssetDatabase.AssetPathToGUID(RiggedGuestPipeline.Controller);
                Require(result.idempotentGuids,"Regeneration changed asset GUIDs");
                Validate(result);result.status="PASS";exit=0;
            }
            catch(Exception ex) {result.error=ex.ToString();Debug.LogException(ex);}
            Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence+"/unity-adult-validation.json",JsonUtility.ToJson(result,true)+"\n");
            AssetDatabase.SaveAssets(); Debug.Log("RIGGED_GUEST_VALIDATION "+result.status);
            EditorApplication.Exit(exit);
        }
        private static void Validate(Result result,string id="GuestAdult01")
        {
            GameObject root=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(RiggedGuestPipeline.Root+"/Prefabs/"+id+".prefab"));
            var guest=root.GetComponent<RiggedRestaurantGuest>(); Animator animator=guest.Animator;
            GuestRigSettings settings=guest.RigSettings;
            PlayableGraph graph=PlayableGraph.Create("Original guest deterministic validation");
            Mesh baked=new Mesh();
            try
            {
                result.genericAvatarValid=animator.avatar!=null && animator.avatar.isValid && !animator.avatar.isHuman;
                Avatar human=AssetDatabase.LoadAssetAtPath<Avatar>(RiggedGuestPipeline.Root+"/Resources/"+id+"Humanoid.asset");
                result.humanoidCandidateValid=human!=null && human.isValid && human.isHuman;
                Require(result.genericAvatarValid && result.humanoidCandidateValid,"Missing valid avatars");
                Require(!animator.applyRootMotion,"Runtime must not drive navigation root");
                SkinnedMeshRenderer skin=root.GetComponentsInChildren<SkinnedMeshRenderer>().Single();Mesh mesh=skin.sharedMesh;
                int shoeSlot=Array.FindIndex(skin.sharedMaterials,m=>m!=null && (m.name=="GuestShoe" || m.name==id+"_Shoe"));Require(shoeSlot>=0,"Missing identifiable shoe material");
                result.vertices=mesh.vertexCount;result.triangles=mesh.triangles.Length/3;result.bones=skin.bones.Length;
                result.blendedVertices=mesh.boneWeights.Count(w=>w.weight1>0);
                Require(result.blendedVertices>300,"No real blended skinning");
                Require(skin.sharedMaterials.All(m=>m!=null && m.shader!=null && !m.shader.name.Contains("Error")),"Missing material");
                Require(guest.LeftHandSocket!=null && guest.RightHandSocket!=null && guest.SeatContact!=null,"Missing interaction anchors");
                animator.Rebind();animator.Update(0);
                // Ratios must use the bind/T-pose mesh. An already bent idle elbow
                // can collapse an edge and create a false sixfold stretch on unfolding.
                skin.BakeMesh(baked); Vector3[] rest=mesh.vertices;int[] triangles=baked.triangles;
                File.WriteAllLines(Evidence+"/unity-import-transforms.txt",root.GetComponentsInChildren<Transform>().Select(t=>t.name+" local="+t.localPosition.ToString("F5")+" scale="+t.localScale.ToString("F5")+" world="+t.position.ToString("F5")+" lossy="+t.lossyScale.ToString("F5")).Concat(new[]{"Mesh="+mesh.bounds+" baked="+baked.bounds+" renderer="+skin.bounds}));
                result.height=WorldBounds(skin.transform,baked.vertices).size.y;
                Require(Mathf.Abs(result.height-settings.height)<.03f,"Unexpected FBX units "+result.height+" expected "+settings.height);
                var controller=AnimatorControllerPlayable.Create(graph,animator.runtimeAnimatorController);
                AnimationPlayableOutput output=AnimationPlayableOutput.Create(graph,"Adult output",animator);output.SetSourcePlayable(controller);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);graph.Play();graph.Evaluate(0);
                Transform hip=RiggedGuestPipeline.Find(root.transform,"Hips");
                Transform lf=RiggedGuestPipeline.Find(root.transform,"GroundContact_Left"),rf=RiggedGuestPipeline.Find(root.transform,"GroundContact_Right");
                var segments=new List<Tuple<Transform,Transform,float>>();
                foreach(string side in new[]{"Left","Right"})
                foreach(var pair in new[]{Tuple.Create("UpperLeg","LowerLeg",settings.upperLeg),Tuple.Create("LowerLeg","Foot",settings.lowerLeg),Tuple.Create("UpperArm","LowerArm",settings.upperArm),Tuple.Create("LowerArm","Hand",settings.lowerArm)})
                    segments.Add(Tuple.Create(RiggedGuestPipeline.Find(root.transform,side+pair.Item1),RiggedGuestPipeline.Find(root.transform,side+pair.Item2),pair.Item3));
                using(var furniture=new FurnitureProbe(result,settings))
                using(var csv=new StreamWriter(Evidence+"/unity-adult-samples.csv"))
                {
                    csv.WriteLine("state,normalizedTime,hipX,hipY,hipZ,leftFootY,rightFootY,seatContactY,meshFloorY");
                    foreach(string state in RiggedGuestPipeline.States)
                    {
                        AnimationClip clip=RiggedGuestPipeline.LoadClip(id,state);
                        var r=new ClipResult{state=state,duration=clip.length,loops=clip.isLooping,minFloor=float.MaxValue,maxFloor=float.MinValue};
                        result.clips.Add(r);r.controllerState=controller.HasState(0,Animator.StringToHash("Base Layer."+state));
                        Require(r.controllerState,"Missing controller state "+state);
                        Require(r.loops==RiggedGuestPipeline.Loops(state),"Wrong loop mode "+state);
                        int count=Mathf.CeilToInt(clip.length*60);Vector3 firstHip=Vector3.zero,plantLeft=Vector3.zero,plantRight=Vector3.zero;bool haveLeft=false,haveRight=false;
                        for(int frame=0;frame<=count;frame++)
                        {
                            float t=frame/(float)count;controller.Play("Base Layer."+state,0,t);graph.Evaluate(0);
                            skin.BakeMesh(baked);Vector3[] points=baked.vertices;
                            float floor=WorldBounds(skin.transform,points).min.y;r.minFloor=Mathf.Min(r.minFloor,floor);r.maxFloor=Mathf.Max(r.maxFloor,floor);
                            if(frame==0)firstHip=hip.position;r.hipTravel=Mathf.Max(r.hipTravel,Vector3.Distance(firstHip,hip.position));
                            r.seatContactY=guest.SeatContact.position.y;
                            foreach(var segment in segments)result.maxBoneLengthError=Mathf.Max(result.maxBoneLengthError,Mathf.Abs(Vector3.Distance(segment.Item1.position,segment.Item2.position)-segment.Item3));
                            if(state=="Walk")
                            {
                                Vector3 drive=Vector3.forward*(settings.walkSpeed*t);
                                if(t>.03f && t<.48f) {Vector3 p=lf.position+drive;if(!haveLeft){plantLeft=p;haveLeft=true;}result.walkStanceSlip=Mathf.Max(result.walkStanceSlip,Vector3.Distance(plantLeft,p));}
                                if(t>.53f && t<.98f) {Vector3 p=rf.position+drive;if(!haveRight){plantRight=p;haveRight=true;}result.walkStanceSlip=Mathf.Max(result.walkStanceSlip,Vector3.Distance(plantRight,p));}
                            }
                            csv.WriteLine(FormattableString.Invariant($"{state},{t:F6},{hip.position.x:F6},{hip.position.y:F6},{hip.position.z:F6},{lf.position.y:F6},{rf.position.y:F6},{r.seatContactY:F6},{floor:F6}"));
                            for(int i=0;i<triangles.Length;i+=3)
                            for(int edge=0;edge<3;edge++)
                            {
                                int a=triangles[i+edge],b=triangles[i+(edge+1)%3];float original=Vector3.Distance(rest[a],rest[b]);
                                if(original>.003f)r.maxTriangleEdgeRatio=Mathf.Max(r.maxTriangleEdgeRatio,Vector3.Distance(points[a],points[b])/original);
                            }
                            r.samples++;
                            if(settings.RequiresFootrest && (state=="Seated" || state=="Handoff"))
                            {
                                foreach(int vertex in mesh.GetTriangles(shoeSlot).Distinct())
                                {Vector3 p=skin.transform.TransformPoint(points[vertex]);Require(Mathf.Abs(p.x)<=.183f && p.z>=.312f && p.z<=.518f,"Child shoe overhangs support deck: "+p);}
                                result.footrestShoeFootprint=true;
                            }
                            if(frame%6==0 && (state=="SeatReady" || state=="SitDown" || state=="Seated" || state=="StandUp" || state=="Handoff"))
                                furniture.Check(skin.transform,points,triangles,state,t);
                        }
                        bool transition=state=="SitDown" || state=="StandUp",supportedSeat=state=="Seated" || state=="Handoff";
                        float expected=supportedSeat?settings.supportHeight:.015f;
                        Require(r.minFloor>=(transition ? .010f : expected-.005f) && r.maxFloor<=(transition ? settings.supportHeight+.04f : expected+.010f),state+" feet/floor/support incorrect: "+r.minFloor+".."+r.maxFloor);
                        Require(r.maxTriangleEdgeRatio<3,state+" tears/stretches skinned mesh: "+r.maxTriangleEdgeRatio);
                        if(state=="Seated" || state=="Handoff")Require(Mathf.Abs(r.seatContactY-.5475f)<.003f,state+" incorrect seat anchor "+r.seatContactY);
                    }
                }
                Require(result.clips.Single(c=>c.state=="Walk").hipTravel>.008f*settings.walkSpeed/.8f,"Walk is static");
                Require(result.clips.Single(c=>c.state=="SitDown").hipTravel>=settings.expectedSeatHipTravel-.001f,"SitDown missing contracted hip motion");
                Require(result.maxBoneLengthError<.001f,"Bone length changed "+result.maxBoneLengthError);
                Require(result.walkStanceSlip<.004f,"Walk stance slides under nominal external .8m/s drive "+result.walkStanceSlip);
                graph.Destroy();
                // API preconditions and exact seat-root change; no autonomous task transfer is claimed.
                animator.Rebind();animator.Update(0);root.transform.position=settings.Approach;
                GameObject chair=new GameObject("Seat frame contract");
                GameObject support=settings.RequiresFootrest?(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(RiggedGuestPipeline.Root+"/Prefabs/ChildFootrest.prefab")):null;
                try
                {
                    Require(!guest.Play(GuestPose.Seated),"Unbound chair state accepted");
                    Require(!guest.Play((GuestPose)999),"Invalid pose accepted");Vector3 approachHip=hip.position;Vector3[] previousPose=WorldVertices(skin,baked);
                    if(settings.RequiresFootrest)Require(!guest.BindSeat(chair.transform),"Child accepted missing foot support");
                    if(settings.RequiresFootrest)
                    {
                        Require(!guest.BindSeat(chair.transform,chair.transform),"Child accepted unmarked support");
                        support.transform.localScale=Vector3.one*1.1f;Require(!guest.BindSeat(chair.transform,support.transform),"Child accepted scaled support");support.transform.localScale=Vector3.one;
                    }
                    Require(guest.BindSeat(chair.transform,support==null?null:support.transform),"Seat bind failed at authored approach");
                    animator.Update(0);result.bindPoseJump=Vector3.Distance(approachHip,hip.position);Require(result.bindPoseJump<.001f,"Seat binding visibly jumps");result.maxBoundaryVertexJump=Mathf.Max(result.maxBoundaryVertexJump,PoseJump(previousPose,WorldVertices(skin,baked)));
                    Require(!guest.Play(GuestPose.Walk),"Seated coordinate frame allowed free walking");
                    Require(root.transform.position.sqrMagnitude<.000001f && guest.HasSeat,"Seat root changed incorrectly");
                    Require(!guest.BindSeat(chair.transform,support==null?null:support.transform),"Duplicate seat bind accepted");
                    Require(guest.Play(GuestPose.SitDown),"SitDown API failed");Advance(animator,1.65f);
                    previousPose=WorldVertices(skin,baked);Require(guest.Play(GuestPose.Seated),"Seated API failed");animator.Update(0);result.maxBoundaryVertexJump=Mathf.Max(result.maxBoundaryVertexJump,PoseJump(previousPose,WorldVertices(skin,baked)));
                    Require(guest.Play(GuestPose.StandUp),"StandUp API failed");Require(!guest.FinishStanding(),"Incomplete stand released seat");Advance(animator,1.65f);
                    Vector3 stoodHip=hip.position;
                    Vector3 heldPosition=root.transform.position;GuestPose heldPose=guest.Pose;
                    animator.enabled=false;Require(!guest.FinishStanding(),"Disabled animator released seat");
                    Require(root.transform.position==heldPosition && guest.HasSeat && guest.Pose==heldPose,"Disabled failure partially committed");result.disabledAnimatorAtomic=true;animator.enabled=true;animator.Update(0);
                    Require(guest.FinishStanding(),"Reenabled completed animator could not release");animator.Update(0);
                    Require(!guest.FinishStanding() && !guest.HasSeat && guest.Pose==GuestPose.Idle && Vector3.Distance(root.transform.position,settings.Approach)<.001f,"Reenabled animator released more than once");result.reenabledReleaseOnce=true;
                    Require(guest.BindSeat(chair.transform,support==null?null:support.transform) && guest.Play(GuestPose.SitDown),"Second atomicity cycle setup failed");Advance(animator,1.65f);
                    Require(guest.Play(GuestPose.Seated),"Second cycle seated failed");animator.Update(0);Require(guest.Play(GuestPose.StandUp),"Second cycle stand failed");Advance(animator,1.65f);
                    RuntimeAnimatorController original=animator.runtimeAnimatorController;var missing=new AnimatorController();var stateMachine=new AnimatorStateMachine();
                    missing.AddLayer(new AnimatorControllerLayer{name="Base Layer",defaultWeight=1,stateMachine=stateMachine});
                    AnimatorState only=stateMachine.AddState("StandUp");only.motion=RiggedGuestPipeline.LoadClip(id,"StandUp");stateMachine.defaultState=only;
                    try
                    {
                        animator.runtimeAnimatorController=missing;animator.Rebind();animator.Play("Base Layer.StandUp",0,1);animator.Update(0);
                        Require(!guest.FinishStanding(),"Missing Idle released seat");Require(root.transform.position==heldPosition && guest.HasSeat && guest.Pose==heldPose,"Missing Idle failure partially committed");result.missingIdleAtomic=true;
                    }
                    finally {animator.runtimeAnimatorController=original;animator.Rebind();animator.Play("Base Layer.StandUp",0,1);animator.Update(0);UnityEngine.Object.DestroyImmediate(missing);UnityEngine.Object.DestroyImmediate(stateMachine);}
                    previousPose=WorldVertices(skin,baked);Require(guest.FinishStanding(),"StandUp completion failed");animator.Update(0);result.maxBoundaryVertexJump=Mathf.Max(result.maxBoundaryVertexJump,PoseJump(previousPose,WorldVertices(skin,baked)));Require(result.maxBoundaryVertexJump<.001f,"Pose boundary mesh jumps "+result.maxBoundaryVertexJump);
                    result.standPoseJump=Vector3.Distance(stoodHip,hip.position);Require(result.standPoseJump<.001f,"Standing root release visibly jumps");
                    Require(!guest.HasSeat && Vector3.Distance(root.transform.position,settings.Approach)<.001f,"Standing root offset incorrect");
                    Vector3 released=root.transform.position;Require(!guest.FinishStanding() && root.transform.position==released,"Double release moved root");result.onceOnlyRelease=true;
                    result.seatApi=true;
                }
                finally {UnityEngine.Object.DestroyImmediate(chair);if(support!=null)UnityEngine.Object.DestroyImmediate(support);}
            }
            finally {if(graph.IsValid())graph.Destroy();UnityEngine.Object.DestroyImmediate(baked);UnityEngine.Object.DestroyImmediate(root);}
        }
        private static Bounds WorldBounds(Transform transform,Vector3[] points)
        {Bounds b=new Bounds(transform.TransformPoint(points[0]),Vector3.zero);for(int i=1;i<points.Length;i++)b.Encapsulate(transform.TransformPoint(points[i]));return b;}
        private static void Advance(Animator animator,float duration)
        {animator.Update(0);for(int i=0;i<Mathf.CeilToInt(duration*60);i++)animator.Update(1f/60);}
        private static Vector3[] WorldVertices(SkinnedMeshRenderer skin,Mesh baked)
        {skin.BakeMesh(baked);return baked.vertices.Select(p=>skin.transform.TransformPoint(p)).ToArray();}
        private static float PoseJump(Vector3[] a,Vector3[] b)
        {float delta=0;for(int i=0;i<a.Length;i++)delta=Mathf.Max(delta,Vector3.Distance(a[i],b[i]));return delta;}
        private sealed class FurnitureProbe : IDisposable
        {
            private readonly Result result;private readonly List<GameObject> objects=new List<GameObject>();
            private readonly List<Mesh> meshes=new List<Mesh>();private readonly List<MeshCollider> parts=new List<MeshCollider>();
            private readonly List<MeshCollider> supportParts=new List<MeshCollider>(),furnitureParts=new List<MeshCollider>();
            private readonly MeshCollider body;private readonly Mesh worldMesh;
            public FurnitureProbe(Result result,GuestRigSettings settings)
            {
                this.result=result;
                GameObject chair=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Restaurant/Prefabs/DiningChair.prefab"));objects.Add(chair);
                GameObject table=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Restaurant/Prefabs/DiningTable.prefab"));objects.Add(table);
                table.transform.SetPositionAndRotation(new Vector3(.46f,0,1.4f),Quaternion.Euler(0,90,0));
                // Both prefabs normalize Blender axes through a 180-degree visual frame.
                var fixtures=new List<GameObject>{chair,table};
                if(settings.RequiresFootrest) {var support=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(RiggedGuestPipeline.Root+"/Prefabs/ChildFootrest.prefab"));objects.Add(support);fixtures.Add(support);}
                foreach(GameObject fixture in fixtures)
                foreach(MeshFilter filter in fixture.GetComponentsInChildren<MeshFilter>())
                {
                    var go=new GameObject(fixture.name+" / "+filter.name);objects.Add(go);
                    var mesh=new Mesh();meshes.Add(mesh);mesh.vertices=filter.sharedMesh.vertices.Select(p=>filter.transform.TransformPoint(p)).ToArray();mesh.triangles=filter.sharedMesh.triangles;mesh.RecalculateBounds();
                    MeshCollider col=go.AddComponent<MeshCollider>();col.sharedMesh=mesh;col.convex=true;parts.Add(col);
                    if(fixture.GetComponent<GuestFootSupport>()!=null)supportParts.Add(col);else furnitureParts.Add(col);
                }
                if(settings.RequiresFootrest)
                {
                    result.supportFixtureParts=supportParts.Count;Require(supportParts.Count==5 && furnitureParts.Count>0,"Missing explicit footrest/furniture parts");
                    result.maxSupportFixturePenetration=SupportPenetration(Vector3.zero,out int actualPairs);result.supportFixturePairs=actualPairs;
                    Require(actualPairs==supportParts.Count*furnitureParts.Count && actualPairs>0,"Empty footrest fixture comparison");
                    Require(result.maxSupportFixturePenetration<=.003f,"Footrest overlaps furniture depth="+result.maxSupportFixturePenetration);
                    // Translate the whole support into the timber seat. The same
                    // real geometry predicate must reject this known bad placement.
                    result.negativeSupportFixturePenetration=SupportPenetration(new Vector3(0,.27f,-.415f),out int negativePairs);result.negativeSupportFixturePairs=negativePairs;
                    result.negativeSupportPlacementRejected=result.negativeSupportFixturePenetration>.003f;
                    Require(negativePairs==actualPairs && result.negativeSupportPlacementRejected,"Negative support placement was accepted");
                }
                var human=new GameObject("Actual baked guest triangle collider");objects.Add(human);worldMesh=new Mesh();meshes.Add(worldMesh);body=human.AddComponent<MeshCollider>();body.convex=false;
                Physics.SyncTransforms();
            }
            private float SupportPenetration(Vector3 offset,out int comparisons)
            {
                comparisons=0;float maximum=0;
                foreach(MeshCollider supportPart in supportParts)
                foreach(MeshCollider furniture in furnitureParts)
                {
                    comparisons++;
                    if(Physics.ComputePenetration(supportPart,offset,Quaternion.identity,furniture,Vector3.zero,Quaternion.identity,out Vector3 direction,out float depth))maximum=Mathf.Max(maximum,depth);
                }
                return maximum;
            }
            public void Check(Transform transform,Vector3[] points,int[] triangles,string state,float t)
            {
                body.sharedMesh=null;worldMesh.Clear();worldMesh.vertices=points.Select(p=>transform.TransformPoint(p)).ToArray();worldMesh.triangles=triangles;worldMesh.RecalculateBounds();body.sharedMesh=worldMesh;
                result.furniturePoseSamples++;
                foreach(MeshCollider fixture in parts)
                {
                    if(!worldMesh.bounds.Intersects(fixture.sharedMesh.bounds))continue;
                    result.furnitureNarrowphasePairs++;
                    bool overlap=Physics.ComputePenetration(fixture,Vector3.zero,Quaternion.identity,body,Vector3.zero,Quaternion.identity,out Vector3 direction,out float depth);
                    if(!overlap)continue;
                    bool seat=fixture.name.Contains("Sage seat pad");
                    if(seat)result.maxSeatCompression=Mathf.Max(result.maxSeatCompression,depth);
                    else result.maxFurniturePenetration=Mathf.Max(result.maxFurniturePenetration,depth);
                    if(depth>(seat ? .005f : .003f))File.WriteAllLines(Evidence+"/furniture-intrusion-points.txt",worldMesh.vertices.Where(p=>fixture.sharedMesh.bounds.Contains(p)).Select(p=>p.ToString("F6")));
                    Require(depth<=(seat ? .005f : .003f),"Furniture intrusion "+state+" t="+t+" / "+fixture.name+" depth="+depth);
                }
            }
            public void Dispose()
            {foreach(GameObject go in objects)UnityEngine.Object.DestroyImmediate(go);foreach(Mesh mesh in meshes)UnityEngine.Object.DestroyImmediate(mesh);}
        }
        private static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
    }
}
