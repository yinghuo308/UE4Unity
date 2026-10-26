using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class ReplicatedItemView : MonoBehaviour
    {
        public uint SubobjectId{get;private set;}

        public ReplicatedItemData data;
        public void Initialize(ReplicatedItemData source)
        {
            if(source==null)return;
            data=source;
            SubobjectId=source.SubobjectId;
            Apply(source);
        }
        public void Apply(ReplicatedItemData source)
        {
            if(source==null)return;
            data=source;
            SubobjectId=source.SubobjectId;
            float scale=Mathf.Clamp(0.5f+source.Count*0.1f,0.5f,3f);
            transform.localScale=Vector3.one*scale;
        }
    }
}

