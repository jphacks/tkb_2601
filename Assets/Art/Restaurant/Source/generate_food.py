"""Original serving meals, Blender 5.1.1. No external inputs, textures or services."""
import bpy
import importlib.util
import json
import math
from pathlib import Path

source=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('restaurant_kit',source/'generate_restaurant.py')
kit=importlib.util.module_from_spec(spec); spec.loader.exec_module(kit)
colors={'FoodRice':(.88,.83,.68,1),'CurrySauce':(.43,.23,.07,1),'NoodleDough':(.73,.55,.24,1),
        'MeatBrown':(.30,.13,.065,1),'VegetableGreen':(.22,.40,.14,1),'TomatoRed':(.66,.16,.09,1)}
for name,color in colors.items():
    mat=bpy.data.materials.new(name); mat.diffuse_color=color; mat.use_nodes=True
    node=mat.node_tree.nodes.get('Principled BSDF'); node.inputs['Base Color'].default_value=color
    node.inputs['Roughness'].default_value=.6; kit.MATS[name]=mat

def oval(name,loc,scale,material):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2,radius=1,location=loc)
    obj=bpy.context.object; obj.scale=scale; kit.finish(obj,name,material)
    for face in obj.data.polygons: face.use_smooth=True
    return obj

def soup():
    kit.bowl()
    kit.lathe('Soup liquid',[(0,.127),(.116,.127),(.116,.134),(0,.134)],mat='Soup')
    for i in range(6):
        a=i*2*math.pi/6
        oval('Vegetable garnish',(.06*math.cos(a),.06*math.sin(a),.139),(.012,.008,.004),'VegetableGreen')

def curry():
    kit.plate()
    oval('Rice mound',(-.065,0,.075),(.085,.11,.045),'FoodRice')
    oval('Thick curry sauce',(.065,0,.053),(.095,.12,.018),'CurrySauce')
    for x,y in [(.06,-.055),(.10,.02),(.02,.06)]:
        kit.box('Curry vegetable',(x,y,.075),(.028,.028,.026),'TomatoRed',.008)
    for i in range(10):
        a=i*2*math.pi/10
        oval('Rice grain',(-.065+.045*math.cos(a),.07*math.sin(a),.107),(.009,.018,.006),'FoodRice')

def noodles():
    kit.plate()
    for strand in range(8):
        points=[(-.12+t*.020,-.09+strand*.025+.014*math.sin(t*.9+strand),.065+.015*math.sin(t*.6+strand)**2) for t in range(13)]
        for a,b in zip(points,points[1:]): kit.rod('Dry stir-fried noodle',a,b,.009,'NoodleDough')
    for i in range(5):
        oval('Cabbage',(-.09+i*.043,.05*math.sin(i),.095),(.025,.019,.009),'VegetableGreen')

def hamburg():
    kit.plate()
    oval('Hamburg steak',(.025,0,.087),(.10,.075,.045),'MeatBrown')
    oval('Demi sauce',(.025,0,.126),(.083,.062,.008),'CurrySauce')
    oval('Rice side',(-.10,.025,.071),(.045,.075,.037),'FoodRice')
    for i in range(3): oval('Broccoli',(-.035+i*.04,-.115,.075),(.023,.025,.025),'VegetableGreen')
    oval('Tomato',(.105,.10,.075),(.034,.025,.019),'TomatoRed')

manifest=[kit.export(name,build) for name,build in [('FoodSoup',soup),('FoodCurryRice',curry),('FoodNoodles',noodles),('FoodHamburg',hamburg)]]
(source/'food-manifest.json').write_text(json.dumps({'blender':bpy.app.version_string,'units':'meters',
    'provenance':'Original authored meal geometry; no external inputs',
    'classification':{'FoodSoup':'Soup/liquid','FoodCurryRice':'Food','FoodNoodles':'Food/dry stir-fried noodles','FoodHamburg':'Food'},'assets':manifest},indent=2)+'\n')
print('GUARDIAN_FOOD_FBX_OK',len(manifest))
