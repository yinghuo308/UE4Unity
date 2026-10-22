using System;
using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
namespace UeNet
{
    public class NetworkDriver : MonoBehaviour
    {
        [SerializeField] private bool isServer=false;
        [SerializeField] private int serverPort=7777;
        [SerializeField] private string serverAddress="127.0.0.1:7777";
        private const double RetryInterval=1.0;
        private const double HeartbeatInterval=2.0;
        private const double TimeoutSeconds=10.0;
        private const int ReceiveBudget=128;
        private ITransport transport;
        private Dictionary<string,Connection>connections=new Dictionary<string, Connection>();
        private List<string> expiredAddresses=new List<string>();
        private Connection serverConnection;
        private uint nextConnectionId=1;
        private double nextConnectionRequestTime;
        private double Now=>Time.realtimeSinceStartupAsDouble;
        private List<uint> acknowledgedSequences=new List<uint>(32);
        private NetworkObjectRegistry networkObjectRegistry=new NetworkObjectRegistry();
        private HashSet<uint>despawnedObjectIds=new HashSet<uint>();
        private const double StateSyncInterval=0.1;
       private readonly Dictionary<uint,Dictionary<ushort,PropertyUpdateData>> pendingObjectStates=
                        new Dictionary<uint, Dictionary<ushort, PropertyUpdateData>>();
        private double nextStateSyncTime;
        [SerializeField]private uint testDespawnObjectId=1;
        private readonly Dictionary<uint,ObjectReplicator> objectReplicators=new Dictionary<uint, ObjectReplicator>();
        [ContextMenu("测试服务器销毁")]
        public void DespawnTestObject()
        {
            DespawnServerObject(testDespawnObjectId);
        }
        void Awake()
        {
            Application.runInBackground=true;
        }
        // Start is called before the first frame update
        void Start()
        {
            try
            {
                transport=new UdpTransport(isServer?serverPort:0);
                if (!isServer)
                {
                    serverConnection=new Connection(serverAddress,Now);
                    nextConnectionRequestTime=Now;
                }
            }
            catch(Exception e)
            {
                Debug.LogException(e,this);
                CloseTransport();
                enabled=false;
            }
        }

