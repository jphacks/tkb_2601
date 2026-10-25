using System;
using UnityEngine;

namespace Guardian.UnitySim
{
    [Serializable]
    public sealed class GuestRigSettings
    {
        public string modelId="GuestAdult01";
        public float height=1.831f,upperLeg=.42f,lowerLeg=.48f,upperArm=.28f,lowerArm=.26f;
        public float walkSpeed=.8f,approachX=0,approachForward=.35f,supportHeight=.015f,seatHipForward=0;
        public float expectedSeatHipTravel=.496f;
        public Color skinColor,hairColor,shirtColor,trousersColor,shoeColor,eyeColor,ivoryColor;
        public Vector3 Approach => new Vector3(approachX,0,approachForward);
        public bool RequiresFootrest => supportHeight>.02f;
    }
}
