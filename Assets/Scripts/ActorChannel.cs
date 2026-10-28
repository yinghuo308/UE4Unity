using System;
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
    private const float PositionWakeThreshold=0.001f;
    private Vector3 lastObservedPosition;
    private readonly Dictionary<ushort,PropertyUpdateData> pendingProperties =
        new Dictionary<ushort, PropertyUpdateData>();
    public ReliableMessageQueue ReliableSender{get;}=new ReliableMessageQueue();
    public ReliableReceiveBuffer ReliableReceiver{get;}=new ReliableReceiveBuffer();
    private readonly Dictionary<uint,InventoryReplicator>inventoryReplicators=new Dictionary<uint, InventoryReplicator>();
    private readonly Dictionary<uint,ReplicatedItemView>itemViews=new Dictionary<uint, ReplicatedItemView>();
    private readonly Dictionary<uint,ItemReplicator> itemReplicators=new Dictionary<uint, ItemReplicator>();
    private bool dormant;
    private bool forceNetUpdate;
    public bool Isdormant=>dormant;
    public bool HasForcedUpdate=>forceNetUpdate;
    private double lastActivityTime;
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
        lastObservedPosition=target.transform.position;
        replicator = new ObjectReplicator(target);
        if (target.IsServerObject)
        {
            replicator.InitializeServerBaseline(Time.realtimeSinceStartupAsDouble);
        }
        State = ActorChannelState.Open;
        inventoryReplicators.Clear();
        if (target.IsServerObject)
        {
             foreach (ReplicatedItemData item
                    in target.ReplicatedItems.Values)
            {
                itemReplicators[item.SubobjectId] =
                    new ItemReplicator(item);
            }
        }
        if (target.Inventory != null)
        {
            inventoryReplicators.Add(target.Inventory.SubobjectId,new InventoryReplicator(target.Inventory));
        }
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
        ClearItemViews();
        State = ActorChannelState.Closed;
        Target = null;
        replicator = null;
        pendingProperties.Clear();
        pendingRpcs.Clear();
        inventoryReplicators.Clear();
        itemReplicators.Clear();
    }
    public uint QueueReliableBunch(uint connectionId,NetBunch bunch)
    {
        string data=JsonUtility.ToJson(bunch);
        MarkActivity(Time.realtimeSinceStartupAsDouble);
        return ReliableSender.Enqueue(messageId=>NetMessage.Encode(NetMessageType.ActorBunch,
                                connectionId,
                                messageId,
                                true,
                                data),0);
    }
    public void BeginClose()
    {
        ClearItemViews();
        State=ActorChannelState.Closing;
        Target=null;
        replicator=null;
        pendingProperties.Clear();
        pendingRpcs.Clear();
        inventoryReplicators.Clear();
        itemReplicators.Clear();
    }
    private void ClearItemViews()
    {
        foreach(ReplicatedItemView view in itemViews.Values)
        {
            if (view != null)
            {
                UnityEngine.GameObject.Destroy(view.gameObject);
            }
        }
        itemViews.Clear();
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
    public void QueueInventoryUpdates(uint connectionId)
    {
        if(State!=ActorChannelState.Open||Target==null||!Target.IsServerObject)return;
        if (Isdormant &&
            !HasForcedUpdate)
        {
            return;
        }
        foreach(InventoryReplicator inventoryReplicator in inventoryReplicators.Values)
        {
            if(!inventoryReplicator.TryBuildUpdate(out InventoryStateData update))
            {
                continue;
            }
            var bunch=new NetBunch
            {
                ObjectId=ObjectId,
                Type=NetBunchType.SubobjectProperties,
                PayloadJson=JsonUtility.ToJson(update)
            };
            QueueReliableBunch(connectionId,bunch);
            inventoryReplicator.CommitQueued(update);
        }
    }
    public bool ReceiveInventoryProperties(InventoryStateData update)
    {
        if(State!=ActorChannelState.Open||Target==null||Target.IsServerObject||
                update==null||update.PlayerObjectId!=ObjectId)
        return false;
        if(!inventoryReplicators.TryGetValue(update.SubobjectId,out InventoryReplicator inventoryReplicator))
        {
            return false;
        }
        return inventoryReplicator.ApplyUpdate(update);        
    }
    public bool ReceiveSubobjectCreate(string json)
    {
        if (State != ActorChannelState.Open ||
        Target == null ||
        Target.IsServerObject)
        {
            return false;
        }
        ReplicatedItemData item;
        try
        {
            item =
                JsonUtility.FromJson<ReplicatedItemData>(
                    json);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (item == null ||
            item.SubobjectId == 0 ||
            item.ItemTypeId <= 0 ||
            item.Count <= 0)
        {
            return false;
        }
        // 重复创建消息不重复添加。
        if (Target.ReplicatedItems.ContainsKey(
                item.SubobjectId))
        {
            return true;
        }
          Target.ReplicatedItems.Add(
        item.SubobjectId,
        item);
        CreateItemView(item);
        Debug.Log(
            $"[子对象创建] ObjectId={ObjectId}, " +
            $"SubobjectId={item.SubobjectId}, " +
            $"ItemTypeId={item.ItemTypeId}, " +
            $"Count={item.Count}");
        return true;
    }
    public bool ReceiveSubobjectDestroy(string json)
    {
        if (State != ActorChannelState.Open ||
            Target == null ||
            Target.IsServerObject)
        {
            return false;
        }
        SubobjectDestroyData data;
        try
        {
            data =
                JsonUtility.FromJson<SubobjectDestroyData>(
                    json);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (data == null || data.SubobjectId == 0)
            return false;
        bool removed = Target.ReplicatedItems.Remove(
            data.SubobjectId);
            if (itemViews.TryGetValue(data.SubobjectId,out ReplicatedItemView view))
        {
            if (view != null)
            {
                UnityEngine.Object.Destroy(
                    view.gameObject);
            }
            itemViews.Remove(data.SubobjectId);
        }
        if (removed)
        {
            Debug.Log(
                $"[子对象销毁] ObjectId={ObjectId}, " +
                $"SubobjectId={data.SubobjectId}");
        }
        // 已经不存在，也符合删除后的目标状态。
        return true;
    }
    public bool ReceiveSubobjectItemProperties(string json)
    {
        if(State!=ActorChannelState.Open||Target==null||Target.IsServerObject)return false;
        ReplicatedItemData update;
        try
        {
            update=JsonUtility.FromJson<ReplicatedItemData>(json);
        }
        catch
        {
            return false;
        }
        if(update==null||update.SubobjectId==0||
                !Target.ReplicatedItems.TryGetValue(update.SubobjectId,out ReplicatedItemData current))
        {
            return false;
        }
        if (!ObjectReplicator.IsNewerVersion(update.StateVersion, current.StateVersion))
        {
            return false;
        }
        current.ItemTypeId=update.ItemTypeId;
        current.Count=update.Count;
        current.StateVersion=update.StateVersion;
        if (itemViews.TryGetValue(update.SubobjectId,out ReplicatedItemView view) &&
            view != null)
        {
            view.Apply(current);
        }
        Debug.Log(
        $"[动态物品同步] ObjectId={ObjectId}, " +
        $"SubobjectId={update.SubobjectId}, " +
        $"Count={update.Count}");
        return true;
    }
    public ReplicatedItemView CreateItemView(ReplicatedItemData item)
    {
        if(item==null||Target==null)return null;
        GameObject prefab=Resources.Load<GameObject>($"NetworkItems/{item.ItemTypeId}");
        if (prefab == null)
        {
             Debug.LogWarning(
            $"找不到物品预制体：" +
            $"ItemTypeId={item.ItemTypeId}");
            return null;
        }
        GameObject instance=UnityEngine.Object.Instantiate(prefab,Target.transform);
         instance.name =$"Item_{item.SubobjectId}";
        ReplicatedItemView view =instance.GetComponent<ReplicatedItemView>();       
        if (view == null)
        {
            view =instance.AddComponent<ReplicatedItemView>();
        }
        view.Initialize(item);
        itemViews[item.SubobjectId] = view;
        return view;
    }
    public void RegisterItemForReplication(
            ReplicatedItemData item)
    {
        if (State != ActorChannelState.Open ||
                Target == null ||
                !Target.IsServerObject ||
                item == null ||
                item.SubobjectId == 0)
        {
            return;
        }

        itemReplicators[item.SubobjectId] =new ItemReplicator(item);
    }
    public void UnregisterItemForReplication(
        uint subobjectId)
    {
        itemReplicators.Remove(subobjectId);
    }
    public void QueueItemUpdates(uint connectionId)
    {
        if (State != ActorChannelState.Open || Target == null || !Target.IsServerObject)
        {
            return;
        }
        if (Isdormant &&
            !HasForcedUpdate)
        {
            return;
        }
        foreach(ItemReplicator itemReplicator in itemReplicators.Values)
        {
            if(!itemReplicator.TryBuildUpdate(out ReplicatedItemData update))
            {
                continue;
            }
            var bunch=new NetBunch
            {
                ObjectId=ObjectId,
                Type=NetBunchType.SubobjectItemProperties,
                PayloadJson=JsonUtility.ToJson(update)
            };
            QueueReliableBunch(connectionId,bunch);
            itemReplicator.CommitQueued(update);
        }
    }
    public void SetDormant()
    {
        dormant=true;
        forceNetUpdate=false;
    }
    public void ForceNetUpdate()
    {
        MarkActivity(Time.realtimeSinceStartupAsDouble);
        forceNetUpdate=true;
    }
    public void ClearForcedUpdate()
    {
        forceNetUpdate=false;
    }
    public void MarkActivity(double now)
    {
        lastActivityTime=now;
        dormant=false;
    }
    public bool ShouldEnterDormancy(double now,double idleSeconds,bool hasPendingProperties)
    {
        return State==ActorChannelState.Open&&!dormant&&
            !HasForcedUpdate&&ReliableSender.PendingCount==0
                &&now-lastActivityTime>=idleSeconds&&!hasPendingProperties;
    }
    public void Wake(double now)
    {
        dormant = false;
        forceNetUpdate = false;
        lastActivityTime = now;
    }
    public bool TryBuildActorUpdate(double now,out ObjectUpdateData update)
    {
        update=null;
        if(State!=ActorChannelState.Open||Target==null||!Target.IsServerObject
                || replicator == null)
        {
            return false;
        }        
        return replicator.TryCaptureUpdate(now,out update,allowPeriodicRefresh:false);
    }
    public void CheckPositionAndWake(double now)
    {
        if(State!=ActorChannelState.Open||Target==null||!Target.IsServerObject)return;
        Vector3 currentPosition = Target.transform.position;
        float distanceSquared =(currentPosition - lastObservedPosition).sqrMagnitude;
         if (distanceSquared <=
        PositionWakeThreshold * PositionWakeThreshold)
        {
            return;
        }
        lastObservedPosition = currentPosition;
        // 唤醒通道，并重新计算空闲时间。
        MarkActivity(now);
    }

}
