using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class NetworkObject : MonoBehaviour
    {
        public uint NetObjectId{get;internal set;}
        public int Health=100;
        public uint OwnerConnectId{get;internal set;}
        public bool IsServerObject{get;internal set;}
        public uint StateVersion{get;internal set;}
        internal NetworkObjectRegistry Registry{get;set;}
        private NetworkObject target;
        public uint TargetObjectId{get;internal set;}
        public NetworkObject Target
        {
            get=>target;
            set
            {
                if(Registry==null)throw new InvalidOperationException(
                        "请先注册网络对象，再设置 Target。");
                if (value != null &&
                    (value.Registry != Registry ||
                     value.NetObjectId == 0))
                {
                    throw new ArgumentException(
                        "Target 必须属于同一个对象注册表。");
                }
                Registry.SetTarget(this,value!=null?value.NetObjectId:0);
            }
        }
         internal void ApplyTargetId(uint objectId)
        {
            if (Registry == null)
            {
                throw new InvalidOperationException(
                    "对象尚未注册。");
            }

            Registry.SetTarget(this, objectId);
        }
        // 只有注册表直接修改实际保存的引用，
        // 避免再次进入 Target 的 setter。
        internal void SetResolvedTarget(
            uint objectId,
            NetworkObject resolved)
        {
            TargetObjectId = objectId;
            target = resolved;
        }
    }
}

