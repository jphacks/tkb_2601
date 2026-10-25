"""Original, fully clothed stylized guests. Blender 5.1, meters, +Y forward.
No downloaded geometry, textures, rigs, motion capture or private robot assets.
Run via the non-input desktop helper, with --background --factory-startup.
"""
import bpy, math, json, argparse, sys, importlib.util
from pathlib import Path
from mathutils import Vector, Matrix

ROOT=Path(__file__).resolve().parents[1]
MODELS=ROOT/'Models'; MODELS.mkdir(parents=True,exist_ok=True)
PROJECT=ROOT.parents[3]
EVIDENCE=PROJECT/'docs/evidence/rigged-guests/adult-baseline'
EVIDENCE.mkdir(parents=True,exist_ok=True)
FPS=30
FLOOR=.015
SEAT=.5475
args=argparse.ArgumentParser()
args.add_argument('--render',action='store_true')
args.add_argument('--profile',default='GuestAdult01')
args.add_argument('--keep-scene',action='store_true')
opts=args.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
sys.path.insert(0,str(ROOT/'Source'))
from guest_profiles import PROFILES
MODEL=opts.profile;P=PROFILES[MODEL]
if MODEL!='GuestAdult01':EVIDENCE=PROJECT/'docs/evidence/rigged-guests/family'/MODEL;EVIDENCE.mkdir(parents=True,exist_ok=True)
if not opts.keep_scene:bpy.ops.wm.read_factory_settings(use_empty=True)
if 'guardian_restaurant_kit' not in sys.modules:
 spec=importlib.util.spec_from_file_location('guardian_restaurant_kit',ROOT.parent/'Source/generate_restaurant.py')
 kit=importlib.util.module_from_spec(spec);spec.loader.exec_module(kit);sys.modules['guardian_restaurant_kit']=kit
else:kit=sys.modules['guardian_restaurant_kit']
COLORS={'GuestSkin':(.65,.44,.31,1),'GuestHair':(.075,.065,.055,1),
 'GuestShirt':(.38,.45,.37,1),'GuestTrousers':(.18,.23,.28,1),
 'GuestShoe':(.055,.06,.07,1),'GuestEye':(.035,.035,.03,1),'GuestIvory':(.93,.91,.85,1)}
MATS={}
COLORS.update(GuestSkin=P['skin'],GuestHair=P['hair'],GuestShirt=P['shirt'],GuestTrousers=P['pants'])
for name,color in COLORS.items():
 m=bpy.data.materials.new(name if MODEL=='GuestAdult01' else MODEL+'_'+name[5:]);m.diffuse_color=color;m.use_nodes=True
 m.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value=color
 m.node_tree.nodes['Principled BSDF'].inputs['Roughness'].default_value=.76
 MATS[name]=m

def v(x):return Vector(x)
def blend(a,b,t):return a*(1-t)+b*t
def smooth(t):t=max(0,min(1,t));return t*t*(3-2*t)
verts=[];faces=[];weights=[];materials=[];regions=[]
def mesh_part(points,polys,mat,weight_fn,region):
 base=len(verts)
 for p in points:
  p=v(p);verts.append(tuple(p));weights.append(weight_fn(p));regions.append(region)
 faces.extend(tuple(base+i for i in f) for f in polys);materials.extend([list(MATS).index(mat)]*len(polys))
def oval(center,radii,mat,bone,region,segments=20,rings=12):
 center=v(center);radii=v(radii);points=[];polys=[]
 for j in range(rings+1):
  theta=.001+(math.pi-.002)*j/rings
  for i in range(segments):
   phi=2*math.pi*i/segments
   points.append(center+v((radii.x*math.sin(theta)*math.cos(phi),radii.y*math.sin(theta)*math.sin(phi),radii.z*math.cos(theta))))
 for j in range(rings):
  for i in range(segments):
   a=j*segments+i;b=j*segments+(i+1)%segments;polys.append((a,a+segments,b+segments,b))
 polys+=[tuple(range(segments)),tuple(reversed(tuple(rings*segments+i for i in range(segments))))]
 mesh_part(points,polys,mat,lambda p:{bone:1},region)
