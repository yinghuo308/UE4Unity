using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class RepLayout 
    {
        public static RepLayout Instance{get;}=new RepLayout();
        private const float PositionThreshold=0.001f;
        private readonly RepProperty[] properties;
        public IEnumerable<RepProperty> Properties=>properties;
        private RepLayout()
        {
            properties=new RepProperty[]
            {
                new RepProperty<int>(
                    propertyId:1,name:"Health",readObject:obj=>obj.Health,writeObject:(obj,value)=>obj.Health=value,
                    readState:state=>state.Health,writeState:(state,value)=>state.Health=value
                ),
                 new RepProperty<Vector3>(
                    propertyId: 2,
                    name: "Position",
                    readObject: obj => obj.transform.position,
                    writeObject: (obj, value) =>
                        obj.transform.position = value,
                    readState: state => state.Position,
                    writeState: (state, value) =>
                        state.Position = value,
                    // 位置差距不超过阈值时，认为相同。
                    valuesEqual: (a, b) =>
                        (a - b).sqrMagnitude <=
                        PositionThreshold * PositionThreshold
                )
            };
        }
        public ObjectStateData Capture(NetworkObject target)
        {
            ObjectStateData state=new ObjectStateData
            {
                ObjectId=target.NetObjectId,
                StateVersion=target.StateVersion
            };
            foreach(RepProperty property in properties)
            {
                property.Capture(target,state);
            }
            return state;
        }
        public bool HasChange(ObjectStateData previous,ObjectStateData current)
        {
            if(previous==null)return true;
             foreach (RepProperty property in properties)
            {
                if (property.HasChanged(previous, current))
                    return true;
            }
            return false;
        }
         public void Apply(
            NetworkObject target,
            ObjectStateData state)
        {
            foreach (RepProperty property in properties)
            {
                property.Apply(target, state);
            }
        }
        public bool TryGetProperty(ushort propertyId,out RepProperty result)
        {
            foreach(RepProperty property in properties)
            {
                if (property.PropertyId == propertyId)
                {
                    result=property;
                    return true;
                }
            }
            result=null;
            return false;
        }
    }
}

