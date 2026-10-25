"""Original restaurant kit. Blender 5.1.1: blender -b --python <this file>.
Meters, floor-origin furniture pivots, +Y forward in Blender -> +Z in Unity.
No external inputs or downloads. Original texture pixels are generated below;
FBX geometry is self-contained and Unity assigns the owned texture assets.
"""
import bpy
import json
import math
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
MODELS = ROOT / 'Models'
MODELS.mkdir(parents=True, exist_ok=True)
(ROOT/'Textures').mkdir(parents=True,exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
MATS = {}
PALETTE = {
    'Oak': ((.68, .46, .27, 1), .0, .42),
    'Walnut': ((.27, .15, .095, 1), .0, .45),
    'Cream': ((.87, .82, .71, 1), .0, .75),
    'Steel': ((.64, .70, .71, 1), .75, .28),
    'SteelDark': ((.22, .28, .29, 1), .65, .38),
    'Ceramic': ((.94, .92, .85, 1), .0, .20),
    'Sage': ((.33, .44, .33, 1), .0, .83),
    'Charcoal': ((.055, .075, .08, 1), .15, .55),
    'Soup': ((.64, .30, .08, 1), .0, .3),
    'Tile': ((.72, .75, .72, 1), .0, .6),
    'FloorOakA': ((.73,.57,.43,1), .0, .85),
    'FloorOakB': ((.78,.62,.47,1), .0, .85),
    'FloorOakC': ((.76,.59,.44,1), .0, .85),
    'FloorOakD': ((.71,.54,.40,1), .0, .85),
    'MenuPaper': ((.90,.86,.75,1),.0,.8),
    'SoyBottle': ((.21,.11,.055,1),.0,.45),
    'GlazeIndigo': ((.16,.25,.47,1),.0,.35),
    'GlazeCeladon': ((.56,.70,.62,1),.0,.38),
    'GlazeEarth': ((.42,.27,.17,1),.0,.6),
    'CastIron': ((.10,.10,.11,1),.55,.7),
}
for name, (color, metal, rough) in PALETTE.items():
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = color
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Base Color'].default_value = color
    bsdf.inputs['Metallic'].default_value = metal
    bsdf.inputs['Roughness'].default_value = rough
    MATS[name] = mat


def finish(obj, name, mat, bevel=0):
    obj.name = name
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    obj.data.materials.append(MATS[mat])
    if bevel:
        modifier = obj.modifiers.new('Manufactured edge radius', 'BEVEL')
        modifier.width = bevel
        modifier.segments = 3
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        modifier = obj.modifiers.new('Weighted corner normals', 'WEIGHTED_NORMAL')
        modifier.keep_sharp = True
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    return obj


def box(name, loc, size, mat='Oak', bevel=.02):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    obj = bpy.context.object
    obj.dimensions = size
    return finish(obj, name, mat, bevel)


def rod(name, a, b, r=.018, mat='Steel', end_r=None):
    a, b = Vector(a), Vector(b)
    bpy.ops.mesh.primitive_cone_add(vertices=16, radius1=r, radius2=end_r if end_r is not None else r,
                                  depth=(b-a).length, location=(a+b)/2)
    obj = bpy.context.object
    obj.rotation_euler = (b-a).to_track_quat('Z', 'Y').to_euler()
    return finish(obj, name, mat, .006)


def lathe(name, profile, loc=(0, 0, 0), mat='Ceramic', segments=48, sy=1):
    vertices, faces = [], []
    for radius, z in profile:
        for i in range(segments):
            theta = i * 2 * math.pi / segments
            vertices.append((loc[0]+radius*math.cos(theta), loc[1]+sy*radius*math.sin(theta), loc[2]+z))
    for j in range(len(profile)-1):
        for i in range(segments):
            a = j*segments+i
            b = j*segments+(i+1)%segments
            faces.append((a,b,b+segments,a+segments))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(MATS[mat])
    for p in obj.data.polygons:
        p.use_smooth = True
    return obj


def handle(x, y, z, width=.25):
    rod('Handle left mount', (x-width/2, y, z), (x-width/2, y-.055, z), .013)
    rod('Handle right mount', (x+width/2, y, z), (x+width/2, y-.055, z), .013)
    rod('Rounded handle', (x-width/2, y-.055, z), (x+width/2, y-.055, z), .018)


def legs(width, depth, top, mat='Walnut'):
    for x in [-width/2+.12, width/2-.12]:
        for y in [-depth/2+.12, depth/2-.12]:
            rod('Tapered leg', (x*1.06,y*1.06,.025), (x,y,top), .035, mat, .05)


def plate(loc=(0, 0, 0)):
    return lathe('Ceramic rolled rim plate', [(0,.01),(.09,.01),(.10,.02),(.18,.045),(.2,.05),
                 (.205,.06),(.2,.068),(.18,.063),(.10,.033),(0,.028)], loc)


def bowl(loc=(0, 0, 0)):
    return lathe('Hollow bowl', [(0,.012),(.065,.012),(.09,.04),(.125,.11),(.14,.15),
                 (.139,.16),(.13,.162),(.117,.117),(.083,.048),(0,.033)], loc)


# One dish set per menu. A serving dish stays inside a .41m square and .15m height, so
# every meal fits each robot tray shelf at the fixed physical dish scale. Serving-dish
# parts are named "Dish ..."; Unity keeps them when the meal is eaten.
def curry_plate(loc=(0, 0, 0)):
    # Oval white plate with a broad indigo rim, long axis along X.
    lathe('Dish white oval curry plate', [(0,.01),(.085,.01),(.09,.016),(.15,.036),(.19,.05),(.204,.057),
          (.205,.062),(.2,.065),(.15,.061),(.10,.034),(0,.029)], loc, 'Ceramic', sy=.78)
    return lathe('Dish indigo rim band', [(.15,.0625),(.2,.0665),(.2,.069),(.15,.065),(.15,.0625)],
                 loc, 'GlazeIndigo', sy=.78)


def square_plate(loc=(0, 0, 0), size=.34):
    x, y, z = loc
    plate = box('Dish celadon square plate', (x,y,z+.018), (size,size,.016), 'GlazeCeladon', .007)
    edge = size/2-.008
    for dx, dy in [(1,0),(-1,0),(0,1),(0,-1)]:
        box('Dish celadon raised rim', (x+dx*edge,y+dy*edge,z+.033),
            (.016 if dx else size,.016 if dy else size,.018), 'GlazeCeladon', .006)
    return plate


def skillet(loc=(0, 0, 0)):
    # Oval cast-iron steak skillet resting on a walnut board.
    x, y, z = loc
    box('Dish walnut steak board', (x,y,z+.024), (.40,.29,.028), 'Walnut', .01)
    pan = lathe('Dish cast iron skillet', [(0,.038),(.15,.038),(.165,.044),(.172,.066),(.165,.068),
                (.157,.05),(0,.046)], loc, 'CastIron', sy=.74)
    rod('Dish skillet grip', (x+.166,y,z+.058), (x+.197,y,z+.06), .011, 'CastIron')
    return pan


def soup_bowl(loc=(0, 0, 0), saucer=True):
    # Earthenware bowl; served on a white saucer with a spoon.
    x, y, z = loc
    if saucer:
        lathe('Dish white saucer', [(0,.01),(.12,.01),(.17,.018),(.18,.024),(.175,.027),(.12,.021),(0,.02)], loc)
        rod('Dish soup spoon', (x-.075,y-.15,z+.031), (x+.035,y-.157,z+.029), .006)
        lathe('Dish soup spoon bowl', [(0,0),(.022,0),(.022,.004),(0,.006)], (x+.058,y-.158,z+.026), 'Steel', 16, .68)
    return lathe('Dish earthenware soup bowl', [(0,.021),(.055,.021),(.06,.03),(.10,.06),(.128,.11),(.135,.14),
                 (.13,.143),(.123,.135),(.115,.11),(.09,.065),(.05,.045),(0,.042)], loc, 'GlazeEarth')


def rice_bowl(loc=(0, 0, 0)):
    return lathe('Dish indigo rice bowl', [(0,.006),(.035,.006),(.04,.015),(.068,.05),(.075,.075),
                 (.07,.078),(.064,.05),(.035,.022),(0,.02)], loc, 'GlazeIndigo', 32)


def table():
    box('Solid oak rounded top', (0,0,.77), (1.9,1.55,.10), bevel=.065)
    box('Recessed table apron', (0,0,.66), (1.62,1.28,.14), 'Walnut', .025)
    legs(1.85,1.5,.7)
    for y in [-.52,.52]:
        box('Apron joinery rail', (0,y,.57), (1.6,.05,.12), 'Oak', .012)


def chair():
    # Faces +Y. Bent, open back; upholstered cushion and structural stretchers.
    box('Rounded timber seat', (0,0,.46), (.56,.57,.065), 'Oak', .055)
    box('Sage seat pad', (0,-.015,.51), (.49,.49,.075), 'Sage', .06)
    legs(.58,.56,.46)
    for x in [-.235,.235]:
        rod('Swept back post', (x,-.22,.4), (x,-.30,1.02), .027, 'Walnut')
        rod('Side stretcher', (x,-.19,.23), (x,.19,.23), .018, 'Oak')
    box('Crest rail', (0,-.30,1.0), (.56,.065,.12), 'Walnut', .028)
    for x in [-.15,0,.15]:
        obj = box('Open back slat', (x,-.265,.79), (.065,.045,.36), 'Oak', .018)
        obj.rotation_euler.x = math.radians(8)
    rod('Front stretcher', (-.23,.19,.24), (.23,.19,.24), .018, 'Oak')


def bench():
    box('Timber plinth', (0,0,.18), (2.6,.76,.32), 'Walnut', .04)
    box('Upholstered seat', (0,.02,.47), (2.52,.73,.19), 'Sage', .08)
    back = box('Upholstered angled back', (0,-.30,.93), (2.54,.20,.85), 'Sage', .07)
    back.rotation_euler.x = math.radians(-5)
    for x in [-1.30,1.30]:
        box('Oak end panel', (x,0,.5), (.08,.8,.88), 'Oak', .04)
    for x in [-.84,0,.84]:
        box('Back upholstery seam', (x,-.184,.95), (.014,.014,.63), 'Walnut', .005)


def kitchen_base(width=1.8, depth=.8, open_top=False):
    legs(width,depth,.25,'Steel')
    if open_top:
        # The basin extends below the counter. A closed cabinet volume would
        # put an opaque top at .88 across the bowl and hide its .68 bottom.
        for x in [-width/2+.025,width/2-.025]:
            box('Sink cabinet side panel', (x,0,.54), (.05,depth,.68), 'Steel', .012)
        for y in [-depth/2+.025,depth/2-.025]:
            box('Sink cabinet front rear panel', (0,y,.54), (width-.10,.05,.68), 'Steel', .012)
        box('Sink cabinet lower panel', (0,0,.225), (width-.10,depth-.10,.05), 'Steel', .012)
    else:
        box('Steel cabinet', (0,0,.54), (width,depth,.68), 'Steel', .025)
    for x in [-width/4,width/4]:
        box('Inset cabinet door', (x,-depth/2-.012,.52), (width/2-.035,.025,.57), 'SteelDark', .012)
        handle(x,-depth/2-.033,.69,width=.22)


def prep():
    kitchen_base(2.3)
    box('Steel rolled edge top', (0,0,.91), (2.38,.88,.07), 'Steel', .025)
    box('Splashback', (0,.4,1.04), (2.38,.05,.25), 'Steel', .015)
    box('Wood chopping board', (-.55,0,.96), (.55,.4,.035), 'Oak', .035)
    for z in [1.4,1.92]:
        box('Open wall shelf', (0,.26,z), (2.3,.35,.045), 'Steel', .015)
    for x in [-1.05,1.05]:
        rod('Shelf support', (x,.38,.95), (x,.38,2), .018)
    # Clean stacks of every dish set on the wall shelves (tops at 1.4225 and 1.9425).
    for i in range(3): soup_bowl((-.86,.26,1.4015+i*.035), saucer=False)
    for i in range(4): rice_bowl((-.47,.26,1.4165+i*.02))
    for i in range(4): square_plate((-.08,.26,1.4125+i*.02), .30)
    for i in range(3): curry_plate((.36,.26,1.4125+i*.022))
    for i in range(2): bowl((.84,.26,1.4105+i*.05))
    for i in range(2): skillet((-.55,.26,1.9325+i*.062))
    for i in range(5): lathe('Stacked white saucer', [(0,.01),(.12,.01),(.17,.018),(.18,.024),(.175,.027),
                             (.12,.021),(0,.02)], (.25,.26,1.9325+i*.012))


def sink():
    kitchen_base(1.55,.88,open_top=True)
    # Four rim pieces surround a real depressed basin; no box painted as a sink.
    for x in [-.60,.60]:
        box('Sink side drainboard', (x,0,.92), (.36,.95,.065), 'Steel', .022)
        for y in [-.3,-.15,0,.15,.3]:
            box('Pressed drainboard rib', (x,y,.957), (.28,.015,.008), 'SteelDark', .004)
    for y in [-.36,.36]:
        box('Basin rim', (0,y,.92), (.86,.23,.065), 'Steel', .022)
    box('Basin bottom', (0,0,.68), (.81,.49,.035), 'SteelDark', .06)
    for x in [-.40,.40]:
        box('Basin side', (x,0,.79), (.035,.5,.23), 'Steel', .012)
    for y in [-.25,.25]:
        box('Basin end', (0,y,.79), (.82,.035,.23), 'Steel', .012)
    lathe('Drain', [(0,0),(.045,0),(.045,.009),(0,.009)], (0,0,.70), 'Charcoal', 32)
    # Gooseneck tap along a sampled semicircle.
    pts=[(0,.32,.95),(0,.32,1.16)]
    pts += [(0,.22+.1*math.cos(i*math.pi/12),1.16+.1*math.sin(i*math.pi/12)) for i in range(13)]
    for a,b in zip(pts,pts[1:]):
        if (Vector(a)-Vector(b)).length > .001: rod('Curved faucet',a,b,.018)


def range_unit():
    kitchen_base(1.6,.88)
    box('Hob', (0,0,.91), (1.67,.96,.075), 'Steel', .025)
    box('Oven inset glass', (0,-.466,.5), (1.08,.03,.38), 'Charcoal', .03)
    handle(0,-.49,.74,.85)
    for x in [-.5,-.17,.17,.5]:
        lathe('Control knob', [(0,0),(.04,0),(.04,.03),(0,.03)], (x,-.49,.84), 'Charcoal', 20)
    for x in [-.43,.43]:
        for y in [-.23,.23]:
            lathe('Burner ring', [(.09,0),(.14,0),(.14,.018),(.09,.018),(.09,0)], (x,y,.956), 'Charcoal', 32)
            for dx,dy in [(1,0),(0,1)]:
                box('Burner trivet', (x,y,.985), (.34 if dx else .025,.34 if dy else .025,.025), 'SteelDark', .008)
    lathe('Soup stockpot', [(0,.0),(.14,0),(.16,.03),(.16,.28),(.145,.28),(.145,.03),(0,.03)], (-.43,.23,1.01), 'Steel')
    for x in [-.65,-.21]:
        rod('Pot handle', (x,.16,1.2), (x,.30,1.2), .018)
    # Angled extraction hood is a designed trapezoid mesh.
    verts=[(-.9,-.52,1.9),(.9,-.52,1.9),(.9,.5,1.9),(-.9,.5,1.9),
           (-.6,-.25,2.2),(.6,-.25,2.2),(.6,.4,2.2),(-.6,.4,2.2)]
    mesh=bpy.data.meshes.new('Hood taper'); mesh.from_pydata(verts,[],[(0,3,2,1),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7),(4,5,6,7)])
    obj=bpy.data.objects.new('Tapered extraction hood',mesh); bpy.context.collection.objects.link(obj); obj.data.materials.append(MATS['Steel'])
    box('Hood flue', (0,.2,2.46), (.6,.44,.55), 'Steel', .015)
    for x in [-.6,-.3,0,.3,.6]:
        box('Hood vent', (x,-.518,1.96), (.20,.009,.025), 'SteelDark', .006)