def tube(start,end,radii,mat,upper,lower,region,shoulder=False):
 a=v(start);b=v(end);axis=(b-a).normalized()
 e=axis.cross(v((0,1,0))).normalized();f=axis.cross(e).normalized()
 points=[];polys=[];n=16;ts=[0,.06,.16,.28,.39,.44,.48,.52,.56,.61,.72,.84,.94,1]
 for t in ts:
  r=radii[0]*(1-t)+radii[1]*t
  for i in range(n):points.append(blend(a,b,t)+r*(e*math.cos(2*math.pi*i/n)+f*math.sin(2*math.pi*i/n)))
 for j in range(len(ts)-1):
  for i in range(n):
   x=j*n+i;y=j*n+(i+1)%n;polys.append((x,y,y+n,x+n))
 polys.extend([tuple(reversed(range(n))),tuple((len(ts)-1)*n+i for i in range(n))])
 def w(p):
  t=(p-a).dot(axis)/(b-a).length;mix=smooth((t-.40)/.20)
  result={upper:1-mix,lower:mix}
  if shoulder and t<.10:
   chest=(1-smooth(t/.10))*.65;result={upper:(1-chest)*(1-mix),lower:(1-chest)*mix,'Chest':chest}
  return {k:x for k,x in result.items() if x>.000001}
 mesh_part(points,polys,mat,w,region)

# Tall adult reference fits the existing unusually high .5475m chair without leg stretch.
H=1.015;L1=.42;L2=.48;ANKLE=.115
bones={}
def bone(name,a,b,parent=None,deform=True):bones[name]=(v(a),v(b),parent,deform)
bone('MotionRoot',(0,0,0),(0,0,.2),deform=False)
bone('Hips',(0,0,H),(0,0,H+.13),'MotionRoot')
bone('Spine',(0,0,H+.13),(0,0,H+.35),'Hips')
bone('Chest',(0,0,H+.35),(0,0,H+.55),'Spine')
bone('Neck',(0,0,H+.55),(0,0,H+.61),'Chest')
bone('Head',(0,0,H+.61),(0,0,H+.79),'Neck')
bone('SeatContact',(0,0,H-.115),(0,0,H-.075),'Hips',False)
for side,s in [('Left',1),('Right',-1)]:
 sh=(s*.22,0,H+.475);el=(s*.50,0,H+.475);wr=(s*.76,0,H+.475);hand=(s*.87,0,H+.475)
 bone(side+'Shoulder',(s*.025,0,H+.475),sh,'Chest')
 bone(side+'UpperArm',sh,el,side+'Shoulder');bone(side+'LowerArm',el,wr,side+'UpperArm');bone(side+'Hand',wr,hand,side+'LowerArm')
 bone('HandSocket_'+side,hand,(s*.91,0,H+.475),side+'Hand',False)
 hip=(s*.105,0,H);knee=(s*.105,0,H-L1);ankle=(s*.105,0,ANKLE);toe=(s*.105,.16,.05)
 bone(side+'UpperLeg',hip,knee,'Hips');bone(side+'LowerLeg',knee,ankle,side+'UpperLeg');bone(side+'Foot',ankle,toe,side+'LowerLeg')
 bone(side+'Toes',toe,(s*.105,.24,.05),side+'Foot')
 bone('GroundContact_'+side,(s*.105,0,FLOOR),(s*.105,.04,FLOOR),side+'Foot',False)

# Mesh loops cross elbows and knees and carry smooth two-bone weights.
oval((0,0,H),(.16,.115,.115),'GuestTrousers','Hips','pelvis')
points=[];polys=[];sections=[(.98,.16,.10),(1.08,.18,.11),(1.24,.20,.12),(1.40,.23,.115),(1.49,.225,.105),(1.55,.06,.055)]
for z,rx,ry in sections:
 for i in range(24):points.append((rx*math.cos(i*math.tau/24),ry*math.sin(i*math.tau/24),z))
