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
    }
}

