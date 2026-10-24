using System.Collections;
using System.Collections.Generic;
using UeNet;
using UnityEngine;
public enum ActorChannelState
{
    Opening,
    Open,
    Closing,
    Closed
}
public class ActorChannel 
{
    public uint ObjectId{get;}
    public NetworkObject Target{get;private set;}
    public ActorChannelState State{get;private set;}
    private ObjectReplicator replicator;
    private const int MaxPendingProperties=64;
    private const int MaxPendingRpcs=64;
    private const double PendingRpcTimeout=5.0;
    private sealed class PendingRpc
    {
        public RpcCallData Call;
        public double ReceivedTime;
    }
    private readonly Queue<PendingRpc>pendingRpcs=new Queue<PendingRpc>();
    private enum ReferenceState
    {
        Ready,
        Waiting,
        Removed
    }
    private readonly Dictionary<ushort,PropertyUpdateData> pendingProperties =
        new Dictionary<ushort, PropertyUpdateData>();
    public ReliableMessageQueue ReliableSender{get;}=new ReliableMessageQueue();
    public ReliableReceiveBuffer ReliableReceiver{get;}=new ReliableReceiveBuffer();
    public ActorChannel(uint objectId)
    {
        ObjectId = objectId;
        State = ActorChannelState.Opening;
    }
    public void Bind(NetworkObject target)
    {
        if (target == null ||
            target.NetObjectId != ObjectId)
        {
            return;
        }
        Target = target;
        replicator = new ObjectReplicator(target);
        State = ActorChannelState.Open;
        if(pendingProperties.Count==0)return;
        var update=new ObjectUpdateData
        {
            ObjectId=ObjectId
        };
        foreach(PropertyUpdateData property in pendingProperties.Values)
        {
            update.Properties.Add(property);
        }
        pendingProperties.Clear();
        replicator.ApplyUpdate(update);
    }
    public void ReceiveProperties(ObjectUpdateData update)
    {
        if (update == null || update.ObjectId != ObjectId)
        return;
        if (State == ActorChannelState.Closing ||
            State == ActorChannelState.Closed)
        {
            return;
        }
        if (State == ActorChannelState.Open && replicator != null)
        {
            replicator.ApplyUpdate(update);
            return;
        }
        foreach(PropertyUpdateData item in update.Properties)
        {
            if(item==null||!RepLayout.Instance.TryGetProperty(item.PropertyId,
                                    out RepProperty property))
            {
                continue;
            }
            if(!property.TryReadUpdate(item,new ObjectStateData()))
            {
                continue;
            }
            if(pendingProperties.TryGetValue(item.PropertyId,out PropertyUpdateData pervious))
            {
                if (!ObjectReplicator.IsNewerVersion(item.PropertyVersion, pervious.PropertyVersion))
                {
                    continue;
                }
            }
            else if(pendingProperties.Count>=MaxPendingProperties)continue;
            pendingProperties[item.PropertyId]=new PropertyUpdateData
            {
                PropertyId=item.PropertyId,
                PropertyVersion=item.PropertyVersion,
                ValueJson=item.ValueJson
            };
        }

    }
    public void Close()
    {
        State = ActorChannelState.Closed;
        Target = null;
        replicator = null;
        pendingProperties.Clear();
        pendingRpcs.Clear();
    }
    public uint QueueReliableBunch(uint connectionId,NetBunch bunch)
    {
        string data=JsonUtility.ToJson(bunch);
        return ReliableSender.Enqueue(messageId=>NetMessage.Encode(NetMessageType.ActorBunch,
                                connectionId,
                                messageId,
                                true,
                                data),0);
    }
    public void BeginClose()
    {
        State=ActorChannelState.Closing;
        Target=null;
        replicator=null;
        pendingProperties.Clear();
        pendingRpcs.Clear();
    }
    public bool ReceiveRpc(RpcCallData call,NetworkObjectRegistry registry
                            ,double now,bool allowUnmappedDelay)
    {
       if(call==null||registry==null||call.RpcId==0||
            State != ActorChannelState.Open || Target == null)
        {
            return false;
        }
        if (call.ObjectReferenceIds != null && call.ObjectReferenceIds.Count > 64)
        {
            return false;
        }
        ReferenceState referenceState=CheckReferences(call,registry);
        if (!allowUnmappedDelay)
        {
            if(referenceState!=ReferenceState.Ready)return false;
            return RpcRegistry.TryInvoke(call.RpcId,Target,call.ArgsJson);
        }
        if (pendingRpcs.Count == 0 && referenceState == ReferenceState.Ready)
        {
            return RpcRegistry.TryInvoke(call.RpcId,Target,call.ArgsJson);
        }
        if (referenceState == ReferenceState.Removed)
        {
             Debug.LogWarning(
            $"丢弃 RPC：ObjectId={ObjectId}, " +
            $"RpcId={call.RpcId}，引用对象已经销毁。");
            return false;
        }
        if (pendingRpcs.Count >= MaxPendingRpcs)
        {
            throw new System.InvalidOperationException(
                $"对象 {ObjectId} 的延迟 RPC 队列已满。");
        }
        var copy = new RpcCallData
        {
            RpcId = call.RpcId,
            Direction = call.Direction,
            ArgsJson = call.ArgsJson,
            ObjectReferenceIds =
                call.ObjectReferenceIds == null
                    ? null
                    : new List<uint>(
                        call.ObjectReferenceIds)
        };
        pendingRpcs.Enqueue(new PendingRpc
        {
            Call = copy,
            ReceivedTime = now
        });
        Debug.Log(
        $"[延迟测试] RPC 入队：" +
        $"A={ObjectId}, RpcId={call.RpcId}, " +
        $"依赖状态={referenceState}, " +
        $"队列数量={pendingRpcs.Count}",
        Target);
        return true;
    }
    private static ReferenceState CheckReferences(RpcCallData call,NetworkObjectRegistry registry)
    {
        if(call.ObjectReferenceIds==null)return ReferenceState.Ready;
        bool waiting=false;
        foreach(uint objectId in call.ObjectReferenceIds)
        {
            if(objectId==0)continue;
            if(registry.IsRemoved(objectId))return ReferenceState.Removed;
            if(!registry.TryGet(objectId,out NetworkObject referenceObject) || referenceObject == null)
            {
                waiting=true;
            }
        }
        return waiting?ReferenceState.Waiting:ReferenceState.Ready;
    }
    public int ResolvePendingRpc(NetworkObjectRegistry registry,double now)
    {
        if(registry==null)return 0;
        if(State!=ActorChannelState.Open||Target==null)
        {
            pendingRpcs.Clear();
            return 0;
        }
        int excuted=0;
        while (pendingRpcs.Count > 0)
        {
            if (State != ActorChannelState.Open || Target == null)
            {
                pendingRpcs.Clear();
                break;
            }
            PendingRpc pending=pendingRpcs.Peek();
            RpcCallData call=pending.Call;
            ReferenceState referenceState=CheckReferences(call,registry);
            bool expired=referenceState == ReferenceState.Waiting &&now-pending.ReceivedTime>=PendingRpcTimeout;
            if (referenceState == ReferenceState.Removed || expired)
            {
                pendingRpcs.Dequeue();
                string reason =
                referenceState == ReferenceState.Removed
                    ? "引用对象已经销毁"
                    : "等待超时";
                Debug.LogWarning(
                    $"丢弃延迟 RPC：ObjectId={ObjectId}, " +
                    $"RpcId={call.RpcId}，{reason}。");
                continue;
            }
            if(referenceState==ReferenceState.Waiting)break;
            pendingRpcs.Dequeue();
            if (RpcRegistry.TryInvoke(call.RpcId, Target, call.ArgsJson))
            {
                excuted++;
            }
            else
            {
                Debug.LogWarning(
                $"延迟 RPC 找不到处理器：" +
                $"ObjectId={ObjectId}, RpcId={call.RpcId}");
            }
        }
        return excuted;
    }
}
