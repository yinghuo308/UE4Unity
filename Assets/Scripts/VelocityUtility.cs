using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public static class VelocityUtility 
    {
        public const float ChangeThreshold=0.01f;
        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x)&&IsFinite(value.y)&&IsFinite(value.z);
        }
        public static bool SameVelocity(Vector3 a, Vector3 b)
        {
            bool aStopped = a.sqrMagnitude == 0f;
            bool bStopped = b.sqrMagnitude == 0f;
            if (aStopped || bStopped)
                return aStopped && bStopped;
            return (a - b).sqrMagnitude <=
                ChangeThreshold * ChangeThreshold;
        }
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) &&
                   !float.IsInfinity(value);
        }
    }
}

