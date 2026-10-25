using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    [InitializeOnLoad]
    public static class RiggedGuestPlaymodeEvidence
    {
        private const string Key="Guardian.GuestFamily.Playmode";
        [Serializable]private sealed class ActorResult {public string model;public bool automaticSeated,automaticIdle,onceOnly;public int seatedFrame,idleFrame;}
        [Serializable]private sealed class Result {public string status="FAIL",error="",scope="Actual MonoBehaviour.Update completion observed in headless Editor Play Mode; fixed simulated 1/60s frames, no human input or Player test";public int frames;public List<ActorResult> actors=new List<ActorResult>();}
        private static readonly List<RiggedRestaurantGuest> Guests=new List<RiggedRestaurantGuest>();
        private static Result report;private static int startFrame,lastFrame=-1;private static double startWall;
        static RiggedGuestPlaymodeEvidence() {EditorApplication.update+=Tick;}
        public static void Begin()
        {SessionState.SetBool(Key,true);SessionState.SetBool(Key+"Done",false);EditorApplication.isPlaying=true;}
        private static void Tick()
        {
            if(!SessionState.GetBool(Key,false))return;
            if(SessionState.GetBool(Key+"Done",false) && !EditorApplication.isPlaying)
            {SessionState.SetBool(Key,false);EditorApplication.Exit(SessionState.GetInt(Key+"Exit",1));return;}
            if(!EditorApplication.isPlaying || SessionState.GetBool(Key+"Done",false))return;
            try
            {
                if(report==null)
                {
                    Time.captureDeltaTime=1f/60;report=new Result();startFrame=Time.frameCount;startWall=EditorApplication.timeSinceStartup;
                    int index=0;
                    foreach(string id in RiggedGuestPipeline.Profiles)
                    {
                        GameObject actor=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(RiggedGuestPipeline.Root+"/Prefabs/"+id+".prefab"));
                        var guest=actor.GetComponent<RiggedRestaurantGuest>();Guests.Add(guest);report.actors.Add(new ActorResult{model=id});
                        GameObject chair=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Restaurant/Prefabs/DiningChair.prefab"));chair.transform.position=new Vector3(index++*4,0,0);
                        GameObject support=null;if(guest.RigSettings.RequiresFootrest) {support=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(RiggedGuestPipeline.Root+"/Prefabs/ChildFootrest.prefab"));support.transform.position=chair.transform.position;}
                        actor.transform.position=chair.transform.TransformPoint(guest.RigSettings.Approach);guest.Animator.Rebind();guest.Animator.Update(0);
                        if(!guest.BindSeat(chair.transform,support==null?null:support.transform) || !guest.Play(GuestPose.SitDown))throw new InvalidOperationException(id+" Play Mode setup failed");
                    }
                }
                if(lastFrame==Time.frameCount)return;lastFrame=Time.frameCount;report.frames=Time.frameCount-startFrame;
                bool done=true;
                for(int i=0;i<Guests.Count;i++)
                {
                    RiggedRestaurantGuest guest=Guests[i];ActorResult actor=report.actors[i];
                    if(!actor.automaticSeated && guest.Pose==GuestPose.Seated)
                    {
                        actor.automaticSeated=true;actor.seatedFrame=report.frames;
                        if(!guest.Play(GuestPose.StandUp))throw new InvalidOperationException(actor.model+" could not stand");
                    }
                    if(actor.automaticSeated && !actor.automaticIdle && guest.Pose==GuestPose.Idle && !guest.HasSeat)
                    {
                        actor.automaticIdle=true;actor.idleFrame=report.frames;Vector3 p=guest.transform.position;
                        actor.onceOnly=!guest.FinishStanding() && guest.transform.position==p;
                        if(!actor.onceOnly)throw new InvalidOperationException(actor.model+" double release");
                    }
                    done &= actor.automaticIdle;
                }
                if(done) {report.status="PASS";Finish(0);}
                else if(report.frames>600 || EditorApplication.timeSinceStartup-startWall>45)throw new TimeoutException("Automatic Update lifecycle did not finish");
            }
            catch(Exception ex) {if(report==null)report=new Result();report.error=ex.ToString();Debug.LogException(ex);Finish(1);}
        }
        private static void Finish(int exit)
        {
            File.WriteAllText("docs/evidence/rigged-guests/family/playmode-lifecycle.json",JsonUtility.ToJson(report,true)+"\n");
            Time.captureDeltaTime=0;SessionState.SetInt(Key+"Exit",exit);SessionState.SetBool(Key+"Done",true);EditorApplication.isPlaying=false;
        }
    }
}
