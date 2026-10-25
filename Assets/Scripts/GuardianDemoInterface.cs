using System;
using System.Linq;
using UnityEngine;

namespace Guardian.UnitySim
{
    public enum GuardianDemoChoice { ContinueCapable, Transfer, KeepStopped }

    /// <summary>One selection and three contextual decisions over the authoritative human/task systems.</summary>
    public sealed class GuardianDemoInterface : MonoBehaviour
    {
        private GuardianSimulator sim;
        private GuardianSafeCamera cameraRig;
        private GUISkin skin;
        private Font font;
        private string signature="",operationActor;
        private DeliveryTask operationTask;
        private GuardianDemoChoice operationChoice;
        private bool operating,pointerConsumed,dragged;
        private int pointerButton;
        private Vector2 down,last;
        private Rect panel,header;
        private string pressedActor;
        private int pressedSelection,pressedState;
        private GuardianDemoChoice? pressedChoice;
        private bool cardRequested;
        public string ConversationNotice {get;set;}="";
        /// <summary>A robot (any robot, alert or not) was clicked; null when the click hit nothing.</summary>
        public event Action<string> RobotClicked;
        /// <summary>Other on-screen panels (screen pixels, y up) that clicks and the scroll wheel must not pass through.</summary>
        public Func<Vector2,bool> ExtraUi;
        public string SelectedId {get;private set;}
        public int SelectionVersion {get;private set;}
        public int StateVersion {get;private set;}
        public int AcceptedCount {get;private set;}
        public int RejectedCount {get;private set;}
        public string Feedback {get;private set;}="ロボットをクリックすると、状態を確認できます。";
        public string OperationStatus {get;private set;}="";
        public GuardianSafeCamera CameraRig=>cameraRig;
        public bool CardVisible=>cardRequested && Selected!=null && NeedsDecision;
        public GuardianRobot Selected=>sim==null?null:sim.Robots.FirstOrDefault(r=>r.RobotId==SelectedId);
        private GuardianHumanActor Actor=>sim?.HumanExperienceActive==true && SelectedId!=null && sim.HumanExperience.Actors.TryGetValue(SelectedId,out var a)?a:null;
        public static bool HasAlert(GuardianSimulator simulator,GuardianRobot robot)
        {
            if(robot==null)return false;
            GuardianHumanActor actor=null;
            if(simulator.HumanExperienceActive)simulator.HumanExperience.Actors.TryGetValue(robot.RobotId,out actor);
            return robot.Condition!=RobotCondition.Normal || robot.EmergencyStopped || robot.PhysicsDriver.SafetyHeld
                || actor?.AwaitingDecision==true || actor?.MobilityUnsafe==true || actor?.HoldStopped==true;
        }
        public bool NeedsDecision=>HasAlert(sim,Selected);

