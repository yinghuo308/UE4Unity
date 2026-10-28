using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace UeNet
{
    public enum FragmentReceiveResult
    {
        Rejected,          // 分片不合法，或者缓存已满。
        Pending,           // 已经保存，但还没有收齐。
        Ready,             // 已经拼出完整消息。
        AlreadyCompleted   // 这个组之前已经处理完成。
    }
    public class FragmentReceiveBuffer 
    {
        public const int MaxPendingGroups=16;
        public const int MaxReservedBytes=1024*1024;
        public const int MaxCompletedGroups = 256;
        public const double PendingTimeout = 10.0;
        public const double CompletedKeepTime = 30.0;
        private sealed class Group
        {
            public uint ConnectionId;
            public int Count;
            public int TotalLength;
            public byte[][] Parts;
            public int ReceivedCount;
            public int ReceivedBytes;
            public double LastProgressTime;
            public byte[] Message;
        }
        private readonly Dictionary<uint,Group> pending=new Dictionary<uint, Group>();
        private readonly Dictionary<uint,Group> completed=new Dictionary<uint, Group>();
        private readonly Queue<uint> completedOrder=new Queue<uint>();
        private int reservedBytes;
        public int PendingGroupCount
        {
            get { return pending.Count; }
        }
        public int ReservedBytes
        {
            get { return reservedBytes; }
        }
        private static bool IsValid(FragmentData fragment,uint connectionId)
        {
            return fragment!=null&&connectionId!=0&&
                fragment.ConnectionId==connectionId&&
                fragment.GroupId!=0&&fragment.FragmentCount>0&&
                fragment.FragmentCount>0&&fragment.FragmentCount<=FragmentCodec.MaxFragments&&
                fragment.FragmentIndex<fragment.FragmentCount&&fragment.TotalLength>0
                &&fragment.TotalLength<=FragmentCodec.MaxMessageBytes&&
                fragment.FragmentCount<=fragment.TotalLength&&
                fragment.PayLoad!=null&&fragment.PayLoad.Length>0&&
                fragment.PayLoad.Length>0&&
                fragment.PayLoad.Length<=fragment.TotalLength-(fragment.FragmentCount-1)
                &&fragment.PayLoad.Length<=UdpTransport.MaxPayloadBytes-NetPacketCodec.HeaderSize-FragmentCodec.HeaderSize
                &&(fragment.FragmentCount!=1||fragment.PayLoad.Length==fragment.TotalLength);
        }
        private static bool Matches(Group group,FragmentData fragment)
        {
            return group.ConnectionId == fragment.ConnectionId &&
                   group.Count == fragment.FragmentCount &&
                   group.TotalLength == fragment.TotalLength;
        }
        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
        public FragmentReceiveResult Add(FragmentData fragment,uint connetionID,
                                double now,out byte[] message)
        {
            message=null;
            Expire(now);
            if(!IsValid(fragment,connetionID))return FragmentReceiveResult.Rejected;
            Group group;
            if(completed.TryGetValue(fragment.GroupId,out group))
            return Matches(group,fragment)?FragmentReceiveResult.AlreadyCompleted:FragmentReceiveResult.Rejected;            
            if(!pending.TryGetValue(fragment.GroupId,out group))
            {
                int charge=fragment.TotalLength*2;
                if(pending.Count>=MaxPendingGroups||
                    reservedBytes+charge>MaxReservedBytes)
                    return FragmentReceiveResult.Rejected;
                group=new Group
                {
                    ConnectionId = fragment.ConnectionId,
                    Count = fragment.FragmentCount,
                    TotalLength = fragment.TotalLength,
                    Parts = new byte[fragment.FragmentCount][],
                    LastProgressTime = now
                };
                pending.Add(fragment.GroupId,group);
                reservedBytes+=charge;
            }
            else if (!Matches(group, fragment))
            {
                return FragmentReceiveResult.Rejected;
            }
            int index=fragment.FragmentIndex;
            byte[]old=group.Parts[index];
            if (old != null)
            {
                if (!SameBytes(old, fragment.PayLoad))
                {
                    return FragmentReceiveResult.Rejected;
                }
                message=group.Message;
                return message==null?FragmentReceiveResult.Pending:FragmentReceiveResult.Ready;
            }
            int newBytes=group.ReceivedBytes+fragment.PayLoad.Length;
            int missing=group.Count-group.ReceivedCount-1;
            if (newBytes + missing > group.TotalLength)
            {
                return FragmentReceiveResult.Rejected;
            }
            group.Parts[index]=(byte[])fragment.PayLoad.Clone();
            group.ReceivedBytes=newBytes;
            group.ReceivedCount++;
            group.LastProgressTime=now;
            if(group.ReceivedCount!=group.Count)
            return FragmentReceiveResult.Pending;
            if (group.ReceivedBytes != group.TotalLength)
            {
                RemovePending(fragment.GroupId);
                return FragmentReceiveResult.Rejected;
            }
            var joined=new byte[group.TotalLength];
            int offset=0;
            foreach(byte[] part in group.Parts)
            {
                Buffer.BlockCopy(
                    part,0,joined,offset,part.Length
                );
                offset+=part.Length;
            }
            group.Message=joined;
            message=joined;
            return FragmentReceiveResult.Ready;
        }
        public bool MarkCompleted(uint groupId,double now)
        {
            Expire(now);
            Group group;
            if(!pending.TryGetValue(groupId,out group)||group.Message==null)
            return false;
            RemovePending(groupId);
            group.Parts=null;
            group.Message=null;
            group.LastProgressTime=now;
            completed.Add(groupId,group);
            completedOrder.Enqueue(groupId);
            TrimCompleted(now);
            return true;
        }
        private void RemovePending(uint groupId)
        {
            Group group;
            if(pending.TryGetValue(groupId,out group))
            {
                reservedBytes-=group.TotalLength*2;
                pending.Remove(groupId);
            }
        }
        private void TrimCompleted(double now)
        {
            while (completedOrder.Count > 0)
            {
                uint groupId=completedOrder.Peek();
                Group group;
                if(completed.TryGetValue(groupId,out group)&&
                    completed.Count<=MaxCompletedGroups&&
                        now - group.LastProgressTime < CompletedKeepTime)
                {
                    break;
                }
                completedOrder.Dequeue();
                completed.Remove(groupId);
            }
        }
        public void Expire(double now)
        {
            var expired=new List<uint>();
            foreach(var pair in pending)
            {
                if (now - pair.Value.LastProgressTime > PendingTimeout)
                {
                    expired.Add(pair.Key);
                }
            }
            foreach(uint groupId in expired)
            {
                RemovePending(groupId);
            }
            TrimCompleted(now);
        }
        public void Clear()
        {
            pending.Clear();
            completed.Clear();
            completedOrder.Clear();
            reservedBytes=0;
        }
    }
    
}