def pickup():
    # Width X .85, depth Y 1.55; restaurant service face is local +X.
    box('Oak counter body', (0,0,.53), (.78,1.48,1), 'Oak', .035)
    box('Cream quartz worktop', (0,0,1.07), (.9,1.6,.085), 'Ceramic', .035)
    for y in [-.55,-.28,0,.28,.55]:
        box('Front fluted oak', (.398,y,.58), (.025,.065,.77), 'Walnut', .012)
    for y in [-.65,.65]:
        rod('Pass shelf post', (-.32,y,1.1), (-.32,y,1.88), .023, 'Steel')
    # Worktop and shelf stay clear: GuardianDishInventory places the waiting meals.
    box('Open pickup shelf', (-.13,0,1.65), (.5,1.58,.055), 'Oak', .025)


def returns():
    # Open trolley shelving, raised tray rim, ceramic stack and grip; no solid block.
    for x in [-.48,.48]:
        for y in [-1.12,1.12]:
            rod('Tubular upright', (x,y,.13), (x,y,1.12), .025, 'Steel')
            rod('Caster stem', (x,y,.07), (x,y,.2), .022, 'SteelDark')
            obj=lathe('Caster wheel', [(0,0),(.075,0),(.075,.045),(0,.045)], mat='Charcoal', segments=24)
            obj.rotation_euler.x=math.pi/2; obj.location=(x,y+.02,.075)
    for z in [.23,.64,1.03]:
        box('Open shelf tray', (0,0,z), (1.05,2.4,.055), 'Steel', .025)
        for x in [-.5,.5]: box('Tray rim', (x,0,z+.05), (.025,2.35,.07), 'Steel', .012)
        for y in [-1.15,1.15]: box('Tray end rim', (0,y,z+.05), (1,.025,.07), 'Steel', .012)
    # Returned dish sets on the top (1.0575) and middle (.6675) trays.
    for i in range(4): plate((0,-.78,1.0475+i*.024))
    for i in range(3): curry_plate((0,-.28,1.0475+i*.022))
    for i in range(3): square_plate((0,.2,1.0475+i*.02))
    for i in range(3): soup_bowl((0,.68,1.0365+i*.035), saucer=False)
    for i in range(2): skillet((0,-.5,.6575+i*.062))
    for i in range(3): bowl((0,.45,.6555+i*.045))
    for x in [-.48,.48]: rod('Grip support', (x,1.1,1.03), (x,1.3,1.2), .024)
    rod('Push grip', (-.48,1.3,1.2), (.48,1.3,1.2), .03, 'Walnut')