for j in range(len(sections)-1):
 for i in range(24):a=j*24+i;b=j*24+(i+1)%24;polys.append((a,b,b+24,a+24))
polys.extend([tuple(reversed(range(24))),tuple((len(sections)-1)*24+i for i in range(24))])
def torso_w(p):
 if p.z<1.13:t=smooth((p.z-.98)/.15);return {'Hips':1-t,'Spine':t}
 t=smooth((p.z-1.16)/.25);return {'Spine':1-t,'Chest':t}
mesh_part(points,polys,'GuestShirt',torso_w,'torso')
oval((0,0,1.55),(.052,.052,.08),'GuestSkin','Neck','neck')
oval((0,0,1.695),(.113,.101,.145),'GuestSkin','Head','head',24,16)
# A fitted original cap of short hair, not a downloaded hairstyle.
points=[];polys=[]
for j in range(9):
 for i in range(24):
  phi=i*math.tau/24;edge=(2.05-.73*max(0,math.sin(phi))) if P['style']=='bob' else 1.72-.38*max(0,math.sin(phi))
  top=.78 if P['style']=='silver' else .002
  theta=top+(edge-top)*j/8
  points.append((.118*math.sin(theta)*math.cos(phi),.106*math.sin(theta)*math.sin(phi),1.695+.151*math.cos(theta)))
for j in range(8):
 for i in range(24):a=j*24+i;b=j*24+(i+1)%24;polys.append((a,a+24,b+24,b))
mesh_part(points,polys,'GuestHair',lambda p:{'Head':1},'hair')
for s in [-1,1]:
 oval((s*.115,0,1.682),(.019,.021,.032),'GuestSkin','Head','ear')
 oval((s*.041,.098,1.705),(.011,.006,.009),'GuestEye','Head','eye',12,8)
 oval((s*.039,.104,1.708),(.003,.002,.003),'GuestIvory','Head','eye',8,6)
oval((0,.103,1.674),(.017,.019,.024),'GuestSkin','Head','nose',12,8)
oval((0,.095,1.638),(.026,.004,.004),'GuestEye','Head','mouth',12,6)
for side,s in [('Left',1),('Right',-1)]:
 tube((s*.20,0,H+.475),(s*.76,0,H+.475),(.064,.034),'GuestShirt',side+'UpperArm',side+'LowerArm','arm_'+side,True)
 oval((s*.81,0,H+.475),(.068,.037,.022),'GuestSkin',side+'Hand','hand_'+side)
 oval((s*.79,.035,H+.475),(.030,.019,.019),'GuestSkin',side+'Hand','thumb_'+side,12,8)
 tube((s*.105,0,H),(s*.105,0,ANKLE),(.080,.041),'GuestTrousers',side+'UpperLeg',side+'LowerLeg','leg_'+side)
 # Rounded opaque everyday shoes; floor pivot matches the restaurant floor top.
 oval((s*.105,.055,.070),(.052,.130,.055),'GuestShoe',side+'Foot','shoe_'+side)

if P['style']=='bob':
 for s in [-1,1]:oval((s*.104,-.025,1.628),(.035,.076,.053),'GuestHair','Head','hair_bob')
if P['style']=='hoodie':
 oval((0,-.075,1.51),(.10,.075,.075),'GuestShirt','Chest','hood')
 for s in [-1,1]:oval((s*.07,.111,1.19),(.05,.008,.035),'GuestTrousers','Spine','pocket')
if P['style']=='silver':
 for s in [-1,1]:
  points=[];polys=[]
  for i in range(32):
   a=i*math.tau/32
   for j in range(8):
    b=j*math.tau/8;r=.025+.003*math.cos(b);points.append((s*.041+r*math.cos(a),.111+.003*math.sin(b),1.705+r*math.sin(a)))
  for i in range(32):
   for j in range(8):polys.append((i*8+j,((i+1)%32)*8+j,((i+1)%32)*8+(j+1)%8,i*8+(j+1)%8))
  mesh_part(points,polys,'GuestEye',lambda p:{'Head':1},'glasses')
 oval((0,.111,1.705),(.018,.003,.003),'GuestEye','Head','glasses')
 for z in [1.18,1.28,1.38]:oval((0,.122,z),(.009,.005,.009),'GuestIvory','Spine' if z<1.3 else 'Chest','button')

