using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    [Serializable]
    public sealed class ReplicatedItemData
    {
        // 在所属玩家对象内部唯一。
        public uint SubobjectId;
        // 物品种类，例如 1001 表示药水。
        public int ItemTypeId;
        public int Count;
        public uint StateVersion;
    }

    [Serializable]
    public sealed class SubobjectDestroyData
    {
        public uint SubobjectId;
    }
}