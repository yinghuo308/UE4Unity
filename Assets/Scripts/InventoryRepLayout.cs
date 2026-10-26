using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    [Serializable]
    public sealed class InventoryStateData
    {
        public uint PlayerObjectId;
        public uint SubobjectId;
        public uint StateVersion;
        public int ItemCount;
        public int Capacity;
    }
    public static class InventoryRepLayout 
    {
        public static InventoryStateData Capture(
            InventoryComponent target)
        {
            return new InventoryStateData
            {
                PlayerObjectId = target.Owner.NetObjectId,
                SubobjectId = target.SubobjectId,
                StateVersion = target.StateVersion,
                ItemCount = target.ItemCount,
                Capacity = target.Capacity
            };
        }
        public static bool HasChanged(
            InventoryStateData previous,
            InventoryStateData current)
        {
            return previous == null ||
                   previous.ItemCount != current.ItemCount ||
                   previous.Capacity != current.Capacity;
        }
        public static bool IsValid(
            InventoryStateData state)
        {
            return state != null &&
                   state.PlayerObjectId != 0 &&
                   state.SubobjectId != 0 &&
                   state.Capacity >= 0 &&
                   state.ItemCount >= 0 &&
                   state.ItemCount <= state.Capacity;
        }
         public static void Apply(
            InventoryComponent target,
            InventoryStateData state)
        {
            target.ItemCount = state.ItemCount;
            target.Capacity = state.Capacity;
            target.StateVersion = state.StateVersion;
        }
    }

}
