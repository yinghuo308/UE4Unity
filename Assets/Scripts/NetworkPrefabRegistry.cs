using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UeNet
{
    public class NetworkPrefabRegistry : MonoBehaviour
    {
        [SerializeField] private List<NetworkObject> prefabs=new List<NetworkObject>();
        private Dictionary<string,NetworkObject>byType;
        public void Initialize()
        {
            byType=null;
            var lookup=new Dictionary<string,NetworkObject>(StringComparer.Ordinal);
            foreach(NetworkObject prefab in prefabs)
            {
                if(prefab==null)
                throw new InvalidOperationException("网络 Prefab 列表存在空项。");
                string type=prefab.ObjectType;
                if(string.IsNullOrWhiteSpace(type)||type.Length>64||
                    type != type.Trim())
                {
                    throw new InvalidOperationException($"Prefab {prefab.name} 的 ObjectType 无效。");
                }
                if (prefab.transform.parent != null ||
                    prefab.gameObject.scene.IsValid())
                {
                    throw new InvalidOperationException($"请登记 Project 中的 Prefab 根节点：{type}");
                }
                if (!prefab.gameObject.activeSelf ||
                    prefab.GetComponentsInChildren<NetworkObject>(
                        true).Length != 1)
                {
                    throw new InvalidOperationException(
                        $"Prefab {type} 必须启用，" +
                        "且整个层级只能有一个根 NetworkObject。");
                }
                 if (prefab.GetComponents<InventoryComponent>()
                        .Length > 1)
                {
                    throw new InvalidOperationException(
                        $"Prefab {type} 的根节点只能有一个背包组件。");
                }
                if (lookup.ContainsKey(type))
                {
                    throw new InvalidOperationException(
                        $"ObjectType 重复：{type}");
                }
                lookup.Add(type,prefab);
            }
            if (lookup.Count == 0)
            {
                throw new InvalidOperationException(
                    "请至少登记一个网络 Prefab。");
            }
            byType = lookup;
        }
        public bool TryGetPrefab(string objectType,out NetworkObject prefab)
        {
            prefab=null;
            return byType != null &&
                   !string.IsNullOrEmpty(objectType) &&
                   byType.TryGetValue(objectType, out prefab) &&
                   prefab != null;
        }
        public bool TryInstantiate(string objectType,out NetworkObject instance)
        {
            instance=null;
            if(!TryGetPrefab(objectType,out NetworkObject prefab))
            {
                return false;
            }
            instance=Instantiate(prefab);
            return instance!=null;
        }
    }
}

