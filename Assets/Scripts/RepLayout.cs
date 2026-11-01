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
                    propertyId:1,name:"Health",
                    readObject:obj=>obj.Health,
                    writeObject:(obj,value)=>obj.ApplyReplicatedHealth(value),
                    readState:state=>state.Health,
                    writeState:(state,value)=>state.Health=value
                ),
                new RepProperty<uint>(
                    propertyId:3,
                    name:"Target",
                    readObject:obj=>obj.TargetObjectId,
                    writeObject:(obj,value)=>obj.ApplyTargetId(value),
                    readState:state=>state.TargetObjectId,
                    writeState:(state,value)=>state.TargetObjectId=value
                ),
                new RepProperty<int>(
                    propertyId:4,
                    name:"PrivateScore",
                    readObject:obj=>obj.PrivateScore,
                    writeObject: (obj, value) =>
                    {
                        if(obj.PrivateScore==value)return;
                        obj.PrivateScore=value;
                    },
                    readState:state=>state.PrivateScore,
                    writeState:(state,value)=>state.PrivateScore=value,
                    condition:RepProperty.ReplicationCondition.OwnerOnly
                ),
                new RepProperty<MovementStateData>(
                    propertyId:7,
                    name:"Movement",
                    readObject:obj=>obj.GetServerMovement(),
                    writeObject:(obj,value)=>obj.ApplyReplicatedMovement(value),
                    readState: state =>MovementStateData.FromState(state),
                    writeState: (state, value) =>value.WriteTo(state),
                    valuesEqual: (a, b) =>MovementStateData.SameMovement(a, b)
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

