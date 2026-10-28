using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
namespace UeNet
{
   public class ReplicationScheduler 
    {
        public sealed class Candidate
        {
            public ObjectUpdateData Update;
            public byte[] Payload;
            public double Priority;
            public int PacketBytes
            {
                get{return NetPacketCodec.HeaderSize+PacketPayloadCodec.HeaderSize+
                            PacketPayloadCodec.LengthSize+Payload.Length;}
            }
        }
         private sealed class ActorTiming
            {
                public double FirstConsideredTime;
                public double LastSentTime=double.NegativeInfinity;
                public bool WarnedOversized;
            }
            private readonly Dictionary<uint,ActorTiming>timings=new Dictionary<uint, ActorTiming>();
            private ActorTiming GetTiming(uint objectId,double now)
            {
                ActorTiming timing;
                if(!timings.TryGetValue(objectId,out timing))
                {
                    timing=new ActorTiming
                    {
                        FirstConsideredTime=now
                    };
                    timings.Add(objectId,timing);
                }   
                return timing;             
            }
            public double GetPriority(uint objectId,bool isOwner,float distance,double now)
            {
                ActorTiming timing=GetTiming(objectId,now);       
                double referenceTime=double.IsNegativeInfinity(timing.LastSentTime)?
                    timing.FirstConsideredTime:timing.LastSentTime;   
                double age=Math.Max(0.0,now-referenceTime);   
                double ownerBonus=isOwner?2.0:0.0;
                double distanceBonus=1.0/(1.0+Math.Max(0.0,distance));
                return ownerBonus+distanceBonus+age;
            }
             public void RecordSent(uint objectId, double now)
            {
                ActorTiming timing = GetTiming(objectId, now);

                timing.LastSentTime = now;
                timing.WarnedOversized = false;
            }
             public bool ShouldWarnOversized(uint objectId)
            {
                ActorTiming timing;
                if (!timings.TryGetValue(objectId, out timing) ||
                    timing.WarnedOversized)
                {
                    return false;
                }
                timing.WarnedOversized = true;
                return true;
            }
            public void RemoveObject(uint objectId)
            {
                timings.Remove(objectId);
            }
            public void Clear()
            {
                timings.Clear();
            }
    } 
}