def cup():
    lathe('Hollow ceramic cup', [(0,.005),(.065,.005),(.075,.13),(.07,.14),(.06,.14),(.055,.022),(0,.022)])
    pts=[(.074+.045*math.sin(t*math.pi/12),0,.075+.04*math.cos(t*math.pi/12)) for t in range(13)]
    for a,b in zip(pts,pts[1:]): rod('Cup loop handle',a,b,.012,'Ceramic')


def table_details():
    box('Menu oak base',(0,.48,.025),(.30,.18,.05),'Oak',.018)
    box('Menu timber frame',(0,.48,.19),(.26,.04,.32),'Walnut',.015)
    paper=box('Printed original menu',(0,.454,.19),(.22,.008,.28),'MenuPaper',.004)
    for polygon in paper.data.polygons:
        for index in polygon.loop_indices:
            vertex=paper.data.vertices[paper.data.loops[index].vertex_index].co
            paper.data.uv_layers.active.data[index].uv=(vertex.x/.22+.5,vertex.z/.28+.5)
    rod('Menu rear support',(0,.52,.30),(0,.57,.04),.012,'Walnut')
    box('Condiment tray',(0,-.48,.024),(.30,.21,.045),'Walnut',.02)
    lathe('Soy sauce bottle',[(0,0),(.035,0),(.037,.09),(.022,.12),(.015,.14),(0,.14)],(-.075,-.48,.048),'SoyBottle',32)
    lathe('Bottle cap',[(0,0),(.022,0),(.022,.025),(0,.025)],(-.075,-.48,.185),'Charcoal',24)
    lathe('Salt shaker',[(0,0),(.024,0),(.025,.075),(.02,.09),(0,.09)],(0,-.48,.05),'Ceramic',24)
    lathe('Shaker cap',[(0,0),(.022,0),(.022,.015),(0,.015)],(0,-.48,.14),'Steel',24)
    box('Folded napkins',(.09,-.48,.10),(.07,.10,.12),'Ceramic',.006)
    box('Napkin holder',(.09,-.48,.065),(.09,.12,.055),'Oak',.012)


