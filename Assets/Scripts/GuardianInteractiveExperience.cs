using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed class GuardianInteractionTarget : MonoBehaviour {public string DisplayName;public GuardianRobot Robot;public GuardianPedestrianMotion Person;}
    public sealed class GuardianInteractiveExperience : MonoBehaviour
    {
        public GameObject AdultPrefab;
        public GuardianSimulator Sim {get;private set;}public GuardianPedestrianMotion Person {get;private set;}public GuardianSafeCamera CameraRig {get;private set;}
        public GuardianEvidenceInput Inputs {get;private set;}public GuardianPedestrianSafety Safety {get;private set;}
        public GuardianInteractionTarget Selected {get;private set;}public int SelectionVersion {get;private set;}
        public bool Ready {get;private set;}public string Feedback {get;private set;}="";
        private readonly Queue<string> trace=new Queue<string>();public IEnumerable<string> Trace=>trace;
        private LineRenderer ring;private Material ringMaterial;private GUISkin skin;private Vector2 scroll;
        private bool pointerConsumed,dragged;private int pointerButton;private Vector2 down,last;
        private GuardianMotionHold[] lastHolds;private RobotCondition[] lastConditions;
        public struct Layout {public Rect Panel,Alert,Viewport;}
        public static Layout ComputeLayout(float width,float height)
        {
            if(width<720) {float h=height*.42f;return new Layout{Panel=new Rect(0,height-h,width,h),Alert=new Rect(8,8,Mathf.Max(1,width-16),42),Viewport=new Rect(0,h/height,1,1-h/height)};}
            float w=Mathf.Clamp(width*.30f,280,410);return new Layout{Panel=new Rect(width-w,0,w,height),Alert=new Rect(8,8,width-w-16,42),Viewport=new Rect(0,0,(width-w)/width,1)};
        }
        private IEnumerator Start()
        {
            Sim=GetComponent<GuardianSimulator>();while(Sim.Robots.Count==0 || Sim.Inventory==null)yield return null;
            if(AdultPrefab==null)throw new InvalidOperationException("Original adult prefab reference missing");
            Sim.CustomPresentationActive=true;
            foreach(var r in Sim.Robots)
            {
                var hold=r.GetComponent<GuardianMotionInterlock>()??r.gameObject.AddComponent<GuardianMotionInterlock>();r.PhysicsDriver.AttachSafetyInterlock(hold);
                var target=r.gameObject.AddComponent<GuardianInteractionTarget>();target.Robot=r;target.DisplayName="Robot "+r.RobotId;
            }
            GameObject adult=Instantiate(AdultPrefab,transform);adult.name="Original adult crossing";Person=adult.AddComponent<GuardianPedestrianMotion>();Person.Initialize();
            var personTarget=adult.AddComponent<GuardianInteractionTarget>();personTarget.Person=Person;personTarget.DisplayName="成人のお客さん（模擬）";
            Inputs=new GuardianEvidenceInput();Inputs.Initialize(Sim);Safety=new GuardianPedestrianSafety();Safety.Initialize(Sim,Person);
            CameraRig=gameObject.AddComponent<GuardianSafeCamera>();CameraRig.Initialize(Camera.main);
            var marker=new GameObject("Selected actor marker");ring=marker.AddComponent<LineRenderer>();ring.loop=true;ring.positionCount=40;ring.startWidth=ring.endWidth=.035f;ringMaterial=new Material(Shader.Find("Sprites/Default"));ring.sharedMaterial=ringMaterial;ring.startColor=ring.endColor=new Color(.1f,.8f,1);ring.gameObject.SetActive(false);
            lastHolds=new GuardianMotionHold[Sim.Robots.Count];lastConditions=new RobotCondition[Sim.Robots.Count];Sim.PhysicsStepBeginning+=Step;Ready=true;BeginDemo();
        }
        public void BeginDemo()
        {
            if(!Ready)return;
            Sim.HumanExperience?.EndWithoutReset();Sim.OperatorSupervisionEnabled=false;Sim.Settings.RandomFaults=false;Sim.AutomaticTasks=false;Sim.Settings.MealSeconds=600;Sim.Settings.CollectEmptyOnDelivery=false;
            Sim.Settings.Physical.Enabled=true;Sim.ResetScenario();trace.Clear();foreach(var r in Sim.Robots)
            {
                var hold=r.GetComponent<GuardianMotionInterlock>()??r.gameObject.AddComponent<GuardianMotionInterlock>();r.PhysicsDriver.AttachSafetyInterlock(hold);
                var target=r.GetComponent<GuardianInteractionTarget>()??r.gameObject.AddComponent<GuardianInteractionTarget>();target.Robot=r;target.DisplayName="Robot "+r.RobotId;
                Sim.SetCondition(r.RobotId,RobotCondition.Normal);hold.ResetForScenario(Sim.DemoClock);
            }
            if(lastHolds.Length!=Sim.Robots.Count){lastHolds=new GuardianMotionHold[Sim.Robots.Count];lastConditions=new RobotCondition[Sim.Robots.Count];}
            Array.Clear(lastHolds,0,lastHolds.Length);Array.Clear(lastConditions,0,lastConditions.Length);
            Sim.OperatorSupervisionEnabled=true;Inputs.Initialize(Sim);Person.ResetPedestrian();Safety.ResetObservations();Safety.Evaluate();Select(null);
            Sim.QueueTask(DeliveryKind.Soup,0,GuardianMenu.Soup,"A");Sim.QueueTask(DeliveryKind.Food,1,GuardianMenu.CurryRice,"B");Sim.QueueTask(DeliveryKind.Food,2,GuardianMenu.Hamburg,"C");
            Add("Scenario: 3 concurrent delivery requests; MOCK input; simulated shared vision.");
        }
        private void Step(float clock)
        {
            if(!Ready)return;Inputs.Tick(Time.realtimeSinceStartupAsDouble);foreach(var r in Sim.Robots)r.PhysicsDriver.RefreshLoad();Person.Step(Time.fixedDeltaTime);Safety.Evaluate();
            for(int i=0;i<Sim.Robots.Count;i++)
            {
                var r=Sim.Robots[i];var hold=r.GetComponent<GuardianMotionInterlock>();var flags=hold.Current|hold.Latched;
                if(flags!=lastHolds[i]){Add(r.RobotId+" 局所停止: "+flags+" / "+(Safety.Observed?"模擬観測":"遮蔽・未観測"));lastHolds[i]=flags;}
                if(r.Condition!=lastConditions[i]){Add(r.RobotId+" 能力状態: "+r.Condition+" / "+Inputs.Channels[r.RobotId].Status);lastConditions[i]=r.Condition;}
            }
        }
        public void Add(string message) {if(trace.Count==128)trace.Dequeue();trace.Enqueue(Sim.DemoClock.ToString("F2")+" "+message);Feedback=message;}
        public bool IsUi(Vector2 screen)
        {Layout l=ComputeLayout(Screen.width,Screen.height);Vector2 gui=new Vector2(screen.x,Screen.height-screen.y);return l.Panel.Contains(gui)||l.Alert.Contains(gui)||!CameraRig.View.pixelRect.Contains(screen);}
        public void PointerDown(Vector2 screen,int button)
        {down=last=screen;pointerButton=button;pointerConsumed=IsUi(screen);dragged=false;}
        public void PointerMove(Vector2 screen)
        {
            if((screen-down).sqrMagnitude>16)dragged=true;if(pointerConsumed)return;Vector2 delta=screen-last;last=screen;
            if(pointerButton==1)CameraRig.Orbit(delta);else if(pointerButton==2)CameraRig.Pan(delta);
        }
        public bool PointerUp(Vector2 screen)
        {bool choose=!pointerConsumed && !dragged && pointerButton==0 && !IsUi(screen);pointerConsumed=true;return choose && Pick(CameraRig.View.ScreenPointToRay(screen));}
        public bool Pick(Ray ray)
        {
            if(!Physics.Raycast(ray,out var hit,80,~0,QueryTriggerInteraction.Collide))return false;
            var target=hit.collider.GetComponentInParent<GuardianInteractionTarget>();if(target==null)return false;Select(target);return true;
        }
        public void Select(GuardianInteractionTarget target)
        {Selected=target;SelectionVersion++;if(target!=null)Add("Selected "+target.DisplayName);}
        public bool ResumeSafety(GuardianRobot robot)
        {
            Inputs.Tick(Time.realtimeSinceStartupAsDouble);Safety.Evaluate(true);var hold=robot.GetComponent<GuardianMotionInterlock>();
            bool accepted=hold.Release(Sim.DemoClock);Add((accepted?"ACK accepted ":"ACK refused ")+robot.RobotId+" / "+hold.Reason);return accepted;
        }
        public bool Command(GuardianHumanCommand command,int selectionVersion)
        {
            if(Selected==null || Selected.Robot==null || selectionVersion!=SelectionVersion){Add("Command refused: stale/invalid target");return false;}
            var robot=Selected.Robot;var hold=robot.GetComponent<GuardianMotionInterlock>();
            if(command==GuardianHumanCommand.KeepStopped && !Sim.HumanExperienceActive){hold.KeepStopped();Add("Operator hold "+robot.RobotId);return true;}
            if(hold.Held){Add("Command refused: local safety hold "+hold.Reason);return false;}
            if(!Sim.HumanExperienceActive){Add("No mechanical alert; use safety acknowledgement if held.");return false;}
            GuardianHumanResult result=Sim.HumanExperience.Submit(robot.RobotId,command);Add((result.Accepted?"Accepted ":"Refused ")+command+" / "+result.Reason);return result.Accepted;
        }
        private void Update()
        {
            if(!Ready)return;
            for(int b=0;b<3;b++)if(Input.GetMouseButtonDown(b))PointerDown(Input.mousePosition,b);
            if(Input.GetMouseButton(0)||Input.GetMouseButton(1)||Input.GetMouseButton(2))PointerMove(Input.mousePosition);
            if(Input.GetMouseButtonUp(0)||Input.GetMouseButtonUp(1)||Input.GetMouseButtonUp(2))PointerUp(Input.mousePosition);
            if(!IsUi(Input.mousePosition) && Mathf.Abs(Input.mouseScrollDelta.y)>.001f)CameraRig.Zoom(Input.mouseScrollDelta.y);
            if(Input.GetKeyDown(KeyCode.F)) {if(Selected!=null)CameraRig.Focus(Selected.transform);}
            if(Input.GetKeyDown(KeyCode.Home))CameraRig.ResetView();
        }
        private void LateUpdate()
        {
            if(!Ready)return;CameraRig.View.rect=ComputeLayout(Screen.width,Screen.height).Viewport;CameraRig.Step(Time.deltaTime);
            if(Selected==null){ring.gameObject.SetActive(false);return;}
            ring.gameObject.SetActive(true);float radius=Selected.Robot!=null?Selected.Robot.PhysicsDriver.TurningRadius:.45f;
            for(int i=0;i<40;i++){float a=i*Mathf.PI*2/40;ring.SetPosition(i,new Vector3(Selected.transform.position.x+Mathf.Cos(a)*radius,.035f,Selected.transform.position.z+Mathf.Sin(a)*radius));}
        }
        private void OnGUI()
        {
            if(!Ready)return;
            if(skin==null){skin=Instantiate(GUI.skin);skin.font=GuardianFonts.Create(14);skin.label.wordWrap=skin.button.wordWrap=true;}
            var old=GUI.skin;GUI.skin=skin;Layout layout=ComputeLayout(Screen.width,Screen.height);
            int holds=0;foreach(var r in Sim.Robots)if(r.GetComponent<GuardianMotionInterlock>().Held || r.EmergencyStopped)holds++;
            GUI.Box(layout.Alert,"模擬視覚 / 停止 "+holds+"台 | "+(Selected==null?"対象をクリック":Selected.DisplayName));
            GUILayout.BeginArea(layout.Panel,GUI.skin.box);scroll=GUILayout.BeginScrollView(scroll);
            GUILayout.Label("Physical AI Guardian / 最小操作体験");GUILayout.Label("左クリック: 選択 / 右ドラッグ: 回転 / 中: パン / ホイール: ズーム / F: 注目 / Home: 戻す");
            if(GUILayout.Button("模擬デモを最初から"))BeginDemo();
            foreach(var r in Sim.Robots)if(GUILayout.Button("Robot "+r.RobotId+" / "+r.Condition+" / "+(r.CurrentTask==null?"待機":r.CurrentTask.Id)))Select(r.GetComponent<GuardianInteractionTarget>());
            if(GUILayout.Button("通路の固定ビュー"))CameraRig.AislePreset();
            if(Selected!=null && GUILayout.Button("選択対象へ注目"))CameraRig.Focus(Selected.transform);
            if(Selected!=null && Selected.Robot!=null)
            {
                var r=Selected.Robot;var hold=r.GetComponent<GuardianMotionInterlock>();var input=Inputs.Channels[r.RobotId];
                var dynamics=r.PhysicsDriver.Dynamics;
                if(dynamics!=null)GUILayout.Label("仮定モデル / 質量 "+dynamics.Mass.ToString("F2")+"kg / 支持・滑り・荷物転倒の指標 "+dynamics.Risk.SupportUtilization.ToString("F2")+" / "+dynamics.Risk.SlideUtilization.ToString("F2")+" / "+dynamics.Risk.ItemTipUtilization.ToString("F2")+"（1超で仮定の限界超過）");
                GUILayout.Label(r.RobotId+" / 最後の有効入力による能力: "+r.Condition+" / "+(r.CurrentTask==null?"待機":r.CurrentTask.Id+" "+r.CurrentTask.Phase));
                GUILayout.Label("入力: "+input.Status+" / "+input.Source);GUILayout.Label("能力制限と独立の停止: "+hold.Reason);
                if(GUILayout.Button("危険解除後の停止確認・再開"))ResumeSafety(r);
                foreach(var c in new[]{GuardianHumanCommand.ContinueDelivery,GuardianHumanCommand.Transfer,GuardianHumanCommand.KeepStopped,GuardianHumanCommand.Inspect,GuardianHumanCommand.ResumeWork})
                    if(GUILayout.Button(CommandLabel(c)))Command(c,SelectionVersion);
                if(GUILayout.Button("模擬異常: 配膳不可")){if(input.Origin==GuardianInputOrigin.Mock){input.MockValue=.85f;Add("MOCK Limited evidence selected for "+r.RobotId);}else Add("先に模擬入力を選択してください");}
                if(GUILayout.Button("模擬入力へ"))Inputs.SelectSource(r.RobotId,GuardianInputOrigin.Mock,"mock");
                if(GUILayout.Button("実測アダプタ口へ（未接続）"))Inputs.SelectSource(r.RobotId,GuardianInputOrigin.Measured,"unconfigured-adapter");
            }
            if(Selected!=null && Selected.Person!=null)
                if(GUILayout.Button("後方の安全を検査して開始位置へ戻る")){Selected.Person.Retreat();Add("NPC retreat requested; swept clearance required");}
            if(Person.WaitingForClearance)GUILayout.Label("NPC: 通行が空くまで待機中");
            if(Selected!=null && Selected.Person!=null)
            {if(GUILayout.Button("決められた通路を横断")){Selected.Person.Walk();Add("NPC crossing, original Walk clip");}if(GUILayout.Button("待つ"))Selected.Person.Wait();}
            GUILayout.Label("共有視覚: "+(Safety.Observed?"模擬観測":"遮蔽 / 未観測")+" / 実センサー未接続");GUILayout.Label("応答: "+Feedback);
            foreach(string entry in trace)GUILayout.Label(entry);
            GUILayout.EndScrollView();GUILayout.EndArea();GUI.skin=old;
        }
        private void OnDestroy()
        {if(Sim!=null){Sim.PhysicsStepBeginning-=Step;Sim.CustomPresentationActive=false;}if(ring!=null)Destroy(ring.gameObject);if(ringMaterial!=null)Destroy(ringMaterial);if(skin!=null)Destroy(skin);}
        private static string CommandLabel(GuardianHumanCommand command)
        {switch(command){case GuardianHumanCommand.ContinueDelivery:return "能力を確認して配膳を継続";case GuardianHumanCommand.Transfer:return "安全な地点で他の機体へ引き継ぎ";case GuardianHumanCommand.KeepStopped:return "停止を維持";case GuardianHumanCommand.Inspect:return "停止を確認し模擬点検";case GuardianHumanCommand.ResumeWork:return "待機中の作業受付を再開";default:return command.ToString();}}
    }
}
