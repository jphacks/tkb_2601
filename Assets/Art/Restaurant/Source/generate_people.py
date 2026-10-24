"""Original static adult diners/staff, meters and floor pivots. No input art or rigs.
Blender 5.1.1: blender --factory-startup -b --python <this file>.
Seated pelvis lower surface=.545, matching the existing cushion top=.5475.
"""
import importlib.util
import json
import math
from pathlib import Path
import bpy

source=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('restaurant_kit',source/'generate_restaurant.py')
kit=importlib.util.module_from_spec(spec)
spec.loader.exec_module(kit)
PALETTE={
    'SkinWarm':(.65,.44,.31,1),'SkinLight':(.83,.67,.53,1),'SkinDeep':(.41,.29,.22,1),
    'HairDark':(.075,.065,.055,1),'HairBrown':(.20,.13,.085,1),
    'ShirtSage':(.38,.45,.37,1),'ShirtClay':(.55,.35,.28,1),'ShirtNavy':(.25,.32,.41,1),
    'PantsBeige':(.47,.43,.35,1),'PantsNavy':(.18,.23,.28,1),'LipMuted':(.40,.23,.19,1)
}
for name,color in PALETTE.items():
    mat=bpy.data.materials.new(name)
    mat.diffuse_color=color; mat.use_nodes=True
    shader=mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value=color
    shader.inputs['Roughness'].default_value=.7 if name.startswith('Skin') else .83
    kit.MATS[name]=mat


def oval(name,position,radii,material,subdivisions=2):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdivisions,radius=1,location=position)
    obj=bpy.context.object; obj.scale=radii
    kit.finish(obj,name,material)
    for face in obj.data.polygons: face.use_smooth=True
    return obj


def loft(name,sections,material,segments=16):
    vertices=[]; faces=[]
    for z,rx,ry,cy in sections:
        for i in range(segments):
            angle=i*2*math.pi/segments
            vertices.append((rx*math.cos(angle),cy+ry*math.sin(angle),z))
    for j in range(len(sections)-1):
        for i in range(segments):
            a=j*segments+i; b=j*segments+(i+1)%segments
            faces.append((a,b,b+segments,a+segments))
    faces.append(tuple(reversed(range(segments))))
    faces.append(tuple((len(sections)-1)*segments+i for i in range(segments)))
    mesh=bpy.data.meshes.new(name); mesh.from_pydata(vertices,[],faces); mesh.update()
    obj=bpy.data.objects.new(name,mesh); bpy.context.collection.objects.link(obj)
    mesh.materials.append(kit.MATS[material])
    for face in mesh.polygons: face.use_smooth=True
    return obj


def hair(head_z,style,material):
    vertices=[]; faces=[]; segments=24; rings=9
    for j in range(rings):
        t=j/(rings-1)
        for i in range(segments):
            phi=i*2*math.pi/segments
            front=max(0,math.sin(phi)); back=max(0,-math.sin(phi))
            edge=1.85-.63*front+(.75 if style=='bob' else .30)*back
            if style=='bob': edge+=.32*abs(math.cos(phi))
            theta=.015+t*(edge-.015)
            vertices.append((.123*math.sin(theta)*math.cos(phi),
                .108*math.sin(theta)*math.sin(phi),head_z+.148*math.cos(theta)))
    for j in range(rings-1):
        for i in range(segments):
            a=j*segments+i; b=j*segments+(i+1)%segments
            faces.append((a,a+segments,b+segments,b))
    faces.append(tuple(reversed(range(segments))))
    mesh=bpy.data.meshes.new('Shaped hair cap'); mesh.from_pydata(vertices,[],faces); mesh.update()
    obj=bpy.data.objects.new('Shaped '+style+' hair',mesh); bpy.context.collection.objects.link(obj)
    mesh.materials.append(kit.MATS[material])
    for face in mesh.polygons: face.use_smooth=True
    if style=='swept':
        for x,y,z in [(-.05,.015,.13),(0,.005,.145),(.055,-.015,.12)]:
            oval('Swept hair lock',(x,y,head_z+z),(.065,.075,.035),material)


def head(z,skin,style,hair_material):
    oval('Adult head',(0,0,z),(.113,.100,.14),skin,3)
    for side in [-1,1]:
        oval('Ear',(side*.111,-.005,z-.012),(.024,.020,.042),skin)
        oval('Eye',(side*.042,.096,z+.005),(.012,.007,.009),'Charcoal')
        oval('Eye highlight',(side*.040,.102,z+.007),(.003,.002,.003),'Ceramic',1)
        kit.rod('Eyebrow',(side*.025,.094,z+.034),(side*.060,.091,z+.031),.004,hair_material)
    oval('Nose',(0,.104,z-.025),(.018,.022,.029),skin)
    mouth=[(-.026,.092,z-.067),(-.013,.097,z-.072),(.013,.097,z-.072),(.026,.092,z-.067)]
    for a,b in zip(mouth,mouth[1:]): kit.rod('Quiet smile',a,b,.003,'LipMuted')
    hair(z,style,hair_material)


