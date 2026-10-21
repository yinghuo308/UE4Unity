using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace UeNet
{
    public class ConnectionReplicationState
    {
        private class PendingProperty
        {
            public PropertyUpdateData Update;
            public double LastSentTime=double.NegativeInfinity;
        }
        private const int MaxTrackedPackets=256;
        private readonly Dictionary<uint,Dictionary<ushort,PendingProperty>> pending=
                            new Dictionary<uint, Dictionary<ushort, PendingProperty>>();
        private readonly Dictionary<uint,ObjectUpdateData>sentPackets=new Dictionary<uint, ObjectUpdateData>();
        private readonly Queue<uint>sendHistory=new Queue<uint>();
        public int PendingPropertyCount
        {
            get
            {
                int count=0;
                foreach(var properties in pending.Values)
                {
                    count+=properties.Count;
                }
                return count;
            }
        }
        public void Merge(ObjectUpdateData update)
        {
            if(update==null||update.ObjectId==0||update.Properties==null)return;
            foreach(PropertyUpdateData item in update.Properties)
            {
                if(item==null)continue;
                if(!pending.TryGetValue(update.ObjectId,out var properties))
                {
                    properties=new Dictionary<ushort, PendingProperty>();
                    pending.Add(update.ObjectId,properties);
                }
                if(properties.TryGetValue(item.PropertyId,out PendingProperty previous)
                        && !ObjectReplicator.IsNewerVersion(item.PropertyVersion, previous.Update.PropertyVersion))
                {
                    continue;
                }
                properties[item.PropertyId]=new PendingProperty
                {
                    Update=Copy(item)
                };
            }
        }

        public List<ObjectUpdateData> CollectDue(double now,double retryInterval)
        {
            var result=new List<ObjectUpdateData>();
            foreach(var pair in pending)
            {
                var update=new ObjectUpdateData{ObjectId=pair.Key};
                foreach(PendingProperty property in pair.Value.Values)
                {
                    if(now-property.LastSentTime<retryInterval)continue;
                    update.Properties.Add(Copy(property.Update));
                }
                if(update.Properties.Count>0)result.Add(update);
            }
            return result;
        }

        public void RecordSent(uint packetSequence,ObjectUpdateData update,double now)
        {
            var snapshot=new ObjectUpdateData
            {
                ObjectId=update.ObjectId
            };
            foreach(PropertyUpdateData item in update.Properties)
            {
                snapshot.Properties.Add(Copy(item));
            }
            if (sendHistory.Count >= MaxTrackedPackets)
            {
                uint oldest=sendHistory.Dequeue();
                sentPackets.Remove(oldest);
            }
            sendHistory.Enqueue(packetSequence);
            sentPackets[packetSequence]=snapshot;
            if(!pending.TryGetValue(update.ObjectId,out var properties))
            {
                return ;
            }
            foreach(var item in update.Properties)
            {
                if(properties.TryGetValue(item.PropertyId,out PendingProperty property)
                    && property.Update.PropertyVersion == item.PropertyVersion)
                {
                    property.LastSentTime=now;
                }
            }
        }
        public int AcknowledgePacket(uint packetSequence)
        {
            if (!sentPackets.TryGetValue(
                    packetSequence,
                    out ObjectUpdateData sent))
            {
                return 0;
            }
            sentPackets.Remove(packetSequence);
            if (!pending.TryGetValue(
                    sent.ObjectId,
                    out var properties))
            {
                return 0;
            }
            int confirmed = 0;
            foreach (PropertyUpdateData item
                     in sent.Properties)
            {
                if (properties.TryGetValue(
                        item.PropertyId,
                        out PendingProperty property) &&
                    property.Update.PropertyVersion ==
                        item.PropertyVersion)
                {
                    properties.Remove(item.PropertyId);
                    confirmed++;
                }
            }
            if (properties.Count == 0)
            {
                pending.Remove(sent.ObjectId);
            }
            return confirmed;
        }
        public void RemoveObject(uint objectId)
        {
            pending.Remove(objectId);
            var packetsToRemove = new List<uint>();
            foreach (var pair in sentPackets)
            {
                if (pair.Value.ObjectId == objectId)
                {
                    packetsToRemove.Add(pair.Key);
                }
            }
            foreach (uint sequence in packetsToRemove)
            {
                sentPackets.Remove(sequence);
            }
        }
        // 连接结束时清理。
        public void Clear()
        {
            pending.Clear();
            sentPackets.Clear();
            sendHistory.Clear();
        }
        private PropertyUpdateData Copy(PropertyUpdateData item)
        {
            return new PropertyUpdateData
            {
                PropertyId=item.PropertyId,
                PropertyVersion=item.PropertyVersion,
                ValueJson=item.ValueJson
            };
        }
    }
}