def original_textures():
    # Periodic analytic grain; original pixels, no photographs or external inputs.
    width,height=512,128
    image=bpy.data.images.new('Original oak grain',width=width,height=height,alpha=False)
    pixels=[]
    for y in range(height):
        for x in range(width):
            u=x/width*2*math.pi; v=y/height*2*math.pi
            wave=math.sin(17*v+.25*math.sin(2*u)+.14*math.sin(5*u+3*v))
            fiber=math.sin(53*v+.17*math.sin(3*u))
            value=.94+.023*wave+.012*fiber+.008*math.sin(7*u+13*v)
            pixels.extend((value,value*.995,value*.975,1))
    image.pixels=pixels; image.filepath_raw=str(ROOT/'Textures'/'OakGrain.png'); image.file_format='PNG'; image.save()
    width,height=128,256
    pixels=[.90,.86,.75,1]*(width*height)
    def paint(x,y,color):
        if 0<=x<width and 0<=y<height: pixels[(y*width+x)*4:(y*width+x)*4+4]=[*color,1]
    letters={'M':[17,27,21,21,17,17,17],'E':[31,16,16,30,16,16,31],
             'N':[17,25,21,19,17,17,17],'U':[17,17,17,17,17,17,14]}
    for i,c in enumerate('MENU'):
        for y,row in enumerate(letters[c]):
            for x in range(5):
                if row & (1<<(4-x)):
                    for dx in range(3):
                        for dy in range(3): paint(25+i*21+x*3+dx,225-y*3+dy,(.24,.16,.10))
    for y in [178,124,70]:
        for xx in range(12,116):
            for yy in range(y-16,y+17):
                if (xx-32)**2+(yy-y)**2<15**2: paint(xx,yy,(.72,.45,.26))
                if 57<xx<112 and yy in range(y+3,y+7): paint(xx,yy,(.38,.30,.22))
                if 57<xx<94 and yy in range(y-7,y-4): paint(xx,yy,(.55,.44,.32))
    image=bpy.data.images.new('Original menu artwork',width=width,height=height,alpha=False)
    image.pixels=pixels; image.filepath_raw=str(ROOT/'Textures'/'MenuArtwork.png'); image.file_format='PNG'; image.save()