# Independently reshape rest joints and garment/body regions before binding.
def leg_z(z):
 knee=H-L1;new_knee=P['hip']-P['upperLeg'];a=FLOOR+.1*P['foot']
 return new_knee+(z-knee)*P['upperLeg']/L1 if z>=knee else a+(z-ANKLE)*P['lowerLeg']/L2
def reshape(p,region):
 p=v(p);x,y,z=p
 if region in ('head','hair','hair_bob','ear','eye','nose','mouth','glasses'):
  return v((x*P['head'],y*P['head'],P['hip']+.68*P['torso']+(z-1.695)*P['head']))
 if region.startswith(('arm_','hand_','thumb_')):
  s=1 if x>0 else -1
  return v((s*.22*P['shoulder']+(x-s*.22)*P['torso'],y*P['limb'],P['hip']+(z-H)*P['torso']))
 if region.startswith('leg_'):
  s=1 if x>0 else -1
  return v((s*.105*P['hips']+(x-s*.105)*P['limb'],y*P['limb'],leg_z(z)))
 if region.startswith('shoe_'):
  s=1 if x>0 else -1
  return v((s*.105*P['hips']+(x-s*.105)*P['foot'],y*P['foot'],FLOOR+(z-FLOOR)*P['foot']))
 blend_width=max(0,min(1,(z-H)/.475));width=P['hips']*(1-blend_width)+P['shoulder']*blend_width
 return v((x*width,y*P['hips'],P['hip']+(z-H)*(P['pelvis'] if region=='pelvis' else P['torso'])))
verts=[tuple(reshape(p,r)) for p,r in zip(verts,regions)]
new_bones={}
for name,(a,b,parent,deform) in bones.items():
 def map_joint(p):
  if name=='MotionRoot':return p.copy()
  if name=='SeatContact':return reshape(p,'pelvis')
  if 'Shoulder' in name:return v((p.x*P['shoulder'],p.y*P['torso'],P['hip']+(p.z-H)*P['torso']))
  if any(x in name for x in ['UpperArm','LowerArm','Hand']):return reshape(p,'arm_'+name)
  if any(x in name for x in ['UpperLeg','LowerLeg']):return reshape(p,'leg_'+name)
  if any(x in name for x in ['Foot','Toes','GroundContact']):return reshape(p,'shoe_'+name)
  return v((p.x*P['shoulder'],p.y*P['torso'],P['hip']+(p.z-H)*P['torso']))
 new_bones[name]=(map_joint(a),map_joint(b),parent,deform)
bones=new_bones
H=P['hip'];L1=P['upperLeg'];L2=P['lowerLeg'];ANKLE=FLOOR+.1*P['foot'];S=P['torso'];PELVIS=.115*P['pelvis'];APPROACH=P['approach']
mesh=bpy.data.meshes.new(MODEL+'_SkinnedMesh');mesh.from_pydata(verts,[],faces);mesh.update()
obj=bpy.data.objects.new(MODEL+'_Body',mesh);bpy.context.collection.objects.link(obj)
for m in MATS.values():mesh.materials.append(m)
for p,m in zip(mesh.polygons,materials):p.material_index=m;p.use_smooth=True
arm=bpy.data.armatures.new(MODEL+'_Skeleton');rig=bpy.data.objects.new(MODEL+'_Rig',arm);bpy.context.collection.objects.link(rig)
bpy.context.view_layer.objects.active=rig;rig.select_set(True);bpy.ops.object.mode_set(mode='EDIT')
for name,(a,b,parent,deform) in bones.items():
 e=arm.edit_bones.new(name);e.head=a;e.tail=b;e.use_deform=deform
 if parent:e.parent=arm.edit_bones[parent]