def hand(side,position,skin):
    x,y,z=position
    oval('Hand',(x,y,z),(.040,.050,.020),skin)
    for finger in range(4):
        xx=x+(finger-1.5)*.013
        kit.rod('Finger',(xx,y+.025,z),(xx,y+.060-(abs(finger-1.5)*.006),z-.003),.006,skin)
    oval('Thumb',(x+side*.035,y-.008,z+.002),(.018,.026,.014),skin)


def shoes(side,y):
    x=side*.102
    kit.box('Shoe sole',(x,y,.030),(.114,.28,.030),'Charcoal',.012)
    oval('Shoe upper',(x,y-.012,.075),(.057,.132,.055),'Charcoal')
    kit.box('Shoe instep seam',(x,y-.035,.121),(.073,.014,.009),'PantsBeige',.004)


def adult(seated,shirt,pants,skin,style,hair_material,staff=False):
    offset=0 if seated else .28
    pelvis=.635+offset
    oval('Pelvis trousers',(0,-.04,pelvis),(.18,.16,.09),pants)
    loft('Tailored shirt',[(.68+offset,.185,.125,-.025),(.81+offset,.19,.13,-.025),
        (1.02+offset,.22,.145,-.02),(1.115+offset,.23,.12,-.02),(1.16+offset,.095,.07,-.02)],shirt)
    oval('Neck',(0,-.01,1.20+offset),(.055,.055,.065),skin)
    head(1.36+offset,skin,style,hair_material)
    for side in [-1,1]:
        hip=(side*.102,-.04,pelvis)
        knee=(side*.102,.345,.635) if seated else (side*.102,0,.51)
        ankle=(side*.102,.40,.12) if seated else (side*.102,.015,.12)
        kit.rod('Trousers upper leg',hip,knee,.085,pants,.073)
        oval('Bent knee' if seated else 'Knee',knee,(.077,.078,.077),pants)
        kit.rod('Trousers lower leg',knee,ankle,.066,pants,.043)
        shoes(side,.44 if seated else .055)
        shoulder=(side*.245,-.025,1.105+offset)
        elbow=(side*.270,.015,.85+offset)
        wrist=(side*.14,.16,.77) if seated else (side*.12,.20,1.035)
        kit.rod('Shirt upper sleeve',shoulder,elbow,.067,shirt,.055)
        oval('Sleeve elbow',elbow,(.057,.056,.057),shirt)
        kit.rod('Shirt fore sleeve',elbow,wrist,.052,shirt,.035)
        hand(side,(side*.12,.235,.742) if seated else (side*.10,.255,1.006),skin)
        kit.rod('Visible wrist',wrist,(side*.12,.21,.75) if seated else (side*.10,.23,1.01),.026,skin)
    for z in [.87,1.00,1.10]:
        oval('Shirt button',(0,.128,z+offset),(.006,.005,.006),'Ceramic',1)
    if staff:
        kit.box('Apron bib',(0,.143,1.245),(.28,.025,.27),'Sage',.025)
        kit.box('Apron skirt',(0,.137,.91),(.36,.030,.42),'Sage',.028)
        kit.box('Apron pocket',(0,.158,.98),(.20,.014,.12),'ShirtSage',.012)
        for side in [-1,1]:
            kit.rod('Apron shoulder strap',(side*.1,.13,1.37),(side*.08,-.08,1.40),.018,'Sage')
        oval('Service cap',(0,-.012,1.79),(.128,.111,.033),'Cream')
    # Continuous cloth shading on tapered sleeves/trousers; retain flat end caps.
    for obj in bpy.context.scene.objects:
        if obj.type=='MESH' and obj.name.startswith(('Shirt upper sleeve','Shirt fore sleeve','Trousers')):
            for face in obj.data.polygons:
                face.use_smooth=len(face.vertices)<=4
    return None


builders=[('GuestSage',lambda:adult(True,'ShirtSage','PantsNavy','SkinWarm','short','HairDark')),
          ('GuestClay',lambda:adult(True,'ShirtClay','PantsBeige','SkinLight','bob','HairBrown')),
          ('GuestNavy',lambda:adult(True,'ShirtNavy','PantsBeige','SkinDeep','swept','HairDark')),
          ('KitchenStaff',lambda:adult(False,'Cream','PantsNavy','SkinWarm','short','HairDark',True))]
manifest=[kit.export(name,build) for name,build in builders]
(source/'people-manifest.json').write_text(json.dumps({'blender':bpy.app.version_string,'units':'meters',
    'origin':'Original adult human geometry and colors; no external inputs, rigs or textures',
    'seated_cushion_top':.5475,'pelvis_lower_surface':.545,'assets':manifest},indent=2)+'\n')
print('GUARDIAN_PEOPLE_FBX_OK',len(manifest))