def room():
    box('Floor foundation', (0,0,-.22), (20,12.5,.4), 'Oak', .015)
    for i in range(45):
        board=box('Oak floor board', (0,-6.05+i*.275,.002), (19.8,.269,.027), 'FloorOak'+'ABCD'[i%4], 0)
        for polygon in board.data.polygons:
            for index in polygon.loop_indices:
                vertex=board.data.vertices[board.data.loops[index].vertex_index].co
                board.data.uv_layers.active.data[index].uv=(vertex.x/2.5+i*.17,vertex.y/.275+i*.11)
    box('Beige back wall', (0,6.13,1.63), (19.8,.18,3.25), 'Cream', .015)
    box('Beige side wall', (-9.83,0,1.63), (.18,12.1,3.25), 'Cream', .015)
    for z in [.12,1.10,3.09]:
        box('Back timber trim', (0,6.01,z), (19.6,.1,.12), 'Walnut', .012)
        box('Side timber trim', (-9.72,0,z), (.1,12,.12), 'Walnut', .012)
    for i in range(39):
        box('Oak wainscot panel', (-9.5+i*.5,6.01,.59), (.48,.055,.88), 'Oak', .008)
    for i in range(5):
        x=-6+3.25*i
        box('Menu timber frame', (x,6,2.43), (.85,.1,.6), 'Walnut', .02)
        box('Menu ivory paper', (x,5.94,2.43), (.70,.014,.46), 'Ceramic', .003)
    for x in range(5):
        for y in range(5):
            box('Kitchen tile', (-9.2+x*.64,3.15+y*.64,.019), (.62,.62,.018), 'Tile', .003)