bpy.ops.object.mode_set(mode='OBJECT')
for name in bones:obj.vertex_groups.new(name=name)
for i,ws in enumerate(weights):
 total=sum(ws.values())
 for name,weight in ws.items():
  if weight>0:obj.vertex_groups[name].add([i],weight/total,'REPLACE')
mod=obj.modifiers.new('Linear skeletal skinning','ARMATURE');mod.object=rig;mod.use_deform_preserve_volume=False
obj.parent=rig
rest={n:arm.bones[n].matrix_local.to_quaternion() for n in bones}
posed_matrices={}
def orient(name,a,b):
 a=v(a);b=v(b);direction=b-a;original=bones[name][1]-bones[name][0]
 q=original.rotation_difference(direction)@rest[name]
 target=Matrix.Translation(a)@q.to_matrix().to_4x4()
 parent=bones[name][2]
 # PoseBone.matrix setters use the evaluated parent, which may still contain
 # the previous animation frame. Solve the local basis explicitly instead.
 basis=arm.bones[name].matrix_local.inverted()
 if parent:basis=basis@arm.bones[parent].matrix_local@posed_matrices.get(parent,arm.bones[parent].matrix_local).inverted()
 rig.pose.bones[name].matrix_basis=basis@target
 posed_matrices[name]=target
def ik(a,c,l1,l2,pole):
 axis=c-a;distance=axis.length
 if distance>l1+l2+.00001:raise ValueError('Unreachable joint '+str(distance))
 axis.normalize();d=(l1*l1-l2*l2+distance*distance)/(2*distance)
 p=v(pole)-axis*axis.dot(v(pole));p.normalize()
 return a+axis*d+p*math.sqrt(max(0,l1*l1-d*d))
