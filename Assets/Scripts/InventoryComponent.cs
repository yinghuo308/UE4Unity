using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class InventoryComponent : MonoBehaviour
    {
        public NetworkObject Owner{get;internal set;}
        public uint SubobjectId{get;internal set;}
        public uint StateVersion{get;internal set;}
        public int ItemCount;
        public int Capacity=20;
    }
}

