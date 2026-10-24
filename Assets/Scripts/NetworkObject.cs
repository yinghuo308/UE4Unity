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
        public event Action<int,int>HealthChanged;
        public event Action<NetworkObject,NetworkObject>TargetChanged;
        private bool healthNotifyPending;
        private int previousHealth;
        private bool targetNotifyPending;
        private NetworkObject previousTarget;
        private uint previousTargetId;
        public int PrivateScore;
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
             bool changed =
                TargetObjectId != objectId ||
                !object.ReferenceEquals(target, resolved);
            if (changed &&
                !IsServerObject &&
                !targetNotifyPending)
            {
                previousTargetId = TargetObjectId;
                previousTarget = target;
                targetNotifyPending = true;
            }
            TargetObjectId = objectId;
            target = resolved;
        }
        internal void ApplyReplicatedHealth(int value)
        {
            if(IsServerObject)return;
            if(Health==value)return;
            if (!healthNotifyPending)
            {
                previousHealth=Health;
                healthNotifyPending=true;
            }
            Health=value;
        }
        void LateUpdate()
        {
            bool notifyHealth=healthNotifyPending;
            int oldHealth = previousHealth;
            int newHealth = Health;
            bool notifyTarget = targetNotifyPending;
            uint oldTargetId = previousTargetId;
            uint newTargetId = TargetObjectId;
            NetworkObject oldTarget = previousTarget;
            NetworkObject newTarget = target;
            healthNotifyPending=false;
            targetNotifyPending=false;
            previousTarget=null;
            if(IsServerObject||Registry==null)return;
            if (notifyHealth && oldHealth != newHealth)
            {
                OnRepHealth(oldHealth,newHealth);
            }
            if(this==null||Registry==null)return;
            bool targetChanged=oldTargetId!=newTargetId||!object.ReferenceEquals(oldTarget,newTarget);
            if (notifyTarget && targetChanged)
            {
                OnRepTarget(oldTarget,newTarget,oldTargetId,newTargetId);
            }
        }

        private void OnRepTarget(NetworkObject oldTarget, NetworkObject newTarget, uint oldTargetId, uint newTargetId)
        {
            TargetChanged?.Invoke(oldTarget,newTarget);
        }

        private void OnRepHealth(int oldHealth, int newHealth)
        {
            HealthChanged?.Invoke(oldHealth,newHealth);
        }
    }
}