def pose(state,t):
 posed_matrices.clear()
 for p in rig.pose.bones:p.matrix_basis=Matrix.Identity(4)
 seated=state in ('Seated','Handoff')
 transition=state in ('SitDown','StandUp')
 u=smooth(t if state=='SitDown' else 1-t) if transition else 1 if seated else 0
 # Move the pelvis back before lowering through the table's .72-.82m slab.
 # Feet keep their original, almost planted approach-to-seat interpolation.
 hip_y=APPROACH*(1-u)**3+P['seatHipForward']*u if transition else APPROACH if state=='SeatReady' else P['seatHipForward'] if seated else 0
 hip_x=P['approachX']*(1-u)**3 if transition else P['approachX'] if state=='SeatReady' else 0
 if transition and P['support']>FLOOR:hip_x=P['approachX']*(1-smooth((u-.35)/.65))
 hip_z=(H-.001)*(1-u)+(SEAT+PELVIS)*u
 lean=math.radians(18)*math.sin(math.pi*u) if transition else 0
 if state=='Walk':hip_z=H-.031*P['gait']+.006*P['gait']*math.cos(4*math.pi*t)
 hip=v((hip_x,hip_y,hip_z));R=Matrix.Rotation(-lean,3,'X')
 orient('Hips',hip,hip+v((0,0,.13*S)))
 p=hip+v((0,0,.13*S))
 for name,length in [('Spine',.22),('Chest',.20),('Neck',.06),('Head',.18)]:q=p+R@v((0,0,length*S));orient(name,p,q);p=q
 for side,s in [('Left',1),('Right',-1)]:
  sh=hip+v((0,0,.35*S))+R@v((s*.22*P['shoulder'],0,.125*S));base=hip+v((0,0,.35*S))+R@v((s*.025*P['shoulder'],0,.125*S))
  orient(side+'Shoulder',base,sh)
  foot_y=(APPROACH+.03)*(1-u)+P['seatFoot']*u if transition else P['seatFoot'] if seated else hip_y+.03
  foot_z=ANKLE+(P['support']-FLOOR)*u
  if transition and P['support']>FLOOR:
   foot_y=(APPROACH+.03)+(P['seatFoot']-APPROACH-.03)*smooth(min(1,u*3))
   foot_z=ANKLE+(P['support']-FLOOR)*smooth(min(1,u*3))+.03*math.sin(math.pi*u)
  if state=='Walk':
   phase=(t+(0 if s==1 else .5))%1
   if phase<.5:foot_y=(.20-.8*phase)*P['gait']
   else:q=(phase-.5)*2;foot_y=(-.20+.4*smooth(q))*P['gait'];foot_z+=.065*P['gait']*math.sin(math.pi*q)
  foot_x=s*(.105*(1-u)+P['seatFootHalf']*u)*P['hips']
  foot_x+=P['approachX']*(1-smooth((u-.2)/.8)) if transition else P['approachX'] if state=='SeatReady' else 0
  h=hip+v((s*.105*P['hips'],0,0));ankle=v((foot_x,foot_y,foot_z));knee=ik(h,ankle,L1,L2,(0,1,0))
  orient(side+'UpperLeg',h,knee);orient(side+'LowerLeg',knee,ankle)
  toe=ankle+v((0,.16,-.065))*P['foot'];orient(side+'Foot',ankle,toe);orient(side+'Toes',toe,toe+v((0,.08*P['foot'],0)))
  wrist=hip+v((s*(.20*(1-u)+.13*u)*S,(.075*(1-u)+P['seatHand']*u)*S,(-.02*(1-u)+.15*u)*S))
  # Keep fingers behind the table edge while lowering/rising, rather than
  # swinging the palm through its .72-.82m slab during the transition.
  if transition:wrist.y-=.10*S*math.sin(math.pi*u)
  if state=='Walk':wrist.y+=s*.10*S*math.sin(math.tau*t)
  reach=math.sin(math.pi*t)**2 if state in ('Handoff','HandoffStanding') else 0
  if reach:wrist=blend(wrist,hip+v((s*.10*S,.48*S,.29*S+(.06 if P['support']>FLOOR else 0))),reach)
  if state in ('Handoff','HandoffStanding') and P['seatHipForward']>0:wrist.z+=.10*S*math.sin(math.pi*t)
  elbow=ik(sh,wrist,.28*S,.26*S,(s,0,0));orient(side+'UpperArm',sh,elbow);orient(side+'LowerArm',elbow,wrist)
  hand_dir=blend(v((0,.025,-.107)),v((0,.11,0)),max(u,reach)).normalized()*.11*S
  orient(side+'Hand',wrist,wrist+hand_dir)
 bpy.context.view_layer.update()

states={'Idle':(2,True),'Walk':(1,True),'Waiting':(2,True),'SeatReady':(1,True),'SitDown':(1.6,False),'Seated':(2,True),'StandUp':(1.6,False),'Handoff':(1.6,False),'HandoffStanding':(1.6,False)}
actions={}
scene=bpy.context.scene;scene.render.fps=FPS
for state,(duration,loop) in states.items():
 action=bpy.data.actions.new(state);rig.animation_data_create();rig.animation_data.action=action;actions[state]=action
 for f in range(round(duration*FPS)+1):
  scene.frame_set(f);pose(state,f/(duration*FPS))
  for p in rig.pose.bones:
   p.rotation_mode='QUATERNION';p.keyframe_insert('location',frame=f);p.keyframe_insert('rotation_quaternion',frame=f)

def select_rig(with_mesh):
 bpy.ops.object.select_all(action='DESELECT');rig.select_set(True);obj.select_set(with_mesh);bpy.context.view_layer.objects.active=rig
