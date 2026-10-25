"""Original character construction data, meters. Story setting is not inferred ethnicity.
Dimensions are independent body/limb/head/garment parameters, not actor scaling.
"""
PROFILES={
 'GuestAdult01':dict(hip=1.015,upperLeg=.42,lowerLeg=.48,torso=1.,head=1.,shoulder=1.,hips=1.,limb=1.,foot=1.,pelvis=1.,approach=.35,seatFoot=.411,support=.015,gait=1.,style='short',skin=(.65,.44,.31,1),hair=(.075,.065,.055,1),shirt=(.38,.45,.37,1),pants=(.18,.23,.28,1)),
 'GuestFemale01':dict(hip=.895,upperLeg=.37,lowerLeg=.42,torso=.90,head=.98,shoulder=.90,hips=1.08,limb=.90,foot=.90,pelvis=.90,approach=.338,seatHipForward=.10,seatFoot=.50,seatFootHalf=.080,seatHand=.26,support=.015,gait=.90,style='bob',skin=(.78,.60,.46,1),hair=(.19,.115,.075,1),shirt=(.47,.36,.43,1),pants=(.25,.28,.32,1)),
 'GuestSenior01':dict(hip=.865,upperLeg=.35,lowerLeg=.41,torso=.92,head=.98,shoulder=.95,hips=1.12,limb=1.0,foot=.90,pelvis=.92,approach=.328,seatHipForward=.15,seatFoot=.55,seatFootHalf=.075,seatHand=.19,support=.015,gait=.85,style='silver',skin=(.70,.51,.38,1),hair=(.55,.56,.54,1),shirt=(.43,.40,.32,1),pants=(.28,.29,.27,1)),
 'GuestChild01':dict(hip=.655,upperLeg=.265,lowerLeg=.31,torso=.62,head=.91,shoulder=.70,hips=.72,limb=.65,foot=.65,pelvis=.70,approach=.08,approachX=-.45,seatHipForward=.08,seatFoot=.37,support=.225,gait=.65,style='hoodie',skin=(.74,.54,.40,1),hair=(.085,.07,.055,1),shirt=(.61,.43,.24,1),pants=(.25,.34,.40,1)),
}
for profile in PROFILES.values():
 profile.setdefault('approachX',0);profile.setdefault('seatHipForward',0);profile.setdefault('seatFootHalf',.085);profile.setdefault('seatHand',.28)