        // Update is called once per frame
        void Update()
        {
            if(transport==null)return;
            try
            {
                ReceivePackets();
                if(isServer)UpdateServer();
                else UpdateClient();
            }
            catch(Exception e)
            {
                Debug.LogException(e,this);
                CloseTransport();
                enabled=false;
            }
        }
        private void ReceivePackets()
        {
            for(int i = 0; i < ReceiveBudget; i++)
            {
                if(!transport.TryReceive(out Datagram datagram))
                {
                    break;
                }
                 if (!NetPacketCodec.TryDecode(datagram.Bytes,out NetPacket packet))
            continue;
                if(!NetMessage.TryDecode(packet.Payload,out NetMessageType type,
                        out uint connectionId,out uint messageId,
                        out bool reliable,out string data))
                {
                    continue;
                }
                Connection connection = FindPacketConnection(
                    datagram.Remote,
                    type,
                    connectionId);
                if (connection == null)
                    continue;
                double now = Now;
                // 4. 对方告诉我们：自己发出的哪些包已被收到。
                connection.Packets.ProcessAck(
                    packet, now,acknowledgedSequences);
                
                for (int ackIndex = 0;
                    ackIndex < acknowledgedSequences.Count;
                    ackIndex++)
                {
                    uint acknowledgedSequence =
                        acknowledgedSequences[ackIndex];
                    connection.ReliableMessageQueue
                        .AcknowledgePacket(
                            acknowledgedSequence);
                    int confirmedProperties=connection.Replication.AcknowledgePacket(acknowledgedSequence);
                    if (confirmedProperties > 0)
                    {
                          Debug.Log(
                            $"连接 {connection.connectionID}：" +
                            $"包 {acknowledgedSequence} 确认了 " +
                            $"{confirmedProperties} 个属性",
                            this);
                    }
                }
                if(reliable&&!connection.ReliableReceiver.CanAccept(messageId))continue;

                // 5. 记录收到的编号，过滤重复包和过旧的包。
                if (!connection.Packets.RecordReceived(packet.Sequence))
                    continue;
                // 6. 收到一个新的、有效的连接消息。
                connection.lastReceiveTime = now;
                // 8. 不可靠消息直接交给业务处理。
                if (!reliable)
                {
                    DispatchMessage(
                        datagram.Remote,
                        type,
                        connectionId,
                        data);
                    continue;
                }
                // 9. 可靠消息进入有序缓冲。
                var received = new ReceivedReliableMessage(
                    type,
                    connectionId,
                    messageId,
                    data);
                List<ReceivedReliableMessage> ready =
                    connection.ReliableReceiver.Receive(
                        received);
                // 10. 只执行当前已经连续到达的可靠消息。
                foreach (ReceivedReliableMessage message in ready)
                {
                    DispatchMessage(
                        connection.remoteAddress,
                        message.Type,
                        message.ConnectionId,
                        message.Data);
                    if (connection.connectionState ==
                        ConnectionState.DisConnect)
                    {
                        break;
                    }
                }
            }
        }
        private void HandleServerMessage(string remote,
                 NetMessageType type, uint connectionId,string data)
        {
            if (type == NetMessageType.ConnectRequest)
            {
                if(connectionId!=0)return;
                if (!connections.TryGetValue(remote, out Connection connection))
                return;
                // 此时请求包的 Sequence 已记录，
                // 所以接受消息可以直接携带对这个请求的 ACK。
                Send(connection, NetMessageType.ConnectAccept);
                QueueInitialObjects(connection);
                return;
            }
             if (!connections.TryGetValue(
                    remote, out Connection existing))
                return;

            if (existing.connectionID != connectionId)
                return;
            if(type==NetMessageType.Heartbeat)
            {
                existing.lastReceiveTime=Now;
        //         Debug.Log(
        // $"{name} 收到心跳：编号={connectionId}，" +
        // $"来源={remote}，时间={Now:F2}",
        // this);
            }
            else if (type == NetMessageType.ReliableTest)
            {
                Debug.Log(
                    $"服务端收到可靠消息：{data}",
                    this);
            }
            else if (type == NetMessageType.DisConnect)
            {
                existing.connectionState=ConnectionState.DisConnect;
                existing.Replication.Clear();
                existing.ReliableReceiver.Clear();
                connections.Remove(remote);
                Debug.Log(
                    $"客户端主动断开：{existing.connectionID}");
            }
        }
        private void DispatchMessage(string remote,
            NetMessageType type,
            uint connectionId,
            string data)
        {
            if (isServer)
            {
                HandleServerMessage(
                    remote,
                    type,
                    connectionId,
                    data);
            }
            else
            {
                HandleClientMessage(
                    remote,
                    type,
                    connectionId,
                    data);
            }
        }
        private void HandleClientMessage(string remote, NetMessageType type, 
                    uint connectionId,string data)
        {
            Connection connection=serverConnection;
            if(connection==null||connection.connectionState==ConnectionState.DisConnect)
            return;
            if (remote != connection.remoteAddress)
            {
                return;
            }
            if (type == NetMessageType.ConnectAccept)
            {
                if(connectionId==0)return;
                if (connection.connectionState == ConnectionState.Connecting)
                {
                    connection.connectionID=connectionId;
                    connection.connectionState=ConnectionState.Connected;
                    connection.lastReceiveTime=Now; Debug.Log(
                        $"连接成功，编号：{connectionId}");
                }
                else if (connection.connectionID == connectionId)
                {
                    // 允许同一连接的重复确认。
                    connection.lastReceiveTime = Now;
                }

                return;
            }

            if (connection.connectionState != ConnectionState.Connected ||
                connection.connectionID != connectionId)
                return;

            if (type == NetMessageType.Heartbeat)
            {
                connection.lastReceiveTime = Now;
            }
            else if (type == NetMessageType.ReliableTest)
            {
                Debug.Log(
                    $"客户端收到可靠消息：{data}",
                    this);
            }
            else if (type == NetMessageType.DisConnect)
            {
                connection.connectionState =
                    ConnectionState.DisConnect;

                Debug.Log("服务端已断开连接");
            }
            else if (type == NetMessageType.SpawnObject)
            {
                 SpawnData spawnData;
                    try
                    {
                        spawnData =
                            JsonUtility.FromJson<SpawnData>(data);
                    }
                    catch (ArgumentException)
                    {
                        Debug.LogWarning("收到无法解析的 SpawnObject。", this);
                        return;
                    }
                    if (spawnData == null ||
                        spawnData.ObjectId == 0 ||
                        spawnData.ObjectType != "NetworkObject")
                    {
                        Debug.LogWarning("收到无效的对象生成数据。", this);
                        return;
                    }
                    if (despawnedObjectIds.Contains(spawnData.ObjectId))
                    {
                        Debug.Log(
                            $"忽略已销毁对象的生成消息：" +
                            $"ObjectId={spawnData.ObjectId}",
                            this);
                        return;
                    }   
                    // 对象级去重，防止同一个 ObjectId 创建多个实例。
                    if (networkObjectRegistry.TryGet(
                            spawnData.ObjectId,
                            out NetworkObject existing))
                    {
                        return;
                    }
                    GameObject instance = new GameObject(
                        $"{name}_NetworkObject_{spawnData.ObjectId}");
                    NetworkObject networkObject =
                        instance.AddComponent<NetworkObject>();
                    networkObject.Health = spawnData.health;
                    networkObject.StateVersion=spawnData.StateVersion;
                    networkObject.transform.position=spawnData.Position;
                    networkObjectRegistry.RegisterClientObject(
                        networkObject,
                        spawnData.ObjectId);
                    objectReplicators.Add(networkObject.NetObjectId,new ObjectReplicator(networkObject));
                    if (pendingObjectStates.TryGetValue(
                            spawnData.ObjectId,
                            out Dictionary<ushort, PropertyUpdateData> pending))
                    {
                        pendingObjectStates.Remove(spawnData.ObjectId);

                        ObjectUpdateData cachedUpdate = new ObjectUpdateData
                        {
                            ObjectId = spawnData.ObjectId,
                            Properties =
                                new List<PropertyUpdateData>(pending.Values)
                        };
                        ApplyObjectUpdate(networkObject, cachedUpdate);
                    }
                    Debug.Log(
                        $"{name} 创建客户端对象：" +
                        $"ObjectId={networkObject.NetObjectId}，" +
                        $"Health={networkObject.Health}",
                        instance);
            }
            else if (type == NetMessageType.DespawnObject)
            {
                HandleDespawenObject(data);
            }
            else if (type == NetMessageType.StateUpdate)
            {
                HandleStateUpdate(data);
            }
        }
        private void UpdateServer()
        {
             double now = Now;
            expiredAddresses.Clear();
            foreach (var pair in connections)
            {
                Connection connection = pair.Value;

                if (now - connection.lastReceiveTime >= TimeoutSeconds)
                {
                    connection.connectionState =
                        ConnectionState.DisConnect;
                    connection.Replication.Clear();
                    connection.ReliableReceiver.Clear();
                    expiredAddresses.Add(pair.Key);
                    continue;
                }
                FlushReliable(connection);
                if (now - connection.lastSendTime >= HeartbeatInterval)
                {
                    Send(
                        connection,
                        NetMessageType.Heartbeat);
                }
            }
            foreach (string address in expiredAddresses)
            {
                Debug.Log($"客户端超时：{address}");
                connections.Remove(address);
            }
            if (now >= nextStateSyncTime)
            {
                nextStateSyncTime=now+StateSyncInterval;
                BroadcastObjectStates();
            }
        }
        private void UpdateClient()
        {
            Connection connection = serverConnection;
            if (connection == null ||
                connection.connectionState == ConnectionState.DisConnect)
                return;
            double now = Now;
            if (now - connection.lastReceiveTime >= TimeoutSeconds)
            {
                connection.connectionState =
                    ConnectionState.DisConnect;
                Debug.Log("连接超时");
                return;
            }
            FlushReliable(connection);
            if (connection.connectionState == ConnectionState.Connecting)
            {
                if (now >= nextConnectionRequestTime)
                {
                    Send(
                        connection,
                        NetMessageType.ConnectRequest);
                    nextConnectionRequestTime =
                        now + RetryInterval;
                }

                return;
            }

            if (now - connection.lastSendTime >= HeartbeatInterval)
            {
                Send(
                    connection,
                    NetMessageType.Heartbeat);
            }
        }
        private void Send(Connection connection,NetMessageType type)
        {
            byte[] payload=NetMessage.Encode(type,connection.connectionID);
            SendPayload(connection,payload,0);
        }
        private uint SendPayload(
            Connection connection,
            byte[] payload,
            uint reliableMessageId)
        {
            NetPacket packet =
                connection.Packets.CreatePacket(payload);
            byte[] bytes =
                NetPacketCodec.Encode(packet);
            transport.Send(
                connection.remoteAddress,
                bytes);
            double now = Now;
            connection.Packets.RecordSent(
                packet.Sequence,
                now);
            if (reliableMessageId != 0)
            {
                connection.ReliableMessageQueue.MarkSent(
                    reliableMessageId,
                    packet.Sequence,
                    now);
            }
            connection.lastSendTime = now;
            return packet.Sequence;

            // Debug.Log(
            //     $"{name} 发送包：Seq={packet.Sequence}，" +
            //     $"ReliableMessageId={reliableMessageId}",
            //     this);
        }
        public void Disconnect()
        {
            if(isServer||transport==null||serverConnection==null)return;
            Connection connection=serverConnection;
            if(connection.connectionState==ConnectionState.DisConnect)
            return;
            try
            {
                if (connection.connectionState == ConnectionState.Connected)
                {
                    Send(connection,NetMessageType.DisConnect);
                }
            }
            finally
            {
                connection.connectionState=ConnectionState.DisConnect;
            }

        }
        