rig.animation_data.action=None
for p in rig.pose.bones:p.matrix_basis=Matrix.Identity(4)
scene.frame_set(0);bpy.context.view_layer.update();select_rig(True)
bpy.ops.export_scene.fbx(filepath=str(MODELS/(MODEL+'.fbx')),use_selection=True,object_types={'ARMATURE','MESH'},axis_forward='-Z',axis_up='Y',apply_scale_options='FBX_SCALE_UNITS',add_leaf_bones=False,bake_anim=False,use_mesh_modifiers=False,embed_textures=False,path_mode='STRIP')
for state,action in actions.items():
 rig.animation_data.action=action;scene.frame_start=0;scene.frame_end=round(states[state][0]*FPS);select_rig(False)
 bpy.ops.export_scene.fbx(filepath=str(MODELS/f'{MODEL}@{state}.fbx'),use_selection=True,object_types={'ARMATURE'},axis_forward='-Z',axis_up='Y',apply_scale_options='FBX_SCALE_UNITS',add_leaf_bones=False,bake_anim=True,bake_anim_use_all_actions=False,bake_anim_use_nla_strips=False,bake_anim_simplify_factor=0,embed_textures=False,path_mode='STRIP')

# Public model/rig evidence. Unity import, deformation and furniture validation are separate.
manifest={'provenance':'Original procedural geometry, rig, colors and animation; no external inputs','blender':bpy.app.version_string,'model':MODEL,'height_m':max(p[2] for p in verts),'floor_top_m':FLOOR,'seat_top_m':SEAT,'table_top_m':.82,'body_parameters':P,'rig':'21 humanoid bones plus root/contact/socket bones','states':{n:{'duration':d,'loop':l} for n,(d,l) in states.items()},'bones':{n:{'head':list(a),'tail':list(b),'parent':p,'deform':d} for n,(a,b,p,d) in bones.items()},'materials':COLORS,'vertices':len(verts),'triangles':sum(len(f)-2 for f in faces),'blended_vertices':sum(len(w)>1 for w in weights),'max_weight_influences':max(len(w) for w in weights),'unity_validation':'Separate Unity validation required','appearance_validation':'NOT_RENDERED'}
manifest_path=ROOT/'Source'/('rigged-guest-manifest.json' if MODEL=='GuestAdult01' else MODEL+'-manifest.json')
manifest_path.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
settings=dict(modelId=MODEL,height=max(p[2] for p in verts)-FLOOR,upperLeg=L1,lowerLeg=L2,upperArm=.28*S,lowerArm=.26*S,walkSpeed=.8*P['gait'],approachX=P['approachX'],approachForward=APPROACH,supportHeight=P['support'],seatHipForward=P['seatHipForward'],expectedSeatHipTravel=math.sqrt(P['approachX']**2+(APPROACH-P['seatHipForward'])**2+(H-.001-SEAT-PELVIS)**2))
for name,color in COLORS.items():settings[name[5:].lower()+'Color']=dict(zip('rgba',color))
(ROOT/'Source'/(MODEL+'-settings.json')).write_text(json.dumps(settings,indent=2)+'\n',encoding='utf-8')
footrest_parts=set()
if MODEL=='GuestChild01':
 before=set(scene.objects)
 kit.box('Child foot support deck',(0,.415,.215),(.36,.20,.020),'Oak',.009)
 for x in [-.135,.135]:
  for y in [.35,.48]:kit.rod('Footrest splayed leg',(x*1.08,y,.015),(x,y,.205),.013,'Walnut')
 footrest_parts=set(scene.objects)-before
 bpy.ops.object.select_all(action='DESELECT')
 for part in footrest_parts:part.select_set(True)
 bpy.ops.export_scene.fbx(filepath=str(MODELS/'ChildFootrest.fbx'),use_selection=True,object_types={'MESH'},axis_forward='-Z',axis_up='Y',apply_scale_options='FBX_SCALE_UNITS',bake_anim=False,embed_textures=False,path_mode='STRIP')
