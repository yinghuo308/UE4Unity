using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    [Serializable]
    public class PropertyUpdateData
    {
        public ushort PropertyId;
        public uint PropertyVersion;
        public string ValueJson;
    }
    [Serializable]
    public class ObjectUpdateData
    {
        public uint ObjectId;
        public List<PropertyUpdateData> Properties=new List<PropertyUpdateData>();
    }
}

