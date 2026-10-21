using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class NetworkObjectRegistry
    {
        private readonly Dictionary<uint,NetworkObject> objects=new Dictionary<uint, NetworkObject>();
        private uint nextObjectId=1;
        public NetworkObject RegisterServerObject(NetworkObject networkObject)
        {
             if (networkObject == null)
                throw new System.ArgumentNullException(
                    nameof(networkObject));

            // 0 保留为无效对象编号。
            if (nextObjectId == 0)
                throw new System.InvalidOperationException(
                    "网络对象编号已耗尽。");
            uint id=nextObjectId;
            nextObjectId = unchecked(nextObjectId + 1);
            networkObject.IsServerObject=true;
            networkObject.NetObjectId = id;
            objects.Add(id,networkObject);
            return networkObject;
        }
        public void RegisterClientObject(NetworkObject networkObject,uint objectId)
        {
            networkObject.NetObjectId = objectId;
            networkObject.IsServerObject = false;
            objects[objectId] = networkObject;
        }
        public bool TryGet(uint objectId,out NetworkObject networkObject)
        {
            return objects.TryGetValue(objectId,out networkObject);
        }
        public void Remove(uint objectId)
        {
            objects.Remove(objectId);
        }
        public IEnumerable<NetworkObject> GetAllObject()
        {
            return objects.Values;
        }
    }

} 