# Source .blend is optional local debug state; FBX + this script are the deliverables.
rig.animation_data.action=actions['Idle'];scene.frame_set(0)
print('GUARDIAN_RIGGED_ADULT_GENERATED vertices='+str(len(verts))+' blended='+str(manifest['blended_vertices']))
if opts.render:
 # Actual Cycles CPU render of original skinned poses, never a Unity screenshot.
 rig.animation_data.action=None;rig.hide_render=True;obj.hide_render=True
 previews=[('T pose',None,0),('Idle','Idle',0),('Walk','Walk',.18),('Waiting','Waiting',0),('Sit down','SitDown',.5),('Seated','Seated',0),('Stand up','StandUp',.5),('Handoff','Handoff',.5),('Seat ready','SeatReady',0)]
 for i,(label,state,t) in enumerate(previews):
  rig.hide_render=False;obj.hide_render=False
  if state:pose(state,t)
  else:
   for p in rig.pose.bones:p.matrix_basis=Matrix.Identity(4)
   bpy.context.view_layer.update()
  offset=v(((i%3-1)*3.3,(1-i//3)*5,0))
  r=rig.copy();r.data=rig.data.copy();r.animation_data_clear();bpy.context.collection.objects.link(r);r.name='Preview '+label;r.location=offset
  m=obj.copy();m.data=obj.data;m.parent=r;m.matrix_parent_inverse=Matrix.Identity(4);m.matrix_basis=Matrix.Identity(4);bpy.context.collection.objects.link(m)
  m.modifiers[0].object=r
  rig.hide_render=True;obj.hide_render=True
  if state in ('SitDown','Seated','StandUp','Handoff','SeatReady'):
   before=set(bpy.context.scene.objects);kit.chair()
   for part in set(bpy.context.scene.objects)-before:part.location+=offset
   if state=='Handoff':
    before=set(bpy.context.scene.objects);kit.table()
    rot=Matrix.Rotation(math.pi/2,3,'Z')
    for part in set(bpy.context.scene.objects)-before:
     part.location=rot@part.location+offset+v((.46,1.4,0));part.rotation_euler.rotate_axis('Z',math.pi/2)
  curve=bpy.data.curves.new('Pose label','FONT');curve.body=label;curve.align_x='CENTER';curve.size=.19;curve.extrude=.001
  text=bpy.data.objects.new(label,curve);bpy.context.collection.objects.link(text);text.location=offset+v((0,-.12,2.05));text.rotation_euler=(v((0,14,9))-text.location).to_track_quat('Z','Y').to_euler();curve.materials.append(MATS['GuestEye'])
 bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,.014));plane=bpy.context.object
 mat=bpy.data.materials.new('Original beige backdrop');mat.diffuse_color=(.78,.72,.61,1);mat.use_nodes=True;mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value=mat.diffuse_color;plane.data.materials.append(mat)
 def light(name,pos,power,size):
  data=bpy.data.lights.new(name,'AREA');data.energy=power;data.shape='DISK';data.size=size;o=bpy.data.objects.new(name,data);bpy.context.collection.objects.link(o);o.location=pos;o.rotation_euler=(v((0,0,.8))-o.location).to_track_quat('-Z','Y').to_euler()
 light('Large soft key',(2,4,10),1800,8);light('Soft fill',(-5,0,6),1100,7)
 camera=bpy.data.objects.new('Original-pose offscreen camera',bpy.data.cameras.new('Preview camera'));bpy.context.collection.objects.link(camera);camera.location=(0,14,9);camera.rotation_euler=(v((0,.1,.65))-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.type='ORTHO';camera.data.ortho_scale=11.9;scene.camera=camera
 scene.render.engine='CYCLES';scene.cycles.device='CPU';scene.cycles.samples=24
 if scene.world is None:scene.world=bpy.data.worlds.new('Original preview world')
 scene.world.color=(.55,.55,.55)
 scene.render.resolution_x=1600;scene.render.resolution_y=1300;scene.render.resolution_percentage=100;scene.render.image_settings.file_format='PNG';scene.render.filepath=str(EVIDENCE/'adult-poses-cycles.png')
 bpy.ops.render.render(write_still=True)
 manifest['appearance_validation']='Blender Cycles CPU offscreen pose contact sheet rendered; Unity rendering and human clicking untested'
 manifest_path.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
 print('GUARDIAN_RIGGED_ADULT_RENDERED')
