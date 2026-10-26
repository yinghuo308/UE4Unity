using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class NetworkObjectRegistry
    {
        private readonly Dictionary<uint,NetworkObject> objects=new Dictionary<uint, NetworkObject>();
        private readonly Dictionary<uint,HashSet<uint>> references=new Dictionary<uint, HashSet<uint>>();
        private readonly HashSet<uint> removedIds=new HashSet<uint>();
        private uint nextObjectId=1;
        public NetworkObject RegisterServerObject(NetworkObject networkObject)
        {
             if (networkObject == null)
                throw new ArgumentNullException(
                    nameof(networkObject));
            if (networkObject.Registry != null)
            {
                throw new InvalidOperationException(
                    "对象已经注册。");
            }
            // 0 保留为无效对象编号。
            if (nextObjectId == 0)
                throw new InvalidOperationException(
                    "网络对象编号已耗尽。");
            uint id=nextObjectId;
            nextObjectId = unchecked(nextObjectId + 1);
            networkObject.IsServerObject=true;
            networkObject.NetObjectId = id;
            networkObject.Registry=this;
            objects.Add(id,networkObject);
            ResolveReferences(id);
            return networkObject;
        }
        public void RegisterClientObject(NetworkObject networkObject,uint objectId)
        {
            if (networkObject == null)
            {
                throw new ArgumentNullException(
                    nameof(networkObject));
            }

            if (objectId == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(objectId));
            }
            if (networkObject.Registry != null ||
                objects.ContainsKey(objectId) ||
                removedIds.Contains(objectId))
            {
                throw new InvalidOperationException(
                    "对象重复注册，或者编号已经销毁。");
            }
            networkObject.NetObjectId = objectId;
            networkObject.IsServerObject = false;
            networkObject.Registry=this;
            objects[objectId] = networkObject;
            ResolveReferences(objectId);
        }
        public bool TryGet(uint objectId,out NetworkObject networkObject)
        {
            return objects.TryGetValue(objectId,out networkObject);
        }
        public void Remove(uint objectId,bool permanently=true)
        {
            if(permanently)
            removedIds.Add(objectId);
            if(objects.TryGetValue(objectId,out NetworkObject removed)&&
                        removed != null)
            {
                RemoveReference(objectId,removed.TargetObjectId);
                removed.SetResolvedTarget(0,null);
                removed.Registry=null;
            }
            objects.Remove(objectId);
            if (references.TryGetValue(
                    objectId,
                    out HashSet<uint> owners))
            {
                foreach (uint ownerId in owners)
                {
                    if (objects.TryGetValue(
                            ownerId,
                            out NetworkObject owner) &&
                        owner != null &&
                        owner.TargetObjectId == objectId)
                    {
                        owner.SetResolvedTarget(0, null);
                    }
                }
                references.Remove(objectId);
            }
        }
        public IEnumerable<NetworkObject> GetAllObject()
        {
            return objects.Values;
        }
        public void SetTarget(NetworkObject owner,uint targetId)
        {
            if (owner == null ||
                owner.Registry != this ||
                !objects.TryGetValue(
                    owner.NetObjectId,
                    out NetworkObject registered) ||
                registered != owner)
            {
                throw new InvalidOperationException(
                    "引用所属对象不在当前注册表中。");
            }
            RemoveReference(owner.NetObjectId,owner.TargetObjectId);
             if (targetId == 0 ||
                removedIds.Contains(targetId))
            {
                owner.SetResolvedTarget(0, null);
                return;
            }
            if(!references.TryGetValue(targetId,out HashSet<uint> owners))
            {
                owners=new HashSet<uint>();
                references.Add(targetId,owners);
            }
            owners.Add(owner.NetObjectId);
            objects.TryGetValue(targetId,out NetworkObject resolved);
            owner.SetResolvedTarget(targetId,resolved);
        }
        private void RemoveReference(uint ownerId,uint targetId)
        {
            if(targetId==0)return;
            if(!references.TryGetValue(targetId,out HashSet<uint> owners))
            {
                return;
            }
            owners.Remove(ownerId);
            if(owners.Count==0)references.Remove(targetId);
        }
        private void ResolveReferences(uint targetId)
        {
            if (!objects.TryGetValue(
                    targetId,
                    out NetworkObject resolved) ||
                resolved == null)
            {
                return;
            }
            if (!references.TryGetValue(
                    targetId,
                    out HashSet<uint> owners))
            {
                return;
            }
            foreach (uint ownerId in owners)
            {
                if (!objects.TryGetValue(
                        ownerId,
                        out NetworkObject owner) ||
                    owner == null ||
                    owner.TargetObjectId != targetId)
                {
                    continue;
                }
                owner.SetResolvedTarget(
                    targetId,
                    resolved);
            }
        }
        public bool IsRemoved(uint objectId)
        {
            return removedIds.Contains(objectId);
        }
        public void Clear()
        {
            foreach(NetworkObject obj in objects.Values)
            {
                if(obj==null)continue;
                obj.SetResolvedTarget(0,null);
                obj.Registry=null;
            }
            references.Clear();
            removedIds.Clear();
            objects.Clear();
        }
    }

} 
