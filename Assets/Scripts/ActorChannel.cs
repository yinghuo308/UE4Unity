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
    }
    public bool ReceiveRpc(RpcCallData call)
    {
        if(call==null||State!=ActorChannelState.Open||Target==null)return false;
        return RpcRegistry.TryInvoke(call.RpcId,Target,call.ArgsJson);
    }
}
