using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UeNet
{
    [Serializable]
    public struct MovementStateData
    {
        public double ServerTime;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public static MovementStateData FromState(ObjectStateData state)
        {
            return new MovementStateData
            {
                ServerTime = state.MovementServerTime,
                Position = state.Position,
                Rotation = state.Rotation,
                Velocity = state.Velocity
            };
        }
        public bool TryNormalize(out MovementStateData normalized)
        {
            normalized=default(MovementStateData);
            if(double.IsNaN(ServerTime) ||
                double.IsInfinity(ServerTime) ||ServerTime < 0.0 ||
                !VelocityUtility.IsFinite(Velocity) ||!RotationUtility.TryNormalize(
                    Rotation,out Quaternion rotation))
            {
                return false;
            }
            try
            {
                normalized=new MovementStateData
                {
                    ServerTime=ServerTime,
                    Position=PositionQuantization.Quantize(Position),
                    Rotation=rotation,
                    Velocity=Velocity
                };
                return true;
            }
            catch
            {
                return false;
            }
        }
        public void WriteTo(ObjectStateData state)
        {
            if(!TryNormalize(out MovementStateData value))
            {
                throw new ArgumentException("Invalid movement state.");
            }
            state.MovementServerTime = value.ServerTime;
            state.Position = value.Position;
            state.Rotation = value.Rotation;
            state.Velocity = value.Velocity;
        }
        public static bool SameMovement(MovementStateData a,MovementStateData b)
        {
             return PositionQuantization.SamePosition(a.Position, b.Position) &&
                   RotationUtility.SameRotation(a.Rotation, b.Rotation) &&
                   VelocityUtility.SameVelocity(a.Velocity, b.Velocity);
        }
    }
}
