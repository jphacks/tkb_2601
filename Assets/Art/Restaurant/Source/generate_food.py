"""Original serving meals, each on its own dish set, Blender 5.1.1. No external inputs, textures or services."""
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

# Parts named "Leftover ..." show only once the meal is eaten; "Dish ..." parts always.
def soup():
    kit.soup_bowl()
    kit.lathe('Soup liquid',[(0,.118),(.116,.118),(.116,.124),(0,.124)],mat='Soup')
    for i in range(6):
        a=i*2*math.pi/6
        oval('Vegetable garnish',(.06*math.cos(a),.06*math.sin(a),.127),(.012,.008,.004),'VegetableGreen')
    kit.lathe('Leftover soup film',[(0,.046),(.052,.046),(.052,.049),(0,.049)],mat='Soup')

def curry():
    kit.curry_plate()
    oval('Rice mound',(-.065,0,.075),(.085,.11,.045),'FoodRice')
    oval('Thick curry sauce',(.065,0,.053),(.095,.12,.018),'CurrySauce')
    for x,y in [(.06,-.055),(.10,.02),(.02,.06)]:
        kit.box('Curry vegetable',(x,y,.075),(.028,.028,.026),'TomatoRed',.008)
    for i in range(10):
        a=i*2*math.pi/10
        oval('Rice grain',(-.065+.045*math.cos(a),.07*math.sin(a),.107),(.009,.018,.006),'FoodRice')
    for x in [-.02,0,.02]:
        kit.box('Pickled radish',(x,-.107,.059),(.016,.012,.01),'TomatoRed',.003)
    oval('Leftover curry smear',(.04,-.01,.034),(.085,.06,.004),'CurrySauce')
    for x,y in [(-.05,.03),(-.03,-.04),(0,.05)]:
        oval('Leftover rice grain',(x,y,.034),(.009,.018,.005),'FoodRice')

def noodles():
    kit.square_plate()
    for strand in range(8):
        points=[(-.12+t*.020,-.09+strand*.025+.014*math.sin(t*.9+strand),.042+.015*math.sin(t*.6+strand)**2) for t in range(13)]
        for a,b in zip(points,points[1:]): kit.rod('Dry stir-fried noodle',a,b,.009,'NoodleDough')
    for i in range(5):
        oval('Cabbage',(-.09+i*.043,.05*math.sin(i),.072),(.025,.019,.009),'VegetableGreen')
    for x,y in [(.05,-.03),(.066,-.018),(.058,-.045)]:
        kit.box('Pickled ginger',(x,y,.084),(.024,.006,.005),'TomatoRed',.002)
    kit.rod('Leftover noodle strand',(-.06,-.03,.031),(.02,-.012,.031),.008,'NoodleDough')
    kit.rod('Leftover noodle strand',(.0,.04,.031),(.06,.025,.031),.008,'NoodleDough')
    oval('Leftover cabbage',(-.03,.02,.03),(.02,.015,.004),'VegetableGreen')

def hamburg():
    kit.skillet()
    oval('Hamburg steak',(.035,0,.088),(.095,.07,.04),'MeatBrown')
    oval('Demi sauce',(.035,0,.124),(.08,.058,.008),'CurrySauce')
    for y in [-.055,-.012]: oval('Broccoli',(-.105,y,.068),(.022,.024,.022),'VegetableGreen')
    for i in range(4): oval('Sweet corn',(-.11+.012*(i%2),.03+.012*i,.054),(.011,.009,.008),'NoodleDough')
    oval('Glazed carrot',(-.07,.07,.056),(.026,.016,.012),'Soup')
    oval('Leftover demi smear',(.03,0,.048),(.07,.045,.003),'CurrySauce')

def serving(name,build):
    entry=kit.export(name,build)
    lo,hi=entry['blender_bounds_min'],entry['blender_bounds_max']
    # Robot tray shelves take a .41m square footprint and .15m height at the fixed dish scale.
    assert max(abs(v) for v in lo[:2]+hi[:2])<=.2051 and hi[2]-lo[2]<=.15,(name,lo,hi)
    return entry

manifest=[serving(name,build) for name,build in [('FoodSoup',soup),('FoodCurryRice',curry),('FoodNoodles',noodles),('FoodHamburg',hamburg)]]
(source/'food-manifest.json').write_text(json.dumps({'blender':bpy.app.version_string,'units':'meters',
    'provenance':'Original authored meal geometry; no external inputs',
    'classification':{'FoodSoup':'Soup/liquid','FoodCurryRice':'Food','FoodNoodles':'Food/dry stir-fried noodles','FoodHamburg':'Food'},'assets':manifest},indent=2)+'\n')
print('GUARDIAN_FOOD_FBX_OK',len(manifest))
