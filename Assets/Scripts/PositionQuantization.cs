using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public static class PositionQuantization 
    {
        public const int Scale=100;
         public static int Encode(float value)
        {
            if (float.IsNaN(value) ||float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    "Position must be finite.");
            }
            // 使用 double 进行中间计算。
            double rounded = Math.Round((double)value * Scale,
                MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue ||
                rounded > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    "Quantized position is out of range.");
            }
            return (int)rounded;
        }
        public static float Decode(int value)
        {
            return (float)((double)value / Scale);
        }
        public static Vector3 Quantize(Vector3 value)
        {
            return new Vector3(
                Decode(Encode(value.x)),
                Decode(Encode(value.y)),
                Decode(Encode(value.z)));
        }
        public static bool SamePosition(Vector3 a,Vector3 b)
        {
            return Encode(a.x) == Encode(b.x) &&
                   Encode(a.y) == Encode(b.y) &&
                   Encode(a.z) == Encode(b.z);
        }
    }
}

