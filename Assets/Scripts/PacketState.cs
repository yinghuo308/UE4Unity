using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class PacketState
    {
         // 本端下一次发送使用的包编号。
        private uint nextSendSequence = 1;
        // 本端是否接收过包。
        private bool hasReceivedPacket;
        // 已收到的最新包编号。
        private uint latestReceivedSequence;
        // 最新包之前 32 个包的接收情况。
        private uint receivedMask;
        // 自己发出但还未确认的包：编号 -> 发送时间。
        private readonly Dictionary<uint, double> pending =
            new Dictionary<uint, double>();
        // 按发送顺序清理历史，避免无限增长。
        private readonly Queue<uint> sendHistory =
            new Queue<uint>();
        private const int MaxTrackedPackets = 256;
        public int PendingCount => pending.Count;
        public ulong AcknowledgedCount { get; private set; }
        // 包括对方等待发出 ACK 的时间，不等于纯网络延迟。
        public double LastAckDelaySeconds { get; private set; }
        // 生成待发送的包，带上本端的接收记录。
        public NetPacket CreatePacket(byte[]payload)
        {
            uint sequence=nextSendSequence;
            nextSendSequence=unchecked(nextSendSequence+1);
            return new NetPacket(sequence,hasReceivedPacket,latestReceivedSequence,receivedMask,payload);
        }
        public void RecordSent(uint sequence,double now)
        {
            if (sendHistory.Count >= MaxTrackedPackets)
            {
                uint oldest=sendHistory.Dequeue();
                pending.Remove(oldest);
            }
            sendHistory.Enqueue(sequence);
            pending[sequence]=now;
        }
        public int ProcessAck(NetPacket packet,double now,
                        List<uint> acknowledgedSequences)
        {
            if (acknowledgedSequences == null)
            throw new ArgumentNullException(
            nameof(acknowledgedSequences));
            acknowledgedSequences.Clear();
            if(!packet.HasAck)return 0;
            Confirm(packet.Ack,now,acknowledgedSequences);
            for(int bit = 0; bit < 32; bit++)
            {
                uint mask=1u<<bit;
                if((packet.AckBits&mask)==0)continue;
                uint sequence=unchecked(packet.Ack-(uint)(bit+1));
                Confirm(sequence,now,acknowledgedSequences);
            }
            return acknowledgedSequences.Count;
        }
        private bool Confirm(uint sequence,double now,List<uint>acknowledgedSequences)
        {
            if(!pending.TryGetValue(sequence,out double sentTime))return false;
            pending.Remove(sequence);
            AcknowledgedCount++;
            LastAckDelaySeconds=now-sentTime;
            acknowledgedSequences.Add(sequence);
            return true;
        }
        public bool RecordReceived(uint sequence)
        {
            if (!hasReceivedPacket)
            {
                hasReceivedPacket=true;
                latestReceivedSequence=sequence;
                receivedMask=0;
                return true;
            }
            if(sequence==latestReceivedSequence)return false;
            if (IsNewer(sequence, latestReceivedSequence))
            {
                uint distance=unchecked(sequence-latestReceivedSequence);
                if(distance>32)receivedMask=0;
                else if(distance==32)receivedMask=1u<<31;
                else
                {
                    receivedMask=(receivedMask<<(int)distance|(1u<<((int)distance-1)));
                }
                latestReceivedSequence=sequence;
                return true; 
            }
                uint age = unchecked(latestReceivedSequence - sequence);
                if (age > 32)
                return false;

                 uint flag = 1u << ((int)age - 1);

            // 对应位已经为 1，说明收过了。
            if ((receivedMask & flag) != 0)
                return false;

            receivedMask |= flag;
            return true;
        }
        private static bool IsNewer(uint value, uint reference)
        {
            // 支持 uint 从最大值回绕到 0。
            // 前提：参与比较的序号距离小于 2^31。
            return unchecked((int)(value - reference)) > 0;
        }
        
    }
}