        public void Initialize(GuardianSimulator simulator) {sim=simulator;}
        public void Select(string id)
        {SelectCore(id,true);}
        public void SelectForCommand(string id)
        {SelectCore(id,false);}
        private void SelectCore(string id,bool showCard)
        {
            // Selection only observes an already detected alert; no fault is injected.
            var robot=sim.Robots.FirstOrDefault(r=>r.RobotId==id);
            SelectedId=HasAlert(sim,robot)?id:null;cardRequested=showCard && SelectedId!=null;SelectionVersion++;signature="";RefreshState();
        }
        public void Close() {SelectedId=null;cardRequested=false;SelectionVersion++;signature="";}
        public void RefreshState()
        {
            var r=Selected;var a=Actor;var t=r?.CurrentTask??a?.HeldTask;
            GuardianEvidenceChannel evidence=null;
            if(r!=null && sim.DemoEvidence!=null)sim.DemoEvidence.Channels.TryGetValue(r.RobotId,out evidence);
            string now=r==null?"":r.RobotId+"/"+r.Condition+"/"+r.EmergencyStopped+"/"+r.DispatchEnabled+"/"+r.FleetRole
                +"/"+r.PhysicsDriver.SafetyHeld+"/"+a?.Revision+"/"+a?.AwaitingDecision+"/"+a?.MobilityUnsafe+"/"+a?.HoldStopped
                +"/"+t?.Id+"/"+t?.Phase+"/"+evidence?.Fresh+"/"+evidence?.Origin+"/"+evidence?.Source+"/"+evidence?.Connected+"/"+evidence?.Calibrated;
            if(now!=signature){signature=now;StateVersion++;}
        }
        public string Summary
        {
            get
            {
                var r=Selected;if(r==null)return "ロボットを選んでください。";
                string reason=Actor?.MobilityUnsafe==true?"安全な走行を確認できないため、停止しています。":r.PhysicsDriver.SafetyHeld?
                    "人・障害物・入力の安全確認が必要なため、停止しています。":r.Condition==RobotCondition.Warning?
                    "模擬振動が上がり、スープを運べません。":r.Condition==RobotCondition.Limited?
                    "模擬振動が高く、料理を運べません。空の皿は回収できます。":r.EmergencyStopped?
                    "停止中です。原因を確認してから対応を選んでください。":"通常の状態です。料理や皿を運べます。";
                var task=r.CurrentTask??Actor?.HeldTask;
                return "ロボット "+r.RobotId+"\n"+reason+"\n"+(task==null?"現在、担当する仕事はありません。":
                    "担当："+(task.Kind==DeliveryKind.Soup?"スープの配膳":task.Kind==DeliveryKind.Food?"料理の配膳":"空の皿の回収")+"（"+task.Id+"）");
            }
        }
        private string ChoiceReason(GuardianDemoChoice choice)
        {
            var r=Selected;var a=Actor;if(r==null)return "対象の機体を選んでください。";
            if(choice==GuardianDemoChoice.KeepStopped)return "荷物を保持したまま停止します。";
            if(a?.MobilityUnsafe==true)return "走行の安全確認が済んでいないため、実行できません。";
            if(r.PhysicsDriver.SafetyHeld)return "人・障害物・入力の安全確認を先に行ってください。";
            var task=r.CurrentTask??a?.HeldTask;
            if(choice==GuardianDemoChoice.Transfer)return task==null?"引き継ぐ仕事がありません。":"運搬中の荷物は元の受け渡し場所へ返してから引き継ぎます。";
            if(task!=null && GuardianCapabilityPolicy.Allows(r.Condition,task.Kind))return "この機体で担当の仕事を再開します。";
            return "配膳が難しい場合は、回収できる空の皿を探します。";
        }
        public GuardianHumanResult Choose(string id,int selectionVersion,int stateVersion,GuardianDemoChoice choice)
        {
            sim.RefreshDemoEvidence();
            RefreshState();
            if(id!=SelectedId || selectionVersion!=SelectionVersion || stateVersion!=StateVersion || !NeedsDecision)
                return Reject("状況や選択が変わりました。機体を選び直してください。");
            var human=sim.HumanExperience;var robot=Selected;var actor=Actor;
            if(human==null || !human.Active || actor==null)return Reject("人による確認を準備できません。停止を維持しています。");
            var task=robot.CurrentTask??actor.HeldTask;
            if(choice!=GuardianDemoChoice.KeepStopped && actor.MobilityUnsafe)
                return Reject("走行の安全確認が済んでいないため、停止を維持します。");
            if(choice!=GuardianDemoChoice.KeepStopped && robot.PhysicsDriver.SafetyHeld
                && (choice!=GuardianDemoChoice.ContinueCapable || !sim.TryAcknowledgeDemoEvidence(id)))
                return Reject("人・障害物・入力の安全確認が必要です。停止を維持します。");
            GuardianHumanCommand command;
            if(choice==GuardianDemoChoice.KeepStopped)command=GuardianHumanCommand.KeepStopped;
            else if(choice==GuardianDemoChoice.Transfer)command=GuardianHumanCommand.Transfer;
            else if(task!=null && GuardianCapabilityPolicy.Allows(robot.Condition,task.Kind))
                command=task.Kind==DeliveryKind.EmptyDishes?GuardianHumanCommand.ClearDishes:GuardianHumanCommand.ContinueDelivery;
            else
            {
                int table=Enumerable.Range(0,3).Where(t=>sim.Inventory.AvailableDirty(t).Any()
                    && !sim.Tasks.Any(j=>j.TableIndex==t && j.Kind==DeliveryKind.EmptyDishes && j.Phase!=TaskPhase.Completed)).DefaultIfEmpty(-1).First();
                if(table<0)return Reject("この機体が続けられる仕事はありません。引き継ぐか、停止を続けてください。");
                human.SelectedTable=table;command=GuardianHumanCommand.ClearDishes;
            }
            GuardianHumanResult result=human.Submit(id,command,actor.Revision);
            // A consumed decision can never be replayed by a queued second click.
            StateVersion++;RefreshState();
            if(!result.Accepted)return Reject(result.Reason);
            AcceptedCount++;operationChoice=choice;operationActor=id;
            operationTask=result.TaskId==null?task:sim.Tasks.FirstOrDefault(t=>t.Id==result.TaskId);
            operating=true;OperationStatus="受理しました";Feedback="ロボット "+id+"："+result.Reason;
            Close();
            Debug.Log("[GuardianDemo] accepted "+id+" "+choice+" / "+result.Reason);return result;
        }
        private GuardianHumanResult Reject(string reason)
        {RejectedCount++;Feedback="実行できません："+reason;Debug.Log("[GuardianDemo] refused / "+reason);return new GuardianHumanResult{Accepted=false,Reason=reason};}
        public void TrackOperation()
        {
            if(!operating)return;
            var r=sim.Robots.FirstOrDefault(x=>x.RobotId==operationActor);
            if(r==null){operating=false;OperationStatus="中断：対象の機体が変わりました";return;}
            if(operationChoice==GuardianDemoChoice.KeepStopped)
            {if(r.EmergencyStopped && r.PhysicsDriver.PlanarVelocity.sqrMagnitude<.0004f){OperationStatus="完了：停止を確認しました";operating=false;}else OperationStatus="実行中：停止を確認しています";return;}
            if(operationTask?.Phase==TaskPhase.Completed){OperationStatus="完了："+operationTask.Id+" の受け渡しが終わりました";operating=false;return;}
            if(operationTask?.Phase==TaskPhase.ReturningCargo){OperationStatus="実行中：荷物を元の受け渡し場所へ返しています";return;}
            if(operationChoice==GuardianDemoChoice.Transfer)
                OperationStatus=operationTask?.AssignedTo!=null && operationTask.AssignedTo.RobotId!=operationActor?
                    "実行中：ロボット "+operationTask.AssignedTo.RobotId+" に引き継ぎました":"実行中：荷物と安全な引き継ぎ先を確認しています";
            else OperationStatus="実行中：担当の仕事を進めています";
        }
        // Laid out at 720p and scaled with the screen height, so the card reads the same at any resolution.
        private static float Scale=>Mathf.Max(1f,Screen.height/720f);
        private Rect status;
        private float statusUntil;private string statusKey="";
        private void Layout()
        {
            float s=Scale,w=Screen.width/s,h=Screen.height/s;
            header=new Rect(12,12,Mathf.Min(620,w-24),62);
            float width=Mathf.Min(860,w-24);
            panel=new Rect((w-width)/2,h-226,width,212);
            status=new Rect((w-width)/2,h-84,width,70);
        }
        private bool StatusVisible=>operating || Time.unscaledTime<statusUntil;
        /// <summary>Where a choice button is drawn on the card, in screen pixels from the top left (for the film's pointer).</summary>
        internal Rect ChoiceScreenRect(GuardianDemoChoice choice)
        {
            Layout();float s=Scale,x=panel.x+24,w=panel.width-48,y=panel.y+12+38+56;const float gap=14;float bw=(w-2*gap)/3;
            return new Rect((x+(int)choice*(bw+gap))*s,y*s,bw*s,54*s);
        }
        // Off for the Hack Day screen: only the robots, their marks and a card the viewer opens.
        public bool ShowHeader;
        public bool IsUi(Vector2 point) {Layout();var p=new Vector2(point.x,Screen.height-point.y)/Scale;return (ExtraUi!=null && ExtraUi(point)) || (sim!=null && sim.RepairCrew!=null && sim.RepairCrew.IsUi(point)) || (sim!=null && sim.HubLink!=null && sim.HubLink.IsUi(point)) || (ShowHeader && header.Contains(p)) || (CardVisible && panel.Contains(p)) || (!CardVisible && StatusVisible && status.Contains(p));}
        public void PointerDown(Vector2 p,int button) {down=last=p;pointerButton=button;pointerConsumed=IsUi(p);dragged=false;}
        public void PointerMove(Vector2 p)
        {if((p-down).sqrMagnitude>25)dragged=true;if(pointerConsumed)return;Vector2 d=p-last;last=p;if(dragged && cameraRig!=null){if(pointerButton==2)cameraRig.Pan(d);else cameraRig.Orbit(d);}}
        public bool PointerUp(Vector2 p)
        {
            bool pick=!pointerConsumed && !dragged && pointerButton==0 && !IsUi(p);pointerConsumed=true;
            if(!pick || Camera.main==null)return false;
            return Pick(Camera.main.ScreenPointToRay(p));
        }
        public bool Pick(Ray ray)
        {
            if(Physics.Raycast(ray,out var hit,100,~0,QueryTriggerInteraction.Collide))
            {var r=hit.collider.GetComponentInParent<GuardianRobot>();if(r!=null){Select(r.RobotId);RobotClicked?.Invoke(r.RobotId);return CardVisible;}}
            Close();RobotClicked?.Invoke(null);return false;
        }
        private void Update()
        {
            if(sim==null || sim.DeveloperMode || sim.CustomPresentationActive || sim.Mode!=GuardianSimulationMode.Physics)return;
            RefreshState();TrackOperation();
            if(SelectedId!=null && !NeedsDecision)Close();
            if(cameraRig==null && Camera.main!=null)
            {cameraRig=gameObject.AddComponent<GuardianSafeCamera>();cameraRig.Initialize(Camera.main);cameraRig.Pivot=new Vector3(-1,.8f,0);cameraRig.Distance=11;cameraRig.Pitch=72;}
            for(int b=0;b<3;b++)if(Input.GetMouseButtonDown(b))PointerDown(Input.mousePosition,b);
            if(Input.GetMouseButton(0)||Input.GetMouseButton(1)||Input.GetMouseButton(2))PointerMove(Input.mousePosition);
            for(int b=0;b<3;b++)if(Input.GetMouseButtonUp(b))PointerUp(Input.mousePosition);
            if(!IsUi(Input.mousePosition))cameraRig?.Zoom(Input.mouseScrollDelta.y);
            if(Input.GetKeyDown(KeyCode.Escape))Close();
        }
        private void LateUpdate() {if(sim!=null && !sim.DeveloperMode && !sim.CustomPresentationActive && sim.Mode==GuardianSimulationMode.Physics)cameraRig?.Step(Time.unscaledDeltaTime);}
        private GUIStyle titleStyle,bodyStyle,smallStyle,closeStyle,statusStyle;
        private readonly GUIStyle[] choiceStyles=new GUIStyle[3];
        private Texture2D pixel;
        private readonly System.Collections.Generic.List<Texture2D> textures=new System.Collections.Generic.List<Texture2D>();
        private static readonly Color[] ChoiceColors={new Color(.16f,.56f,.32f),new Color(.17f,.42f,.78f),new Color(.72f,.2f,.2f)};
        private static readonly string[] ChoiceLabels={"▶  できる仕事を続ける","⇄  別のロボットへ引き継ぐ","■  停止を続ける"};
        private Texture2D Solid(Color c) {var t=new Texture2D(1,1){hideFlags=HideFlags.DontSave};t.SetPixel(0,0,c);t.Apply();textures.Add(t);return t;}
        private void EnsureStyles()
        {
            if(skin!=null)return;
            skin=Instantiate(GUI.skin);font=GuardianFonts.Create(18);skin.font=font;
            skin.label.wordWrap=true;skin.button.wordWrap=true;skin.label.fontSize=16;skin.button.fontSize=16;
            pixel=Solid(Color.white);
            titleStyle=new GUIStyle(skin.label){fontSize=22,fontStyle=FontStyle.Bold,wordWrap=false};
            bodyStyle=new GUIStyle(skin.label){fontSize=17};bodyStyle.normal.textColor=new Color(.93f,.94f,.96f);
            smallStyle=new GUIStyle(skin.label){fontSize=13};smallStyle.normal.textColor=new Color(.78f,.8f,.84f);
            statusStyle=new GUIStyle(skin.label){fontSize=16,alignment=TextAnchor.MiddleLeft};statusStyle.normal.textColor=Color.white;
            closeStyle=new GUIStyle(skin.button){fontSize=20,fontStyle=FontStyle.Bold,alignment=TextAnchor.MiddleCenter};
            for(int i=0;i<3;i++)
            {
                Color c=ChoiceColors[i];
                var st=new GUIStyle(skin.button){fontSize=18,fontStyle=FontStyle.Bold,alignment=TextAnchor.MiddleCenter,wordWrap=true};
                st.normal.background=Solid(c);st.hover.background=Solid(Color.Lerp(c,Color.white,.18f));st.active.background=Solid(Color.Lerp(c,Color.black,.25f));
                st.normal.textColor=st.hover.textColor=st.active.textColor=Color.white;
                choiceStyles[i]=st;
            }
        }
        private void Fill(Rect r,Color c) {Color old=GUI.color;GUI.color=c;GUI.DrawTexture(r,pixel);GUI.color=old;}
        private static Color StateColor(GuardianRobot r)=>r.EmergencyStopped || r.PhysicsDriver.SafetyHeld?new Color(.95f,.3f,.27f):
            r.Condition==RobotCondition.Limited?new Color(.93f,.52f,.35f):r.Condition==RobotCondition.Warning?new Color(.98f,.7f,.1f):new Color(.3f,.8f,.4f);
        private static string StateText(GuardianRobot r)=>r.EmergencyStopped || r.PhysicsDriver.SafetyHeld?"停止中":
            r.Condition==RobotCondition.Limited?"制限（片付けのみ）":r.Condition==RobotCondition.Warning?"注意（スープ以外）":"通常";
        private static bool Unavailable(string reason)=>reason.Contains("できません") || reason.Contains("先に") || reason.Contains("ありません");
        public void Draw()
        {
            Layout();RefreshState();EnsureStyles();
            string key=OperationStatus+"|"+Feedback+"|"+ConversationNotice;
            if(key!=statusKey){statusKey=key;statusUntil=Time.unscaledTime+6;}
            var oldSkin=GUI.skin;var oldMatrix=GUI.matrix;GUI.skin=skin;Action action=null;
            float s=Scale;GUI.matrix=Matrix4x4.Scale(new Vector3(s,s,1));
            try
            {
                if(ShowHeader)GUI.Box(header,"GUARDIAN  |  ロボットを選んで状況を確認\nドラッグで見回す・ホイールで拡大  /  模擬入力・実センサー未接続");
                if(CardVisible)DrawCard(ref action);
                else if(StatusVisible)
                {
                    Fill(status,new Color(.05f,.06f,.08f,.88f));
                    string line=string.Join("　",new[]{OperationStatus,Feedback,ConversationNotice}.Where(t=>!string.IsNullOrEmpty(t)));
                    GUI.Label(new Rect(status.x+18,status.y+6,status.width-36,status.height-12),line,statusStyle);
                }
            }
            finally {GUI.skin=oldSkin;GUI.matrix=oldMatrix;}
            action?.Invoke();
        }
        private void DrawCard(ref Action action)
        {
            var r=Selected;
            Fill(panel,new Color(.05f,.06f,.08f,.93f));
            Color state=StateColor(r);
            Fill(new Rect(panel.x,panel.y,7,panel.height),state);
            float x=panel.x+24,y=panel.y+12,w=panel.width-48;
            titleStyle.normal.textColor=state;
            GUI.Label(new Rect(x,y,w-60,32),"ロボット "+GuardianRobotLivery.Label(r.RobotId)+"　●　"+StateText(r),titleStyle);
            if(GUI.Button(new Rect(panel.xMax-50,y,36,32),"×",closeStyle))action=Close;
            // Maintenance: the staff come, keep it stopped and repair it; a person confirms before it works again.
            if(sim.RepairCrew!=null && sim.RepairCrew.AwaitingConfirm(r.RobotId))
            {
                if(GUI.Button(new Rect(panel.xMax-50-190,y,178,32),"点検完了・再開する"))
                {string id=r.RobotId;action=()=>{if(sim.RepairCrew.Confirm(id)){OperationStatus="再開を確認しました";Feedback="ロボット "+id+"：待機場所へ戻ってから仕事を受け付けます。";}Close();};}
            }
            else if(sim.RepairCrew!=null && sim.RepairCrew.Serves(r.RobotId) && !sim.RepairCrew.Repairing(r.RobotId) && GUI.Button(new Rect(panel.xMax-50-150,y,138,32),"修理を呼ぶ"))
            {string id=r.RobotId;action=()=>{if(sim.RepairCrew.RequestNow(id)){OperationStatus="修理スタッフを呼びました";Feedback="ロボット "+id+"：停止を続けたまま修理します。再開は人が確認してから。";}Close();};}
            y+=38;
            string[] lines=Summary.Split('\n');
            GUI.Label(new Rect(x,y,w,50),(lines.Length>1?lines[1]:"")+(lines.Length>2?"\n"+lines[2]:""),bodyStyle);
            y+=56;
            if(!NeedsDecision)return;
            const float gap=14;float bw=(w-2*gap)/3;int i=0;
            foreach(GuardianDemoChoice choice in Enum.GetValues(typeof(GuardianDemoChoice)))
            {
                var current=choice;Rect button=new Rect(x+i*(bw+gap),y,bw,54);string reason=ChoiceReason(choice);
                // Bind on mouse-down, not on release: a new alert/task or
                // selection during the press must never receive the old click.
                if(Event.current.type==EventType.MouseDown && button.Contains(Event.current.mousePosition))
                {pressedActor=SelectedId;pressedSelection=SelectionVersion;pressedState=StateVersion;pressedChoice=choice;}
                Color old=GUI.color;if(Unavailable(reason))GUI.color=new Color(1,1,1,.45f);
                if(GUI.Button(button,ChoiceLabels[(int)choice],choiceStyles[(int)choice]))
                {
                    string id=pressedActor;int selection=pressedSelection,stateVersion=pressedState;
                    action=pressedChoice==choice?(Action)(()=>Choose(id,selection,stateVersion,current)):(()=>Reject("対象を確認して、もう一度選んでください。"));
                    pressedChoice=null;
                }
                GUI.color=old;
                GUI.Label(new Rect(button.x,button.yMax+4,bw,40),reason,smallStyle);
                i++;
            }
        }
        private void OnDestroy() {if(skin!=null)Destroy(skin);GuardianFonts.Release(font);foreach(var t in textures)if(t!=null)Destroy(t);}
    }
}
