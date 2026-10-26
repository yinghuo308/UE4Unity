using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class InventoryReplicator 
    {
        private readonly InventoryComponent target;
        private InventoryStateData lastQueuedState;
        public InventoryReplicator(InventoryComponent target)
        {
            if (target == null || target.Owner == null)
            {
                throw new ArgumentException("背包或所属玩家不存在。",nameof(target));
            }
            this.target = target;
            // 初始状态由玩家的 Open Bunch 发送。
            lastQueuedState =InventoryRepLayout.Capture(target);
        }
         public bool TryBuildUpdate(
            out InventoryStateData update)
        {
            update = null;
            if (target == null ||
                target.Owner == null ||
                !target.Owner.IsServerObject)
            {
                return false;
            }
            InventoryStateData current =
                InventoryRepLayout.Capture(target);
            if (!InventoryRepLayout.IsValid(current))
            {
                throw new InvalidOperationException(
                    "背包数量或容量无效。");
            }
            if (!InventoryRepLayout.HasChanged(
                    lastQueuedState,
                    current))
            {
                return false;
            }
            current.StateVersion =
                unchecked(target.StateVersion + 1);
            update = current;
            return true;
        }
        public void CommitQueued(
            InventoryStateData update)
        {
            target.StateVersion = update.StateVersion;
            lastQueuedState = new InventoryStateData
            {
                PlayerObjectId = update.PlayerObjectId,
                SubobjectId = update.SubobjectId,
                StateVersion = update.StateVersion,
                ItemCount = update.ItemCount,
                Capacity = update.Capacity
            };
        }
         public bool ApplyUpdate(
            InventoryStateData update)
        {
            if (target == null ||
                target.Owner == null ||
                target.Owner.IsServerObject ||
                !InventoryRepLayout.IsValid(update))
            {
                return false;
            }
            if (update.PlayerObjectId !=
                    target.Owner.NetObjectId ||
                update.SubobjectId != target.SubobjectId)
            {
                return false;
            }
            if (!ObjectReplicator.IsNewerVersion(
                    update.StateVersion,
                    target.StateVersion))
            {
                return false;
            }
            InventoryRepLayout.Apply(target, update);
            Debug.Log(
                $"[背包同步] Player={update.PlayerObjectId}, " +
                $"Subobject={update.SubobjectId}, " +
                $"ItemCount={update.ItemCount}, " +
                $"Capacity={update.Capacity}",
                target);

            return true;
        }
    }
}

