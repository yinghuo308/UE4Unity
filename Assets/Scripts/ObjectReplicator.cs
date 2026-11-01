using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class ObjectReplicator 
    {
        private readonly NetworkObject target;
        private readonly RepLayout layout;
        private ObjectStateData lastSnapshot;

        private double lastFullRefreshTime;
        private const double FullRefreshInterval=1.0;
        private readonly Dictionary<ushort,uint> appliedVersions=new Dictionary<ushort, uint>();
        private uint nextServerVersion;
        public ObjectReplicator(NetworkObject target)
        {
            if(target==null)throw new ArgumentException(nameof(target));
            this.target=target;
            layout=RepLayout.Instance;
            if (!target.IsServerObject)
            {
                foreach(RepProperty property in layout.Properties)
                {
                    appliedVersions[property.PropertyId]=target.StateVersion;
                }
            }
        }
        public bool ApplyState(ObjectStateData state)
        {
            if(target==null||state==null)return false;
            if(target.IsServerObject)return false;
            if(state.ObjectId!=target.NetObjectId)
            return false;
            if (!IsNewerVersion(state.StateVersion,target.StateVersion))
            {
                return false;
            }
            layout.Apply(target,state);
            target.StateVersion=state.StateVersion;
            return true;
        }
        public static bool IsNewerVersion(uint value,uint reference)
        {
            return unchecked((int)(value-reference)>0);
        }
         public bool TryCaptureUpdate(double now,
            out ObjectUpdateData update,bool allowPeriodicRefresh,
                    bool forceFullRefresh=false)
        {
            update = null;
            if (target == null)
            {
                throw new InvalidOperationException(
                    "复制器关联的对象已被销毁。");
            }
            if (!target.IsServerObject)
            {
                throw new InvalidOperationException(
                    "只有服务器对象可以采集发送状态。");
            }
            ObjectStateData current = layout.Capture(target);
            bool fullRefresh =forceFullRefresh||
                lastSnapshot == null ||
                (now - lastFullRefreshTime >=
                    FullRefreshInterval&&allowPeriodicRefresh);
            ObjectUpdateData candidate = new ObjectUpdateData
            {
                ObjectId = target.NetObjectId,
                OwnerVersion=target.OwnerVersion
            };

            uint version =
                unchecked(nextServerVersion + 1);
            foreach (RepProperty property in layout.Properties)
            {
                bool shouldSend =
                    fullRefresh ||
                    property.HasChanged(lastSnapshot, current);
                if (!shouldSend)
                    continue;
                candidate.Properties.Add(
                    property.CreateUpdate(current, version));
            }
            if (candidate.Properties.Count == 0)
                return false;
            nextServerVersion = version;
            if (lastSnapshot == null)
            {
                lastSnapshot = new ObjectStateData
                {
                    ObjectId = target.NetObjectId
                };
            }
            // 只更新本次实际选中发送的属性基线。
            // 未发送的位置微小变化仍可继续累积。
            foreach (PropertyUpdateData item in candidate.Properties)
            {
                if (layout.TryGetProperty(
                        item.PropertyId,
                        out RepProperty property))
                {
                    property.Capture(target, lastSnapshot);
                }
            }
            if (fullRefresh)lastFullRefreshTime = now;

            update = candidate;
            return true;
        }
        public bool ApplyUpdate(ObjectUpdateData update)
        {
            if(target==null||target.IsServerObject||update==null||
                update.ObjectId!=target.NetObjectId||
                update.OwnerVersion!=target.OwnerVersion
                        ||update.Properties==null)
            return false;
            bool applied=false;
            foreach(PropertyUpdateData item in update.Properties)
            {
                if(item==null)continue;
                if(!layout.TryGetProperty(item.PropertyId,out RepProperty property))
                {
                    continue;
                }
                if(appliedVersions.TryGetValue(item.PropertyId,out uint previousVersion))
                {
                    if(!IsNewerVersion(item.PropertyVersion,previousVersion))
                    continue;
                }
                ObjectStateData decoded=new ObjectStateData();
                if(!property.TryReadUpdate(item,decoded))continue;
                property.Apply(target,decoded);
                appliedVersions[item.PropertyId]=item.PropertyVersion;
                if (IsNewerVersion(item.PropertyVersion, target.StateVersion))
                {
                    target.StateVersion=item.PropertyVersion;
                }
                applied=true;
            }
            return applied;
        }
        public void InitializeServerBaseline(double now)
        {
            if(target==null||!target.IsServerObject)return;
            lastSnapshot=layout.Capture(target);
            lastFullRefreshTime=now;
            nextServerVersion=target.StateVersion;           
        }
    }
}

