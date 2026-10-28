using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class FragmentSendQueue 
    {
        public const int MaxPendingGroups=16;
        public const int MaxQueuedBytes = 1024 * 1024;
        public sealed class Group
        {
            public readonly uint GroupId;
            public readonly uint ObjectId;
            public readonly List<byte[]> Fragments;
            public int NextFragmentIndex { get; internal set; }
            public double LastRoundSentTime { get; internal set; } =double.NegativeInfinity;
            internal readonly int ReservedBytes;
            internal Group(uint groupId,uint objectId,
                List<byte[]> fragments,int reservedBytes)
            {
                GroupId = groupId;
                ObjectId = objectId;
                Fragments = fragments;
                ReservedBytes = reservedBytes;
            }
        }
        private readonly Dictionary<uint, Group> groups =new Dictionary<uint, Group>();
        private readonly Dictionary<uint, uint> objectGroups =new Dictionary<uint, uint>();
        private readonly List<Group> order = new List<Group>();
        private uint nextGroupId = 1;
        // 下一轮优先从哪个组开始，避免总是先发同一个对象。
        private int cursor;
        private int queuedBytes;
        public int PendingGroupCount
        {
            get { return groups.Count; }
        }
        public int QueuedBytes
        {
            get { return queuedBytes; }
        }
        public bool HasObject(uint objectId)
        {
            return objectGroups.ContainsKey(objectId);
        }
        public bool TryEnqueue(uint connectionId,uint objectId,
            byte[] payload,int maxPacketBytes,out Group group)
        {
            group = null;

            if (connectionId == 0 ||objectId == 0 ||
                payload == null ||payload.Length == 0 ||
                payload.Length > FragmentCodec.MaxMessageBytes ||
                HasObject(objectId) ||groups.Count >= MaxPendingGroups ||
                nextGroupId == 0)
            {
                return false;
            }
             List<byte[]> fragments;

            try
            {
                fragments = FragmentCodec.Split(connectionId,
                    nextGroupId,payload,maxPacketBytes);
            }
            catch (ArgumentException)
            {
                return false;
            }
            int charge = 0;
            foreach (byte[] fragment in fragments)
            {
                charge += NetPacketCodec.HeaderSize +
                          fragment.Length;
            }
            if (queuedBytes + charge > MaxQueuedBytes)
                return false;
            group = new Group(nextGroupId,objectId,
                fragments,charge);
            nextGroupId = unchecked(nextGroupId + 1);
            groups.Add(group.GroupId, group);
            objectGroups.Add(objectId, group.GroupId);
            order.Add(group);
            queuedBytes += charge;
            return true;
        }
        public List<Group> GetGroups()
        {
            return new List<Group>(order);
        }
        public List<Group> GetReadyGroups(double now,double retryInterval)
        {
            var ready=new List<Group>();
            for(int i = 0; i < order.Count; i++)
            {
                Group group=order[(cursor+i)%order.Count];
                if (group.NextFragmentIndex == group.Fragments.Count)
                {
                    if (now - group.LastRoundSentTime < retryInterval)
                    {
                        continue;
                    }
                    group.NextFragmentIndex=0;
                }
                ready.Add(group);
            }
            return ready;
        }
        public void RecordSent(uint groupId,double now)
        {
            if(!groups.TryGetValue(groupId,out Group group)||
                    group.NextFragmentIndex>=group.Fragments.Count)
            return;
            group.NextFragmentIndex++;
            if (group.NextFragmentIndex == group.Fragments.Count)
            {
                group.LastRoundSentTime=now;
            }
            cursor=(order.IndexOf(group)+1)%order.Count;
        }
        public bool RemoveGroup(uint groupId)
        {
            if(!groups.TryGetValue(groupId,out Group group))return false;
            groups.Remove(groupId);
            objectGroups.Remove(group.ObjectId);
            queuedBytes-=group.ReservedBytes;
            int index=order.IndexOf(group);
            order.RemoveAt(index);
            if (index < cursor)
                cursor--;
            if (cursor >= order.Count)
                cursor = 0;
            return true;
        }
        public void RemoveObject(uint objectId)
        {
            if (objectGroups.TryGetValue(objectId,out uint groupId))
            {
                RemoveGroup(groupId);
            }
        }
        public void Clear()
        {
            groups.Clear();
            objectGroups.Clear();
            order.Clear();
            queuedBytes = 0;
            cursor = 0;
        }
    }
}

