using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UeNet
{
    public sealed class MovementSnapshotBuffer
    {
        public const int MaxSnapshots=32;
        private readonly List<MovementStateData>snapshots=new List<MovementStateData>(MaxSnapshots);
        public int Count=>snapshots.Count;
        public bool Add(MovementStateData value)
        {
            if (!value.TryNormalize(out MovementStateData snapshot))
            {
                return false;
            }
            if (snapshots.Count > 0 &&snapshot.ServerTime <=
                    snapshots[snapshots.Count - 1].ServerTime)
            {
                return false;
            }
            if (snapshots.Count >= MaxSnapshots)
                snapshots.RemoveAt(0);
            snapshots.Add(snapshot);
            return true;
        }
        public bool TrySample(double renderTime,out MovementStateData result)
        {
            return TrySample(renderTime,0.0,out result);
        }
        public bool TrySample(double renderTime,double maxExtrapolation,out MovementStateData result)
        {
            result = default(MovementStateData);
            if (snapshots.Count == 0 ||double.IsNaN(renderTime) ||
                double.IsInfinity(renderTime) ||double.IsNaN(maxExtrapolation) ||
                double.IsInfinity(maxExtrapolation) ||maxExtrapolation < 0.0)
            {
                return false;
            }
            while (snapshots.Count > 2 &&
                   snapshots[1].ServerTime <= renderTime)
            {
                snapshots.RemoveAt(0);
            }
            MovementStateData latest =snapshots[snapshots.Count - 1];
            if (renderTime >= latest.ServerTime)
            {
                double elapsed=Math.Min(renderTime-latest.ServerTime,maxExtrapolation);
                result=latest;
                result.ServerTime=latest.ServerTime+elapsed;
                Vector3 predicted=latest.Position+latest.Velocity*(float)elapsed;
                result.Position=VelocityUtility.IsFinite(predicted)?predicted:latest.Position;
                return true;
            }
            if (snapshots.Count == 1 ||renderTime <= snapshots[0].ServerTime)
            {
                result = snapshots[0];
                return true;
            }
            MovementStateData a = snapshots[0];
            MovementStateData b = snapshots[1];
            float factor = (float)((renderTime - a.ServerTime) /(b.ServerTime - a.ServerTime));
            result = new MovementStateData
            {
                ServerTime = renderTime,
                Position = Vector3.Lerp(a.Position, b.Position, factor),
                Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, factor),
                Velocity = b.Velocity
            };
            return true;
        }
        public void Clear()
        {
            snapshots.Clear();
        }
    }
}
