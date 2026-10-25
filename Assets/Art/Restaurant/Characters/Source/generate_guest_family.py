"""Regenerate the four original per-body rigs and render optional pose evidence.
Run Blender --background --factory-startup --threads 2 --python-exit-code 1
--python this_file -- [--render]. No graphical launch or video is needed.
"""
import bpy,sys,runpy,math,traceback,contextlib
from pathlib import Path
from mathutils import Matrix,Vector
source=Path(__file__).resolve().parent
project=source.parents[4]
evidence=project/'docs/evidence/rigged-guests/family';evidence.mkdir(parents=True,exist_ok=True)
(project/'Logs').mkdir(parents=True,exist_ok=True)
render='--render' in sys.argv
with (project/'Logs/GuestFamilyPython.log').open('w',encoding='utf-8') as log,contextlib.redirect_stdout(log),contextlib.redirect_stderr(log):
 try:
  characters=[]
  for i,model in enumerate(['GuestAdult01','GuestFemale01','GuestSenior01','GuestChild01']):
   sys.argv=[str(source/'generate_rigged_guests.py'),'--','--profile',model]+(['--keep-scene'] if i else [])
   data=runpy.run_path(str(source/'generate_rigged_guests.py'),run_name='__main__');characters.append(data)
  if render:
   for column,data in enumerate(characters):
    rig=data['rig'];body=data['obj'];rig.animation_data.action=None
    for row,(state,t) in enumerate([('Walk',.18),('SitDown',.5),('Seated',0),('StandUp',.5)]):
     data['pose'](state,t);offset=Vector(((column-1.5)*2.7,(row-1.5)*4.8,0))
     r=rig.copy();r.data=rig.data.copy();r.animation_data_clear();bpy.context.collection.objects.link(r);r.location=offset
     m=body.copy();m.parent=r;m.matrix_parent_inverse=Matrix.Identity(4);m.matrix_basis=Matrix.Identity(4);bpy.context.collection.objects.link(m);m.modifiers[0].object=r
     if state!='Walk':
      before=set(bpy.context.scene.objects);data['kit'].chair()
      for part in set(bpy.context.scene.objects)-before:part.location+=offset
      for part in data['footrest_parts']:
       p=part.copy();p.data=part.data;bpy.context.collection.objects.link(p);p.location+=offset;p.hide_render=False
     curve=bpy.data.curves.new('Original preview label','FONT');curve.body=data['MODEL'].replace('Guest','')+' / '+state;curve.align_x='CENTER';curve.size=.15
     label=bpy.data.objects.new(curve.body,curve);bpy.context.collection.objects.link(label);label.location=offset+Vector((0,-.1,2.05));label.rotation_euler=(Vector((0,22,15))-label.location).to_track_quat('Z','Y').to_euler();curve.materials.append(data['MATS']['GuestEye'])
    rig.hide_render=True;body.hide_render=True
    for part in data['footrest_parts']:part.hide_render=True
   scene=bpy.context.scene
   bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,.014))
   mat=bpy.data.materials.new('Original family preview ground');mat.diffuse_color=(.78,.72,.61,1);mat.use_nodes=True;mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value=mat.diffuse_color;bpy.context.object.data.materials.append(mat)
   for name,pos,power,size in [('Key',(2,3,15),3000,12),('Fill',(-6,0,8),1700,9)]:
    light=bpy.data.objects.new(name,bpy.data.lights.new(name,'AREA'));bpy.context.collection.objects.link(light);light.location=pos;light.data.energy=power;light.data.size=size
   camera=bpy.data.objects.new('Family offscreen camera',bpy.data.cameras.new('Family camera'));bpy.context.collection.objects.link(camera);camera.location=(0,22,15);camera.rotation_euler=(Vector((0,0,.65))-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.type='ORTHO';camera.data.ortho_scale=14;scene.camera=camera
   scene.render.engine='CYCLES';scene.cycles.device='CPU';scene.cycles.samples=24
   if scene.world is None:scene.world=bpy.data.worlds.new('Family world')
   scene.world.color=(.55,.55,.55);scene.render.resolution_x=1800;scene.render.resolution_y=1800;scene.render.resolution_percentage=100;scene.render.image_settings.file_format='PNG';scene.render.filepath=str(evidence/'four-guests-cycles.png');bpy.ops.render.render(write_still=True)
  print('GUARDIAN_FOUR_GUESTS_GENERATED render='+str(render))
 except BaseException:traceback.print_exc();log.flush();raise
