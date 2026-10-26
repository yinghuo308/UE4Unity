using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class ItemReplicator
    {
        // 服务器当前真正持有的物品数据。
        private readonly ReplicatedItemData target;
        // 上一次成功排入当前客户端通道的物品种类。
        private int lastQueuedItemTypeId;
        // 上一次成功排入当前客户端通道的物品数量。
        private int lastQueuedCount;
        public ItemReplicator(
            ReplicatedItemData target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(
                    nameof(target));
            }
            if (target.SubobjectId == 0 ||
                target.ItemTypeId <= 0 ||
                target.Count <= 0)
            {
                throw new ArgumentException(
                    "动态物品初始数据无效。",
                    nameof(target));
            }
            this.target = target;
            // Create Bunch 已经把初始数据发送给客户端。
            // 因此创建复制器时，以当前物品状态作为同步基线。
            lastQueuedItemTypeId = target.ItemTypeId;
            lastQueuedCount = target.Count;
        }
        public bool TryBuildUpdate(out ReplicatedItemData update)
        {
            update=null;
             if (target == null ||
                target.SubobjectId == 0 ||
                target.ItemTypeId <= 0 ||
                target.Count <= 0)
            {
                return false;
            }
            bool changed =target.ItemTypeId != lastQueuedItemTypeId ||
                target.Count != lastQueuedCount;
            if (!changed)
            {
                return false;
            }
            update = new ReplicatedItemData
            {
                SubobjectId = target.SubobjectId,
                ItemTypeId = target.ItemTypeId,
                Count = target.Count,
                // 客户端用这个版本号拒绝旧包和重复包。
                StateVersion = target.StateVersion
            };
            return true;
        }
        public void CommitQueued(
            ReplicatedItemData update)
        {
            if (update == null ||
                update.SubobjectId != target.SubobjectId)
            {
                throw new ArgumentException(
                    "提交的物品状态与复制器目标不一致。",
                    nameof(update));
            }

            target.StateVersion =update.StateVersion;

            lastQueuedItemTypeId = update.ItemTypeId;
            lastQueuedCount = update.Count;
        }
    }
}

