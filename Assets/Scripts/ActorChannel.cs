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
    private readonly Queue<ObjectUpdateData> pendingUpdates =
        new Queue<ObjectUpdateData>();

    public ActorChannel(uint objectId)
    {
        ObjectId = objectId;
        State = ActorChannelState.Opening;
    }
    public void Bind(NetworkObject target)
    {
        Target = target;
        replicator = new ObjectReplicator(target);
        State = ActorChannelState.Open;

        while (pendingUpdates.Count > 0)
            replicator.ApplyUpdate(pendingUpdates.Dequeue());
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

        if (State != ActorChannelState.Open || replicator == null)
        {
            pendingUpdates.Enqueue(update);
            return;
        }

        replicator.ApplyUpdate(update);
    }

    public void Close()
    {
        State = ActorChannelState.Closed;
        Target = null;
        replicator = null;
        pendingUpdates.Clear();
    }
}
