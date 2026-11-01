using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public static class RotationUtility
    {
        public const float ChangeThreshold=0.5f;
        public static bool SameRotation(Quaternion a,Quaternion b)
        {
            return Quaternion.Angle(a,b)<=ChangeThreshold;
        }
        public static bool TryNormalize(Quaternion value,out Quaternion normalized)
        {
            normalized=Quaternion.identity;
            if(!IsFinite(value.x)||!IsFinite(value.y) ||
                !IsFinite(value.z) ||!IsFinite(value.w))
                return false;
            double lengthSquared =
                (double)value.x * value.x +
                (double)value.y * value.y +
                (double)value.z * value.z +
                (double)value.w * value.w;
            if(lengthSquared<=1e-12)
                return false;
            double scale = 1.0 / Math.Sqrt(lengthSquared);
            normalized = new Quaternion(
                (float)(value.x * scale),
                (float)(value.y * scale),
                (float)(value.z * scale),
                (float)(value.w * scale));
            return true;
        }
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value)&&!float.IsInfinity(value);
        }
    }
}