def export(name, build):
    for obj in list(bpy.data.objects): bpy.data.objects.remove(obj, do_unlink=True)
    build()
    meshes=list(bpy.context.scene.objects)
    bpy.ops.object.select_all(action='SELECT')
    # Keep semantic component names in the FBX; each is an editable authored mesh.
    bpy.ops.export_scene.fbx(filepath=str(MODELS/(name+'.fbx')), use_selection=True,
        object_types={'MESH'}, axis_forward='-Z',axis_up='Y',apply_unit_scale=True,
        bake_anim=False, add_leaf_bones=False, use_mesh_modifiers=True, mesh_smooth_type='FACE',
        path_mode='STRIP', embed_textures=False)
    vertices=[obj.matrix_world@v.co for obj in meshes for v in obj.data.vertices]
    lo=[min(v[i] for v in vertices) for i in range(3)]
    hi=[max(v[i] for v in vertices) for i in range(3)]
    return {'name':name,'objects':len(meshes),'vertices':len(vertices),
            'triangles':sum(len(p.vertices)-2 for obj in meshes for p in obj.data.polygons),
            'blender_bounds_min':lo,'blender_bounds_max':hi}


def generate_restaurant():
    original_textures()
    manifest=[export(n,f) for n,f in [('DiningTable',table),('DiningChair',chair),('BoothBench',bench),
        ('KitchenPrep',prep),('KitchenSink',sink),('KitchenRange',range_unit),('PickupCounter',pickup),
        ('ReturnCounter',returns),('Plate',plate),('Bowl',bowl),('Cup',cup),('RoomShell',room),('TableDetails',table_details)]]
    (ROOT/'Source'/'mesh-manifest.json').write_text(json.dumps({'blender':bpy.app.version_string,
        'units':'meters','source':'Original procedural geometry; no external input assets', 'assets':manifest},indent=2)+'\n')
    print('GUARDIAN_RESTAURANT_FBX_OK',len(manifest))


if __name__=='__main__':
    generate_restaurant()
