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
       //private readonly Dictionary<uint,Dictionary<ushort,PropertyUpdateData>> pendingObjectStates=
                        // Dictionary<uint, Dictionary<ushort, PropertyUpdateData>>();
        private double nextStateSyncTime;
        [SerializeField]private uint testDespawnObjectId=1;
        [SerializeField]private uint testRpcObjectId=1;
        private readonly Dictionary<uint,ObjectReplicator> objectReplicators=new Dictionary<uint, ObjectReplicator>();
        [ContextMenu("测试服务器销毁")]
        public void DespawnTestObject()
        {
            DespawnServerObject(testDespawnObjectId);
        }
        void Awake()
        {
            RpcRegistry.Register(1, (target, argsJson) =>
            {
                HitRpcArgs args;
                try
                {
                    args=JsonUtility.FromJson<HitRpcArgs>(argsJson);
                }
                catch
                {
                    return ;
                }
                if(args==null)return;
                Debug.Log(
                $"收到受击 RPC：ObjectId={target.NetObjectId}, Damage={args.Damage}",
                target);
            });
            RpcRegistry.Register(101, (target, argsJson) =>
            {
                HitRpcArgs args;
                try
                {
                    args = JsonUtility.FromJson<HitRpcArgs>(argsJson);
                }
                catch (ArgumentException)
                {
                    return;
                }
                if (args == null || args.Damage <= 0 || args.Damage > 50)
                    return;
                // 该 handler 应只处理服务器上的权威对象。
                if (!target.IsServerObject)
                    return;
                target.Health = Mathf.Max(0, target.Health - args.Damage);
                Debug.Log(
                    $"服务器处理请求：ObjectId={target.NetObjectId}, " +
                    $"Health={target.Health}",
                    target);
            });
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
                ReliableReceiveBuffer receiver=connection.ReliableReceiver;
                if (reliable && type == NetMessageType.ActorBunch)
                {
                    NetBunch bunch;
                    try
                    {
                        bunch=JsonUtility.FromJson<NetBunch>(data);
                    }
                    catch
                    {
                        continue;
                    }
                    if(bunch==null||bunch.ObjectId==0)continue;
                    if(!connection.ActorChannels.TryGetValue(bunch.ObjectId,out ActorChannel channel))
                    {
                        channel=new ActorChannel(bunch.ObjectId);
                        connection.ActorChannels.Add(bunch.ObjectId,channel);
                    }
                    receiver = channel.ReliableReceiver;
                }
                for (int ackIndex = 0;
                    ackIndex < acknowledgedSequences.Count;
                    ackIndex++)
                {
                    uint acknowledgedSequence =
                        acknowledgedSequences[ackIndex];
                    connection.ReliableMessageQueue
                        .AcknowledgePacket(
                            acknowledgedSequence);
                    foreach(ActorChannel channel in connection.ActorChannels.Values)
                    {
                        channel.ReliableSender.AcknowledgePacket(acknowledgedSequence);
                    }
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
                var closingChannelsToRemove=new List<uint>();
                foreach(var pair in connection.ActorChannels)
                {
                    if (pair.Value.State == ActorChannelState.Closing && pair.Value.ReliableSender.PendingCount == 0)
                    {
                        closingChannelsToRemove.Add(pair.Key);
                    }
                }
                foreach(uint objectId in closingChannelsToRemove)
                {
                    if(connection.ActorChannels.TryGetValue(objectId,out ActorChannel channel))
                    {
                        channel.Close();
                        connection.ActorChannels.Remove(objectId);
                    }
                }
                if(reliable&&!receiver.CanAccept(messageId))continue;

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
                    receiver.Receive(
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
                ClearActorChannels(existing);
                connections.Remove(remote);
                Debug.Log(
                    $"客户端主动断开：{existing.connectionID}");
            }
            else if (type == NetMessageType.ActorBunch)
            {
                HandleActorBunch(existing,data);
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
                ClearActorChannels(connection);
                Debug.Log("服务端已断开连接");
            }
            else if (type == NetMessageType.ActorBunch)
            {
                HandleActorBunch(connection,data);
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
                    ClearActorChannels(connection);
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
                ClearActorChannels(connection);
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
                ClearActorChannels(connection);
            }

        }
        
        private void CloseTransport()
        {
            // pendingObjectStates.Clear();
            objectReplicators.Clear();
            if (serverConnection != null)
            {
                serverConnection.connectionState =
                    ConnectionState.DisConnect;
                serverConnection.Replication.Clear();
                serverConnection.ReliableReceiver.Clear();
                ClearActorChannels(serverConnection);
            }
            foreach (Connection connection in connections.Values)
            {
                connection.connectionState =
                    ConnectionState.DisConnect;
                connection.Replication.Clear();
                connection.ReliableReceiver.Clear();
                ClearActorChannels(connection);
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
                    type != NetMessageType.DisConnect&&
                    type!=NetMessageType.ReliableTest&&
                    type!=NetMessageType.ActorBunch
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
                type!=NetMessageType.ReliableTest&&
                type!=NetMessageType.ActorBunch)
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
                    SendPayload(connection,message.Payload,message.MessageId);
                }
            foreach(ActorChannel channel in connection.ActorChannels.Values)
            {
                List<ReliableMessageQueue.OutboundMessage> channelDue =
                    channel.ReliableSender.CollectDue(
                        Now,
                        RetryInterval,
                        8);
                foreach (ReliableMessageQueue.OutboundMessage message
                        in channelDue)
                {
                    uint packetSequence = SendPayload(
                        connection,
                        message.Payload,
                        0);
                    channel.ReliableSender.MarkSent(
                        message.MessageId,
                        packetSequence,
                        Now);
                }
            }
            
        }
        private void FlushPropertyUpdates(Connection connection,double now)
        {
            List<ObjectUpdateData> due=connection.Replication.CollectDue(now,RetryInterval);
            foreach(ObjectUpdateData update in due)
            {
                if (!connection.ActorChannels.TryGetValue(
                update.ObjectId,
                out ActorChannel channel))
                {
                    continue;
                }
                if (channel.State != ActorChannelState.Open)
                    continue;
                NetBunch bunch = new NetBunch
                {
                    ObjectId = update.ObjectId,
                    Type = NetBunchType.Properties,
                    PayloadJson = JsonUtility.ToJson(update)
                };
                byte[] payload = NetMessage.Encode(
                NetMessageType.ActorBunch,
                connection.connectionID,
                0,
                false,
                JsonUtility.ToJson(bunch));
                uint packetSequence = SendPayload(
                    connection,
                    payload,
                    0);

                // 仍然记录：这个包携带了哪些属性版本。
                connection.Replication.RecordSent(
                    packetSequence,
                    update,
                    Now);
            }           
        }
        private void SendSpawnObject(Connection connection,NetworkObject networkObject)
        {
            if(networkObject==null)return;
            uint objectId = networkObject.NetObjectId;
            // 同一连接，不重复打开同一个对象通道。
            if (connection.ActorChannels.ContainsKey(objectId))
                return;
            SpawnData spawnData = new SpawnData
            {
                ObjectId = networkObject.NetObjectId,
                ObjectType = "NetworkObject",
                health = networkObject.Health,
                StateVersion=networkObject.StateVersion,
                Position=networkObject.transform.position
            };
            NetBunch bunch=new NetBunch
            {
              ObjectId=objectId,
              Type=NetBunchType.Open,
              PayloadJson=JsonUtility.ToJson(spawnData)  
            };
            ActorChannel channel=new ActorChannel(objectId);
            channel.Bind(networkObject);
            connection.ActorChannels.Add(objectId,channel);
            QueueReliableBunch(connection,bunch);
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
            var connectedClients = new List<Connection>();
            foreach (Connection connection in connections.Values)
            {
                if (connection.connectionState == ConnectionState.Connected)
                    connectedClients.Add(connection);
            }
             if (connectedClients.Count == 0)
            {
                Debug.LogWarning("当前没有已连接的客户端。", this);
                return;
            }
            foreach(Connection ownerConnection in connectedClients)
            {
                GameObject instance=new GameObject($"ServerPlayerObject_{ownerConnection.connectionID}");
                NetworkObject networkObject=instance.AddComponent<NetworkObject>();
                networkObject.Health=100;
                networkObjectRegistry.RegisterServerObject(networkObject);
                networkObject.OwnerConnectId=ownerConnection.connectionID;
                objectReplicators.Add(networkObject.NetObjectId,new ObjectReplicator(networkObject));
                foreach(Connection recipient in connectedClients)
                {
                    SendSpawnObject(recipient,networkObject);
                }
                 Debug.Log(
                    $"服务器生成对象：ObjectId={networkObject.NetObjectId}, " +
                    $"OwnerConnectionId={networkObject.OwnerConnectId}",
                    instance);
            }
        }
       [ContextMenu("测试发送受击 RPC")]
        public void SendTestHitRpc()
        {
            if (!isServer)
            {
                Debug.LogWarning("请在服务器的 NetworkDriver 上运行此测试。", this);
                return;
            }
            string argsJson = JsonUtility.ToJson(
                new HitRpcArgs { Damage = 25 });
            int queuedCount = 0;
            foreach (Connection connection in connections.Values)
            {
                if (connection.connectionState != ConnectionState.Connected)
                    continue;
                if (!connection.ActorChannels.TryGetValue(
                        testRpcObjectId,
                        out ActorChannel channel) ||
                    channel.State != ActorChannelState.Open)
                {
                    continue;
                }
                SendClientRpc(
                    connection,
                    testRpcObjectId,
                    1,
                    argsJson);
                queuedCount++;
            }
            Debug.Log(
                $"已为 {queuedCount} 个客户端排入受击 RPC，对象编号={testRpcObjectId}",
                this);
        }
        [ContextMenu("测试客户端请求服务器 RPC")]
        public void TestServerRpc()
        {
            if (isServer) 
            {
                Debug.LogWarning("这个测试入口必须在客户端运行。", this);
                return;
            }
            SendServerRpc(
                testRpcObjectId,
                101,
                JsonUtility.ToJson(new HitRpcArgs { Damage = 10 }));
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
                    connection.ActorChannels.TryGetValue(objectId,out ActorChannel channel)
                            &&channel.ReliableSender.PendingCount >= 256)
                {
                    Debug.LogWarning(
                        "对象通道可靠队列已满",
                        this);
                    return false;
                }
            }
            // 每个客户端有独立的可靠消息队列。
            foreach (Connection connection in connections.Values)
            {
                if (connection.connectionState !=
                    ConnectionState.Connected)
                {
                    continue;
                }
                if (connection.ActorChannels.TryGetValue(
                        objectId,
                        out ActorChannel channel))
                {
                    NetBunch bunch = new NetBunch
                    {
                        ObjectId = objectId,
                        Type = NetBunchType.Close,
                        PayloadJson = string.Empty
                    };
                    connection.Replication.RemoveObject(objectId);
                    QueueReliableBunch(connection,bunch);
                    channel.BeginClose();
                }
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
        // private void HandleDespawenObject(string data)
        // {
        //     DespawnData despawnData;
        //     try
        //     {
        //         despawnData=JsonUtility.FromJson<DespawnData>(data);
        //     }
        //     catch(ArgumentException)
        //     {
        //         return;
        //     }
        //     if (despawnData == null || despawnData.ObjectId == 0)
        //         return;
        //     uint objectId=despawnData.ObjectId;
        //     // 阻止延迟的生成消息和状态消息重新影响对象。
        //     despawnedObjectIds.Add(objectId);
        //     // 对象尚未生成时，可能已经缓存过状态。
        //     pendingObjectStates.Remove(objectId);
        //     objectReplicators.Remove(objectId);
        //     if(networkObjectRegistry.TryGet(objectId,out NetworkObject networkObject))
        //     {
        //         networkObjectRegistry.Remove(objectId);
        //         if(networkObject!=null)Destroy(networkObject.gameObject);
        //     }
        //     Debug.Log(
        //         $"{name} 处理对象销毁：ObjectId={objectId}",
        //         this);
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
                    if (!connection.ActorChannels.ContainsKey(
                    update.ObjectId))
                    {
                        continue;
                    }
                    connection.Replication.Merge(update);
                }
            }
            foreach(Connection connection in connections.Values)
            {
                if(connection.connectionState!=ConnectionState.Connected)continue;
                FlushPropertyUpdates(connection,now);
            }   
        }
        // private void ApplyObjectUpdate(NetworkObject networkObject,ObjectUpdateData update)
        // {
        //     if(networkObject==null)return;
        //     if(objectReplicators.TryGetValue(networkObject.NetObjectId,out ObjectReplicator objectReplicator))
        //     {
        //         objectReplicator.ApplyUpdate(update);
        //     }
        // }
        // private void HandleStateUpdate(string data)
        // {
        //     ObjectUpdateData update;
        //     try
        //     {
        //         update=JsonUtility.FromJson<ObjectUpdateData>(data);
        //     }
        //     catch
        //     {
        //         return;
        //     }
        //     if(update==null||update.ObjectId==0||update.Properties==null)return;
        //     if(despawnedObjectIds.Contains(update.ObjectId))return;
        //     if(networkObjectRegistry.TryGet(update.ObjectId,out NetworkObject networkObject))
        //     {
        //         if (networkObject != null)
        //         {
        //             ApplyObjectUpdate(networkObject,update);
        //             return;
        //         }
        //     }
        //      foreach (PropertyUpdateData item in update.Properties)
        //     {
        //         if (item == null)
        //             continue;
        //         // 只缓存布局认识、且可以解码的属性。
        //         if (!RepLayout.Instance.TryGetProperty(
        //                 item.PropertyId,
        //                 out RepProperty property))
        //         {
        //             continue;
        //         }
        //         if (!property.TryReadUpdate(
        //                 item,
        //                 new ObjectStateData()))
        //         {
        //             continue;
        //         }
        //         if (!pendingObjectStates.TryGetValue(
        //                 update.ObjectId,
        //                 out Dictionary<ushort, PropertyUpdateData> pending))
        //         {
        //             pending =
        //                 new Dictionary<ushort, PropertyUpdateData>();

        //             pendingObjectStates.Add(update.ObjectId, pending);
        //         }
        //         if (pending.TryGetValue(
        //                 item.PropertyId,
        //                 out PropertyUpdateData previous))
        //         {
        //             if (!ObjectReplicator.IsNewerVersion(
        //                     item.PropertyVersion,
        //                     previous.PropertyVersion))
        //             {
        //                 continue;
        //             }
        //         }
        //         pending[item.PropertyId] = item;
        //     }
        // }
        private void QueueReliableBunch(Connection connection,NetBunch bunch)
        {
            if(!connection.ActorChannels.TryGetValue(bunch.ObjectId,out ActorChannel channel))
            {
                channel=new ActorChannel(bunch.ObjectId);
                connection.ActorChannels.Add(bunch.ObjectId,channel);
            }
            channel.QueueReliableBunch(connection.connectionID,bunch);
        }
        private void  HandleActorBunch(Connection connection,string data)
        {
             NetBunch bunch;
            try
            {
                bunch = JsonUtility.FromJson<NetBunch>(data);
            }
            catch (ArgumentException)
            {
                return;
            }
            if (bunch == null || bunch.ObjectId == 0)
                return;
            // 已关闭对象不能被迟到的消息重新创建。
            if (despawnedObjectIds.Contains(bunch.ObjectId))
                return;
            if(isServer&&bunch.Type!=NetBunchType.Rpc)return;
            switch (bunch.Type)
            {
                case NetBunchType.Open:
                    HandleChannelOpen(connection, bunch);
                    break;
                case NetBunchType.Properties:
                    HandleChannelProperties(connection, bunch);
                    break;
                case NetBunchType.Close:
                    HandleChannelClose(connection, bunch.ObjectId);
                    break;
                case NetBunchType.Rpc:
                    HandleChannelRpc(connection,bunch);
                    break;
            }
        }

        private void HandleChannelRpc(Connection connection, NetBunch bunch)
        {
            RpcCallData call;
            try
            {
                call=JsonUtility.FromJson<RpcCallData>(bunch.PayloadJson);
            }
            catch
            {
                return;
            }
            RpcDirection exceptedDirection=isServer?RpcDirection.Server:RpcDirection.Client;
            if(call==null||call.Direction!=exceptedDirection)return;
            if (isServer)
            {
                if (!networkObjectRegistry.TryGet(
                        bunch.ObjectId,
                        out NetworkObject serverObject) ||
                    serverObject == null ||
                    serverObject.OwnerConnectId != connection.connectionID)
                {
                    Debug.LogWarning(
                        $"拒绝无权限的 RPC：ConnectionId={connection.connectionID}, " +
                        $"ObjectId={bunch.ObjectId}, RpcId={call.RpcId}",
                        this);
                    return;
                }
            }
            if(!connection.ActorChannels.TryGetValue(bunch.ObjectId
                                    ,out ActorChannel channel))
            {
                return;
            }
            if (!channel.ReceiveRpc(call))
            {
                Debug.LogWarning(
                    $"RPC 未执行：ObjectId={bunch.ObjectId}, RpcId={call.RpcId}",
                    this);
            }
        }

        private void HandleChannelOpen(
            Connection connection,
            NetBunch bunch)
        {
            SpawnData spawnData;
            try
            {
                spawnData =
                    JsonUtility.FromJson<SpawnData>(
                        bunch.PayloadJson);
            }
            catch (ArgumentException)
            {
                return;
            }
            if (spawnData == null ||
                spawnData.ObjectId != bunch.ObjectId ||
                spawnData.ObjectType != "NetworkObject")
            {
                return;
            }
            if (!connection.ActorChannels.TryGetValue(
                    bunch.ObjectId,
                    out ActorChannel channel))
            {
                channel = new ActorChannel(bunch.ObjectId);
                connection.ActorChannels.Add(
                    bunch.ObjectId,
                    channel);
            }
            // 防止重复 Open 重置对象状态。
            if (channel.State != ActorChannelState.Opening)
                return;
            NetworkObject networkObject;
            if (!networkObjectRegistry.TryGet(
                    bunch.ObjectId,
                    out networkObject) ||
                networkObject == null)
            {
                GameObject instance = new GameObject(
                    $"{name}_NetworkObject_{bunch.ObjectId}");
                networkObject =
                    instance.AddComponent<NetworkObject>();
                networkObject.Health = spawnData.health;
                networkObject.StateVersion =
                    spawnData.StateVersion;
                networkObject.transform.position =
                    spawnData.Position;
                networkObjectRegistry.RegisterClientObject(
                    networkObject,
                    bunch.ObjectId);
            }
            // Bind 内部创建客户端复制器，
            // 并应用在 Open 之前收到的属性更新。
            channel.Bind(networkObject);
            Debug.Log(
                $"打开通道并创建对象：ObjectId={bunch.ObjectId}",
                networkObject);
        }
        private void HandleChannelProperties(
            Connection connection,
            NetBunch bunch)
        {
            ObjectUpdateData update;
            try
            {
                update =
                    JsonUtility.FromJson<ObjectUpdateData>(
                        bunch.PayloadJson);
            }
            catch (ArgumentException)
            {
                return;
            }
            if (update == null ||
                update.ObjectId != bunch.ObjectId ||
                update.Properties == null)
            {
                return;
            }

            if (!connection.ActorChannels.TryGetValue(
                    bunch.ObjectId,
                    out ActorChannel channel))
            {
                // 属性先于 Open 到达：
                // 先创建 Opening 状态的通道。
                channel = new ActorChannel(bunch.ObjectId);
                connection.ActorChannels.Add(
                    bunch.ObjectId,
                    channel);
            }
            channel.ReceiveProperties(update);
        }
        private void HandleChannelClose(
            Connection connection,
            uint objectId)
        {
            // 先标记销毁，阻止迟到属性重新建立通道。
            despawnedObjectIds.Add(objectId);
            if (connection.ActorChannels.TryGetValue(
                    objectId,
                    out ActorChannel channel))
            {
                channel.Close();
                //connection.ActorChannels.Remove(objectId);
            }
            if (networkObjectRegistry.TryGet(
                    objectId,
                    out NetworkObject networkObject))
            {
                networkObjectRegistry.Remove(objectId);
                if (networkObject != null)
                    Destroy(networkObject.gameObject);
            }
            Debug.Log(
                $"关闭通道并销毁对象：ObjectId={objectId}",
                this);
        }
        private void ClearActorChannels(Connection connection)
        {
            foreach (ActorChannel channel
                    in connection.ActorChannels.Values)
            {
                NetworkObject target = channel.Target;
                channel.Close();
                // 客户端断线时清理复制出来的对象。
                if (!isServer && target != null)
                {
                    networkObjectRegistry.Remove(
                        channel.ObjectId);
                    Destroy(target.gameObject);
                }
            }
            connection.ActorChannels.Clear();
        }        
        private void SendClientRpc(Connection connection,uint objectId,
                            ushort rpcId,string argsJson)
        {
            if(!isServer||connection.connectionState!=ConnectionState.Connected
                ||!connection.ActorChannels.TryGetValue(objectId,
                    out ActorChannel channel)||channel.State!=ActorChannelState.Open)
            {
                return;
            }
            var call=new RpcCallData
            {
                RpcId=rpcId,
                Direction=RpcDirection.Client,
                ArgsJson=argsJson??string.Empty
            };
            var bunch=new NetBunch
            {
                ObjectId=objectId,
                Type=NetBunchType.Rpc,
                PayloadJson=JsonUtility.ToJson(call)
            };
            QueueReliableBunch(connection,bunch);
        }
        private void SendServerRpc(uint objectId,ushort rpcId,string argsJson)
        {
            if(isServer||serverConnection==null||
                serverConnection.connectionState!=ConnectionState.Connected)
                {
                    return;
                }
            if(!serverConnection.ActorChannels.TryGetValue(objectId,out ActorChannel channel)||
                    channel.State != ActorChannelState.Open)
            {
                return;
            }
            var call=new RpcCallData
            {
                RpcId=rpcId,
                Direction=RpcDirection.Server,
                ArgsJson=argsJson??string.Empty
            };
            var bunch=new NetBunch
            {
                ObjectId=objectId,
                Type=NetBunchType.Rpc,
                PayloadJson=JsonUtility.ToJson(call)
            };
            QueueReliableBunch(serverConnection,bunch);
        }
    
    }

}
