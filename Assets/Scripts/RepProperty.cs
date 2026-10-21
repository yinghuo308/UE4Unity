using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public abstract class RepProperty 
    {
        public ushort PropertyId{get;}
        public string Name{get;}
        protected RepProperty(ushort propertyId,string name)
        {
            PropertyId=propertyId;
            Name=name;
        }
        public abstract void Capture(NetworkObject target,ObjectStateData state);
        public abstract void Apply(NetworkObject target,ObjectStateData state);
        public abstract bool HasChanged(ObjectStateData previous,ObjectStateData current);
        public abstract PropertyUpdateData CreateUpdate(ObjectStateData state,uint version);
        public abstract bool TryReadUpdate(PropertyUpdateData update,ObjectStateData state);
    }
    public sealed class RepProperty<T> : RepProperty
    {
        private Func<NetworkObject,T> readObject;
        private Action<NetworkObject,T>writeObject;
        private Func<ObjectStateData,T>readState;
        private Action<ObjectStateData,T>writeState;
        private Func<T,T,bool>valuesEqual;
        public RepProperty(ushort propertyId,
            string name,
            Func<NetworkObject, T> readObject,
            Action<NetworkObject, T> writeObject,
            Func<ObjectStateData, T> readState,
            Action<ObjectStateData, T> writeState,
            Func<T, T, bool> valuesEqual = null):base(propertyId,name)
        {
            this.readObject = readObject;
            this.writeObject = writeObject;
            this.readState = readState;
            this.writeState = writeState;
            this.valuesEqual=valuesEqual??((a, b) => EqualityComparer<T>.Default.Equals(a, b));
        }
        public override void Apply(NetworkObject target, ObjectStateData state)
        {
            T value=readState(state);
            writeObject(target,value);
        }

        public override void Capture(NetworkObject target, ObjectStateData state)
        {
            T value=readObject(target);
            writeState(state,value);
        }

        

        public override bool HasChanged(ObjectStateData previous, ObjectStateData current)
        {
            T previousValue=readState(previous);
            T currentValue=readState(current);
            return !valuesEqual(previousValue,currentValue);
        }
        [Serializable]
        private class ValueBox
        {
            public T Value;
        }
        public override PropertyUpdateData CreateUpdate(ObjectStateData state, uint version)
        {
            ValueBox box=new ValueBox{Value=readState(state)};
            return new PropertyUpdateData
            {
                PropertyId=PropertyId,
                PropertyVersion=version,
                ValueJson=JsonUtility.ToJson(box)
            };
        }
        public override bool TryReadUpdate(PropertyUpdateData update, ObjectStateData state)
        {
            if(update==null||update.PropertyId!=PropertyId||string.IsNullOrEmpty(update.ValueJson))return false;
            try
            {
                ValueBox box=JsonUtility.FromJson<ValueBox>(update.ValueJson);
                if(box==null)return false;
                writeState(state,box.Value);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