        private void CloseTransport()
        {
            pendingObjectStates.Clear();
            objectReplicators.Clear();
            if (serverConnection != null)
            {
                serverConnection.connectionState =
                    ConnectionState.DisConnect;
                serverConnection.Replication.Clear();
                serverConnection.ReliableReceiver.Clear();
            }
            foreach (Connection connection in connections.Values)
            {
                connection.connectionState =
                    ConnectionState.DisConnect;
                connection.Replication.Clear();
                connection.ReliableReceiver.Clear();
            }
                
            connections.Clear();

            if (transport == null)
                return;

            transport.Dispose();
            transport = null;
        }
        private void OnDestroy()
        {
            CloseTransport();
        }
        private void OnApplicationQuit()
        {
            CloseTransport();
        }
        private Connection FindPacketConnection(string remote,NetMessageType type,
                                                uint connectionId)
        {
            if (isServer)
            {
                if (type == NetMessageType.ConnectRequest)
                {
                    if(connectionId!=0)return null;
                    if(!connections.TryGetValue(remote,out Connection connection))
                    {
                        if (nextConnectionId == 0)
                    throw new InvalidOperationException("连接编号已耗尽。");
                    connection = new Connection(remote, Now)
                    {
                        connectionID = nextConnectionId++,
                        connectionState = ConnectionState.Connected
                    };
                    connections.Add(remote, connection);
                    Debug.Log(
                        $"客户端接入：{connection.connectionID}，{remote}",this);
                    }
                    return connection;
                }
                if(!connections.TryGetValue(remote,out Connection existing))
                {
                    return null;
                }
                if(existing.connectionID!=connectionId)return null;
                if(existing.connectionState!=ConnectionState.Connected)return null;
                 // 当前服务端只处理这两种已连接消息。
                if (type != NetMessageType.Heartbeat &&
                    type != NetMessageType.DisConnect&&type!=NetMessageType.ReliableTest
                  )
                return null;
                return existing;
            }
            // 客户端只接收配置的服务端地址。
            Connection server = serverConnection;
            if (server == null ||
                server.connectionState == ConnectionState.DisConnect ||
                remote != server.remoteAddress)
                return null;
            if (type == NetMessageType.ConnectAccept)
            {
                if (connectionId == 0)
                    return null;
                if (server.connectionState == ConnectionState.Connecting ||
                    server.connectionID == connectionId)
                    return server;
                return null;
            }
            if (server.connectionState != ConnectionState.Connected ||
                server.connectionID != connectionId)
                return null;
            if (type != NetMessageType.Heartbeat &&
                type != NetMessageType.DisConnect&&
                type!=NetMessageType.ReliableTest&&type != NetMessageType.SpawnObject &&
    type != NetMessageType.DespawnObject &&
    type != NetMessageType.StateUpdate)
                return null;
            return server;
        }
        private void QueueReliable(
            Connection connection,
            NetMessageType type,string data)
        {
            connection.ReliableMessageQueue.Enqueue(
                messageId =>
                    NetMessage.Encode(
                        type,
                        connection.connectionID,
                        messageId,
                        true,data),
                Now);
        }
        private void FlushReliable(
            Connection connection)
        {
            List<ReliableMessageQueue.OutboundMessage> due =
                connection.ReliableMessageQueue.CollectDue(
                    Now,
                    RetryInterval,
                    8);

            foreach (
                ReliableMessageQueue.OutboundMessage message
                in due)
            {
                SendPayload(
                    connection,
                    message.Payload,
                    message.MessageId);
            }
        }
        private void FlushPropertyUpdates(Connection connection,double now)
        {
            List<ObjectUpdateData> due=connection.Replication.CollectDue(now,RetryInterval);
            foreach(ObjectUpdateData update in due)
            {
                string json=JsonUtility.ToJson(update);
                byte[] payload=NetMessage.Encode(NetMessageType.StateUpdate,connection.connectionID,
                                                    0,false,json);
                uint packetSequence=SendPayload(connection,payload,0);
                connection.Replication.RecordSent(packetSequence,update,Now);
            }           
        }
        private void SendSpawnObject(Connection connection,NetworkObject networkObject)
        {
            if(networkObject==null)return;
            SpawnData spawnData = new SpawnData
            {
                ObjectId = networkObject.NetObjectId,
                ObjectType = "NetworkObject",
                health = networkObject.Health,
                StateVersion=networkObject.StateVersion,
                Position=networkObject.transform.position
            };
            string json = JsonUtility.ToJson(spawnData);
            QueueReliable(
                connection,
                NetMessageType.SpawnObject,
                json);
        }
        private void QueueInitialObjects(Connection connection)
        {
            // 同一条连接只排入一次。
            if (connection.InitialObjectsQueued)
                return;
            foreach (NetworkObject networkObject
                    in networkObjectRegistry.GetAllObject())
            {
                if (networkObject == null)
                    continue;
                SendSpawnObject(connection, networkObject);
            }
            connection.InitialObjectsQueued = true;
            Debug.Log(
                $"已为客户端 {connection.connectionID} " +
                "排入已有对象的生成消息。",
                this);
        }
        [ContextMenu("测试服务器生成对象")]
        public void SpawnTestObject()
        {
            if(!isServer||transport==null)
            {
                Debug.LogWarning("请在运行中的服务器上调用。", this);
                return;
            }
            GameObject instance=new GameObject("ServerNetworkObject");
            NetworkObject networkObject =
                instance.AddComponent<NetworkObject>();
            networkObject.Health = 100;
            networkObjectRegistry.RegisterServerObject(
                networkObject);
            objectReplicators.Add(networkObject.NetObjectId,new ObjectReplicator(networkObject));
            foreach (Connection connection in connections.Values)
                {
                    if (connection.connectionState !=
                        ConnectionState.Connected)
                    {
                        continue;
                    }
                    SendSpawnObject(connection,networkObject);
                }
            Debug.Log(
                $"服务器创建对象，ObjectId={networkObject.NetObjectId}",
                instance);
        }
        public bool DespawnServerObject(uint objectId)
        {
            if(!isServer||transport==null)return false;
            if (objectId == 0 ||
                !networkObjectRegistry.TryGet(
                    objectId,
                    out NetworkObject networkObject))
            {
                Debug.LogWarning(
                    $"服务器找不到对象：{objectId}",
                    this);
                return false;
            }
            foreach (Connection connection in connections.Values)
            {
                if (connection.connectionState ==
                        ConnectionState.Connected &&
                    connection.ReliableMessageQueue.PendingCount >= 256)
                {
                    Debug.LogWarning(
                        "可靠队列已满，请稍后重试销毁。",
                        this);
                    return false;
                }
            }
            DespawnData despawnData = new DespawnData
            {
                ObjectId = objectId
            };
            string json = JsonUtility.ToJson(despawnData);
            // 每个客户端有独立的可靠消息队列。
            foreach (Connection connection in connections.Values)
            {
                if (connection.connectionState !=
                    ConnectionState.Connected)
                {
                    continue;
                }
                QueueReliable(
                    connection,
                    NetMessageType.DespawnObject,
                    json);
                connection.Replication.RemoveObject(objectId);
            }
            objectReplicators.Remove(objectId);
            // 从服务器注册表移除。
            networkObjectRegistry.Remove(objectId);
            // Unity 的 Destroy 会在本帧稍后实际销毁对象。
            if (networkObject != null)
            {
                Destroy(networkObject.gameObject);
            }
            Debug.Log(
                $"服务器销毁对象：ObjectId={objectId}",
                this);
            return true;
        }
        private void HandleDespawenObject(string data)
        {
            DespawnData despawnData;
            try
            {
                despawnData=JsonUtility.FromJson<DespawnData>(data);
            }
            catch(ArgumentException)
            {
                return;
            }
            if (despawnData == null || despawnData.ObjectId == 0)
                return;
            uint objectId=despawnData.ObjectId;
            // 阻止延迟的生成消息和状态消息重新影响对象。
            despawnedObjectIds.Add(objectId);
            // 对象尚未生成时，可能已经缓存过状态。
            pendingObjectStates.Remove(objectId);
            objectReplicators.Remove(objectId);
            if(networkObjectRegistry.TryGet(objectId,out NetworkObject networkObject))
            {
                networkObjectRegistry.Remove(objectId);
                if(networkObject!=null)Destroy(networkObject.gameObject);
            }
            Debug.Log(
                $"{name} 处理对象销毁：ObjectId={objectId}",
                this);
        }
        // private static bool IsNewerStateVersion(uint value,uint reference)
        // {
        //     return unchecked((int)(value-reference))>0;
        // }
        private void BroadcastObjectStates()
        {
            double now=Now;
            foreach(NetworkObject networkObject in networkObjectRegistry.GetAllObject())
            {
                if(networkObject==null)continue;
                if(!objectReplicators.TryGetValue(networkObject.NetObjectId,out ObjectReplicator objectReplicator))
                {
                    continue;
                }
                if (!objectReplicator.TryCaptureUpdate(
                now,
                out ObjectUpdateData update))
                {
                    continue;
                }
                foreach(Connection connection in connections.Values)
                {
                    if(connection.connectionState!=ConnectionState.Connected)continue;
                    connection.Replication.Merge(update);
                }
                foreach(Connection connection in connections.Values)
                {
                    if(connection.connectionState!=ConnectionState.Connected)continue;
                    FlushPropertyUpdates(connection,now);
                }
            }
        }
        private void ApplyObjectUpdate(NetworkObject networkObject,ObjectUpdateData update)
        {
            if(networkObject==null)return;
            if(objectReplicators.TryGetValue(networkObject.NetObjectId,out ObjectReplicator objectReplicator))
            {
                objectReplicator.ApplyUpdate(update);
            }
        }
        private void HandleStateUpdate(string data)
        {
            ObjectUpdateData update;
            try
            {
                update=JsonUtility.FromJson<ObjectUpdateData>(data);
            }
            catch
            {
                return;
            }
            if(update==null||update.ObjectId==0||update.Properties==null)return;
            if(despawnedObjectIds.Contains(update.ObjectId))return;
            if(networkObjectRegistry.TryGet(update.ObjectId,out NetworkObject networkObject))
            {
                if (networkObject != null)
                {
                    ApplyObjectUpdate(networkObject,update);
                    return;
                }
            }
             foreach (PropertyUpdateData item in update.Properties)
            {
                if (item == null)
                    continue;
                // 只缓存布局认识、且可以解码的属性。
                if (!RepLayout.Instance.TryGetProperty(
                        item.PropertyId,
                        out RepProperty property))
                {
                    continue;
                }
                if (!property.TryReadUpdate(
                        item,
                        new ObjectStateData()))
                {
                    continue;
                }
                if (!pendingObjectStates.TryGetValue(
                        update.ObjectId,
                        out Dictionary<ushort, PropertyUpdateData> pending))
                {
                    pending =
                        new Dictionary<ushort, PropertyUpdateData>();

                    pendingObjectStates.Add(update.ObjectId, pending);
                }
                if (pending.TryGetValue(
                        item.PropertyId,
                        out PropertyUpdateData previous))
                {
                    if (!ObjectReplicator.IsNewerVersion(
                            item.PropertyVersion,
                            previous.PropertyVersion))
                    {
                        continue;
                    }
                }
                pending[item.PropertyId] = item;
            }
        }
    }

}
