using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class ReliableMessageQueue 
    {
        public class OutboundMessage
        {
            public uint MessageId;
            public byte[]Payload;
            public double LastSentTime;
            public int SendCount;
            public OutboundMessage(uint messageId,byte[] payload)
            {
                MessageId=messageId;
                Payload=payload;
                LastSentTime=double.NegativeInfinity;
                SendCount=0;
            }
        }
            private const int MaxPendingMessages = 256;
            private const int MaxReceivedMessageIds = 1024;
            private uint nextMessageId = 1;
            // MessageId -> 可靠消息。
            private readonly Dictionary<uint, OutboundMessage> pending =new Dictionary<uint, OutboundMessage>();
            // PacketSequence -> MessageId。
            private readonly Dictionary<uint, uint> packetToMessage =new Dictionary<uint, uint>();
            // 已收到过的 MessageId。
            private readonly HashSet<uint> receivedMessageIds =new HashSet<uint>();
            // 用于限制 receivedMessageIds 的大小。
            private readonly Queue<uint> receivedHistory =new Queue<uint>();
            public int PendingCount
            {
                get { return pending.Count; }
            }
            public uint Enqueue(Func<uint, byte[]> factory,double now)
            {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            if (pending.Count >= MaxPendingMessages)
                throw new InvalidOperationException(
                    "Reliable message queue is full.");
            uint messageId = AllocateMessageId();
            byte[] payload = factory(messageId);
            if (payload == null)
                throw new ArgumentException(
                    "Reliable payload cannot be null.",
                    nameof(factory));
            pending.Add(
                messageId,
                new OutboundMessage(messageId, payload));
            return messageId;
            }

            private uint AllocateMessageId()
            {
                uint start = nextMessageId;
                while (nextMessageId == 0 ||pending.ContainsKey(nextMessageId))
                {
                    nextMessageId = unchecked(nextMessageId + 1);
                    if (nextMessageId == start)
                        throw new InvalidOperationException(
                            "Reliable message id is exhausted.");
                }
                uint result = nextMessageId;
                nextMessageId = unchecked(nextMessageId + 1);
                return result;
            }
            /// <summary>
            /// 获取当前需要首次发送或重传的可靠消息。
            /// 这里只返回快照，不修改发送状态。
            /// </summary>
            public List<OutboundMessage> CollectDue(
                double now,
                double retryInterval,
                int budget)
            {
                var result = new List<OutboundMessage>();
                if (budget <= 0)
                    return result;
                foreach (OutboundMessage message in pending.Values)
                {
                    bool neverSent =
                        double.IsNegativeInfinity(message.LastSentTime);
                    bool retryDue =
                        now - message.LastSentTime >= retryInterval;
                    if (!neverSent && !retryDue)
                        continue;
                    result.Add(message);
                    if (result.Count >= budget)
                        break;
                }
                return result;
            }
            public void MarkSent(
            uint messageId,
            uint packetSequence,
            double now)
            {
                if (!pending.TryGetValue(
                        messageId,
                        out OutboundMessage message))
                    return;
                message.LastSentTime = now;
                message.SendCount++;
                // 同一 MessageId 重传时，会对应多个 PacketSequence。
                packetToMessage[packetSequence] = messageId;
            }
            public bool AcknowledgePacket(uint packetSequence)
            {
                if (!packetToMessage.TryGetValue(
                        packetSequence,
                        out uint messageId))
                    return false;
                packetToMessage.Remove(packetSequence);
                if (!pending.ContainsKey(messageId))
                    return false;
                pending.Remove(messageId);
                // 同一可靠消息可能被发送过多次。
                // 任意一次重传包被确认，都清理其他映射。
                var stalePackets = new List<uint>();
                foreach (KeyValuePair<uint, uint> pair
                    in packetToMessage)
                {
                    if (pair.Value == messageId)
                        stalePackets.Add(pair.Key);
                }
                foreach (uint stalePacket in stalePackets)
                    packetToMessage.Remove(stalePacket);
                return true;
            }
            public bool AcceptIncoming(uint messageId)
            {
                if (messageId == 0)
                    return false;
                if (!receivedMessageIds.Add(messageId))
                    return false;
                receivedHistory.Enqueue(messageId);
                while (receivedHistory.Count >
                    MaxReceivedMessageIds)
                {
                    uint oldId = receivedHistory.Dequeue();
                    receivedMessageIds.Remove(oldId);
                }
                return true;
            }
    }
}


