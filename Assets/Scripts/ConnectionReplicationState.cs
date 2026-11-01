using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
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
        private readonly Dictionary<uint,List<ObjectUpdateData>>sentPackets=new Dictionary<uint, List<ObjectUpdateData>>();
        private readonly Queue<uint>sendHistory=new Queue<uint>();
        private const int MaxTrackedFragmentGroups=16;
        private readonly Dictionary<uint,ObjectUpdateData>sentFragmentGroups=new Dictionary<uint, ObjectUpdateData>();
        private readonly Dictionary<uint,uint>ownerVersions=new Dictionary<uint, uint>();
        public void ForgetFragmentGroup(uint groupId)
        {
            sentFragmentGroups.Remove(groupId);
        }
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
            if(ownerVersions.TryGetValue(update.ObjectId,out uint version)&&
                    update.OwnerVersion != version)
            {
                if(update.OwnerVersion<version)
                    return;
                RemoveObject(update.ObjectId);
            }
            ownerVersions[update.ObjectId]=update.OwnerVersion;
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
                var update=new ObjectUpdateData
                {
                    ObjectId=pair.Key,
                    OwnerVersion=ownerVersions[pair.Key]
                };
                foreach(PendingProperty property in pair.Value.Values)
                {
                    if(now-property.LastSentTime<retryInterval)continue;
                    update.Properties.Add(Copy(property.Update));
                }
                if(update.Properties.Count>0)result.Add(update);
            }
            return result;
        }

        public void RecordSent(uint packetSequence,List<ObjectUpdateData> updates,double now)
        {
            if(updates==null||updates.Count==0)return;
            var snapshots=new List<ObjectUpdateData>();
            foreach(ObjectUpdateData update in updates)
            {
                if(update==null||update.Properties==null)
                continue;
                var snapshot = new ObjectUpdateData
                {
                    ObjectId = update.ObjectId,
                    OwnerVersion=update.OwnerVersion
                };

                foreach (PropertyUpdateData item in update.Properties)
                {
                    if (item != null)
                        snapshot.Properties.Add(Copy(item));
                }
                snapshots.Add(snapshot);
                if (!ownerVersions.TryGetValue(
                        update.ObjectId, out uint currentOwnerVersion) ||
                    update.OwnerVersion != currentOwnerVersion ||
                    !pending.TryGetValue(update.ObjectId, out var properties))
                {
                    continue;
                }
                foreach(var item in snapshot.Properties)
                {
                    if(properties.TryGetValue(item.PropertyId,out PendingProperty property)
                        && property.Update.PropertyVersion == item.PropertyVersion)
                    {
                        property.LastSentTime=now;
                    }
                }
            }
            if (sendHistory.Count >= MaxTrackedPackets)
            {
                uint oldest=sendHistory.Dequeue();
                sentPackets.Remove(oldest);
            }
            sendHistory.Enqueue(packetSequence);
            sentPackets[packetSequence]=snapshots;
        }
        public int AcknowledgePacket(uint packetSequence)
        {
            if (!sentPackets.TryGetValue(
                    packetSequence,
                    out List<ObjectUpdateData> snapshots))
            {
                return 0;
            }
            sentPackets.Remove(packetSequence);
            int confirmed=0;
            foreach(ObjectUpdateData sent in snapshots)
            {
                confirmed+=AcknowledgeSnapshot(sent);
            }
            return confirmed;
        }
        public void RemoveObject(uint objectId)
        {
            pending.Remove(objectId);
            ownerVersions.Remove(objectId);
            var packetsToRemove = new List<uint>();
            foreach (var pair in sentPackets)
            {
                pair.Value.RemoveAll(update =>
                    update.ObjectId==objectId
                );
                if(pair.Value.Count==0)
                packetsToRemove.Add(pair.Key);
            }
            foreach (uint sequence in packetsToRemove)
            {
                sentPackets.Remove(sequence);
            }
            var fragmentGroupsToRemove = new List<uint>();
            foreach (var pair in sentFragmentGroups)
            {
                if (pair.Value.ObjectId == objectId)
                    fragmentGroupsToRemove.Add(pair.Key);
            }
            foreach (uint groupId in fragmentGroupsToRemove)
            {
                sentFragmentGroups.Remove(groupId);
            }
        }
        // 连接结束时清理。
        public void Clear()
        {
            pending.Clear();
            sentPackets.Clear();
            sendHistory.Clear();
            sentFragmentGroups.Clear();
            ownerVersions.Clear();
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
        public bool HasPendingProperties(uint objectId)
        {
            return pending.TryGetValue(objectId,out var properties)
                    &&properties.Count>0;
        }
        public bool RecordFragmentGroup(uint groupId,ObjectUpdateData update,double now)
        {
             if (groupId == 0 ||
                update == null ||
                update.ObjectId == 0 ||
                update.Properties == null ||
                sentFragmentGroups.ContainsKey(groupId) ||
                sentFragmentGroups.Count >= MaxTrackedFragmentGroups)
            {
                return false;
            }
            var snapshot=new ObjectUpdateData
            {
                ObjectId=update.ObjectId
            };
            foreach(PropertyUpdateData item in update.Properties)
            {
                if(item!=null)
                snapshot.Properties.Add(Copy(item));
            }
            if(snapshot.Properties.Count==0)return false;
            sentFragmentGroups.Add(groupId,snapshot);
            if (ownerVersions.TryGetValue(
                update.ObjectId, out uint currentOwnerVersion) &&
            update.OwnerVersion == currentOwnerVersion &&
            pending.TryGetValue(update.ObjectId, out var properties))
            {
                foreach (PropertyUpdateData item in snapshot.Properties)
                {
                    if (properties.TryGetValue(
                            item.PropertyId,
                            out PendingProperty property) &&
                        property.Update.PropertyVersion ==
                            item.PropertyVersion)
                    {
                        property.LastSentTime = now;
                    }
                }
            }
            return true;
        }
        public int AcknowledgeFragmentGroup(uint groupId)
        {
            if(!sentFragmentGroups.TryGetValue(groupId,out ObjectUpdateData snapshot))
            {
                return 0;
            }
            sentFragmentGroups.Remove(groupId);
            return AcknowledgeSnapshot(snapshot);
        }
        private int AcknowledgeSnapshot(ObjectUpdateData sent)
        {
            if (!ownerVersions.TryGetValue(
                    sent.ObjectId, out uint version) ||
                sent.OwnerVersion != version)
            {
                return 0;
            }
            if(!pending.TryGetValue(sent.ObjectId,out var properties))
            {
                return 0;
            }
            int confirmed=0;
            foreach(PropertyUpdateData item in sent.Properties)
            {
                if(properties.TryGetValue(item.PropertyId,out PendingProperty property)
                    && property.Update.PropertyVersion == item.PropertyVersion)
                {
                    properties.Remove(item.PropertyId);
                    confirmed++;
                }
            }
            if(properties.Count==0)pending.Remove(sent.ObjectId);
            return confirmed;
        }

    }
}

