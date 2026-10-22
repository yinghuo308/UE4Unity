using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class ReceivedReliableMessage
    {
        public NetMessageType Type{get;}
        public uint ConnectionId{get;}
        public uint MessageId { get; }
        public string Data { get; }
        public ReceivedReliableMessage(
            NetMessageType type,
            uint connectionId,
            uint messageId,
            string data)
        {
            Type = type;
            ConnectionId = connectionId;
            MessageId = messageId;
            Data = data ?? string.Empty;
        }
    }
    public class ReliableReceiveBuffer
    {
        private const int MaxBufferedMessages=256;
        private uint nextExceptedSequence=1;
        private readonly Dictionary<uint,ReceivedReliableMessage> bufferedMessages=new Dictionary<uint, ReceivedReliableMessage>();
        public int BufferCount=>bufferedMessages.Count;
        public uint NextExpectedSequence=>nextExceptedSequence;
        public bool CanAccept(uint messageId)
        {
            if(messageId==0)return false;
            if(messageId==nextExceptedSequence)return true;
            if(!IsNewer(messageId,nextExceptedSequence))return true;
            if(bufferedMessages.ContainsKey(messageId))return true;
            uint distance=unchecked(messageId-nextExceptedSequence);
            if(distance>(uint)MaxBufferedMessages)return false;
            return bufferedMessages.Count<MaxBufferedMessages;
        }
        public List<ReceivedReliableMessage> Receive(ReceivedReliableMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(
                    nameof(message));
            }

            if (!CanAccept(message.MessageId))
            {
                throw new InvalidOperationException(
                    "可靠消息超出接收窗口或缓存已满。");
            }
            var ready=new List<ReceivedReliableMessage>();
            uint sequence=message.MessageId;
            if (sequence != nextExceptedSequence)
            {
                if (IsNewer(sequence, nextExceptedSequence))
                {
                    if (!bufferedMessages.ContainsKey(sequence))
                    {
                        bufferedMessages.Add(sequence,message);
                    }
                }
                return ready;
            }
           ready.Add(message);
           nextExceptedSequence=NextSequence(nextExceptedSequence);
           while(bufferedMessages.TryGetValue(nextExceptedSequence,out ReceivedReliableMessage next))
            {
                bufferedMessages.Remove(nextExceptedSequence);
                ready.Add(next);
                nextExceptedSequence=NextSequence(nextExceptedSequence);
            }
            return ready;
        }
        public void Clear()
        {
            bufferedMessages.Clear();
            nextExceptedSequence=1;
        }
        private uint NextSequence(uint nextExceptedSequence)
        {
            uint next=unchecked(nextExceptedSequence+1);
            return next==0?1u:next;
        }

        private static bool IsNewer(uint value,uint reference)
        {
            return (int)(value-reference)>0;
        }
    }
}

