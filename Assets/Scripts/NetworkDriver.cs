using System;
using System.Collections;
using System.Collections.Generic;
    using System.IO;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SocialPlatforms;

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
        private readonly HashSet<uint>pendingPlayerDespawns=new HashSet<uint>();
        private const double StateSyncInterval=0.1;
        private const float RelevancyDistance=20f;
        private double nextStateSyncTime;
        [SerializeField]private uint testDespawnObjectId=1;
        [SerializeField]private uint testRpcObjectId=1;
        [SerializeField]private uint testAttackTargetObject=2;
        [SerializeField]private uint testSubobjectId;
        private readonly Dictionary<uint, List<uint>> testCreatedSubobjectIds =
        new Dictionary<uint, List<uint>>();
        private uint localPlayerObjectId;
        private readonly Dictionary<uint,ObjectReplicator> objectReplicators=new Dictionary<uint, ObjectReplicator>();
        [SerializeField,Min(1)]private int propertyBytesPerTick=4096;
        [SerializeField]private bool simulateNetwork = false;
        [SerializeField, Range(0f, 1f)]private float simulatedLoss = 0.1f;
        [SerializeField, Range(0f, 1f)]private float simulatedDuplicate = 0.02f;
        [SerializeField, Min(0f)]private float simulatedLatencyMs = 100f;
        [SerializeField, Min(0f)]private float simulatedJitterMs = 50f;
        [SerializeField] private int simulationSeed = 12345;
        private SimulatedTransport simulatedTransport;
        [SerializeField] private NetworkPrefabRegistry prefabRegistry;
        [SerializeField] private string playerObjectType="Player";
        [SerializeField] private string testSpawnObjectType="Enemy";
        [SerializeField] private Vector3 testSpawnPosition=new Vector3(3f,0,0);
        public sealed class IncomingMessage
        {
            public NetMessageType Type;
            public uint ConnectionId;
            public uint MessageId;
            public bool Reliable;
            public string Data;
        }
       
        [ContextMenu("测试创建动态子对象")]
        private void TestCreateSubobject()
        {
            if (!isServer)
            {
                Debug.LogWarning("请在服务器上执行。", this);
                return;
            }

            if (testRpcObjectId == 0)
            {
                Debug.LogWarning(
                    "请先设置 testRpcObjectId。",
                    this);
                return;
            }

            uint subobjectId =
                ServerCreateItem(
                    testRpcObjectId,
                    1001,
                    1);

            if (subobjectId == 0)
            {
                Debug.LogWarning(
                    "动态子对象创建失败。",
                    this);
                return;
            }

           if (!testCreatedSubobjectIds.TryGetValue(
                    testRpcObjectId,
                    out List<uint> itemIds))
            {
                itemIds = new List<uint>();

                testCreatedSubobjectIds.Add(
                    testRpcObjectId,
                    itemIds);
            }

            itemIds.Add(subobjectId);

            Debug.Log(
                $"服务器创建动态子对象：" +
                $"OwnerObjectId={testRpcObjectId}, " +
                $"SubobjectId={subobjectId}",
                this);
        }
        [ContextMenu("测试销毁动态子对象")]
        private void TestDestroySubobject()
        {
            
        if (!isServer)
        {
            Debug.LogWarning("请在服务器上执行。", this);
            return;
        }
        if (testRpcObjectId == 0 ||
            testSubobjectId == 0)
        {
            Debug.LogWarning(
                "请在 Inspector 填写 testRpcObjectId 和 testSubobjectId。",
                this);
            return;
        }
        if (!ServerDestroyItem(
                testRpcObjectId,
                testSubobjectId))
        {
            Debug.LogWarning(
                $"动态子对象销毁失败：" +
                $"OwnerObjectId={testRpcObjectId}, " +
                $"SubobjectId={testSubobjectId}",
                this);
            return;
        }
        if (testCreatedSubobjectIds.TryGetValue(
                testRpcObjectId,
                out List<uint> itemIds))
        {
            itemIds.Remove(testSubobjectId);

            if (itemIds.Count == 0)
            {
                testCreatedSubobjectIds.Remove(
                    testRpcObjectId);
            }
        }
        Debug.Log(
            $"服务器销毁动态子对象：" +
            $"OwnerObjectId={testRpcObjectId}, " +
            $"SubobjectId={testSubobjectId}",
            this);
        }
        
        [ContextMenu("动态物品数量变化同步测试")]
        private void UpdateServerReplication()
        {
            ServerSetItemCount(testRpcObjectId,2,10);
        }
        [ContextMenu("测试服务器销毁")]
        public void DespawnTestObject()
        {
            DespawnServerObject(testDespawnObjectId);
        }
        void Awake()
        {
            RpcRegistry.Register(201, (target, argsJson) =>
            {
                if (target == null || target.IsServerObject)
                    return;
                DelayRpcTestArgs args;
                try
                {
                    args = JsonUtility.FromJson<DelayRpcTestArgs>(
                        argsJson);
                }
                catch (ArgumentException)
                {
                    return;
                }
                if (args == null)
                    return;
                if (args.ReferenceId != 0)
                {
                    if (!target.Registry.TryGet(
                            args.ReferenceId,
                            out NetworkObject referencedObject) ||
                        referencedObject == null)
                    {
                        Debug.LogError(
                            $"[延迟测试] RPC 已执行，但依赖对象不存在：" +
                            $"Order={args.Order}, B={args.ReferenceId}",
                            target);
                        return;
                    }
                }
                Debug.Log(
                    $"[延迟测试] 执行 RPC：Order={args.Order}, " +
                    $"A={target.NetObjectId}, " +
                    $"引用编号={args.ReferenceId}",
                    target);
            });
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
            if(isServer)
            {
            RpcRegistry.Register(101, (attacker, argsJson) =>
            {
                if (!isServer || attacker == null || !attacker.IsServerObject)
                return;
                AttackRpcArgs args;
                try
                {
                    args = JsonUtility.FromJson<AttackRpcArgs>(argsJson);
                }
                catch (ArgumentException)
                {
                    return;
                }
                if (args == null || args.TargetObjectId == 0 || args.TargetObjectId==attacker.NetObjectId)
                    return;
                // 该 handler 应只处理服务器上的权威对象。
                if (!networkObjectRegistry.TryGet(args.TargetObjectId,out NetworkObject victim)||
                        victim == null || !victim.IsServerObject)
                {
                    Debug.LogWarning(
                        $"攻击目标不存在：ObjectId={args.TargetObjectId}",
                        this);
                    return;
                }
                float distance=Vector3.Distance(attacker.transform.position,
                        victim.transform.position);
                if (distance > 3f)
                {
                    Debug.LogWarning(
                        $"攻击距离过远：Distance={distance:F2}",
                        this);
                    return;
                }
                if (!ServerSetTarget(
                        attacker.NetObjectId,
                        victim.NetObjectId))
                {
                    return;
                }
                const int damage = 10;
                if (!ServerSetHealth(victim.NetObjectId,
                        Mathf.Max(0, victim.Health - damage)))
                {
                    return;
                }
                            Debug.Log(
                $"攻击成功：attacker={attacker.NetObjectId}, " +
                $"target={victim.NetObjectId}, Health={victim.Health}",
                victim);
                attacker.PrivateScore+=10;
                ServerForceNetUpdate(attacker.NetObjectId);
                string hitArgsJson=JsonUtility.ToJson(new HitRpcArgs{Damage=damage});
                foreach (Connection connection in connections.Values)
                {
                    if (connection.connectionState != ConnectionState.Connected)
                        continue;
                    if (!connection.ActorChannels.TryGetValue(
                            victim.NetObjectId,
                            out ActorChannel channel) ||
                        channel.State != ActorChannelState.Open)
                    {
                        continue;
                    }
                    SendClientRpc(
                        connection,
                        victim.NetObjectId,
                        1,
                        hitArgsJson);
                }
            });
            }
            Application.runInBackground=true;
        }
        // Start is called before the first frame update
        void Start()
        {
            try
            {
               CreateTransport();
               if(!isServer)
                    BeginClientConnection();
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
                if (simulatedTransport != null)
                {
                    simulatedTransport.Enabled = simulateNetwork;
                }
                simulatedTransport?.Pump();
                ReceivePackets();
                if(isServer)UpdateServer();
                else UpdateClient();
                UpdatePendingRpcs();
                simulatedTransport?.Pump();
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
                if (FragmentCodec.IsFragment(packet.Payload))
                {
                    HandleFragmentPacket(datagram.Remote, packet);
                    continue;
                }
                if(!TryDecodePacketMessage(packet.Payload,out List<IncomingMessage>incoming))
                {
                    continue;
                }
                IncomingMessage first=incoming[0];
                NetMessageType type=first.Type;
                uint connectionId = first.ConnectionId;
                uint messageId = first.MessageId;
                bool reliable = first.Reliable;
                string data = first.Data;
                if(!CheckHandshake(datagram.Remote,packet,first,
                                out NetHandshakeData handshake))
                    continue;
                Connection connection = FindPacketConnection(
                    datagram.Remote,
                    type,
                    connectionId,handshake );
                if (connection == null)
                    continue;
                double now = Now;
                // 4. 对方告诉我们：自己发出的哪些包已被收到。
                ProcessPacketAcknowledgements(connection,packet,now);
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
                        connection.Scheduler.RemoveObject(objectId);
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
                    foreach(IncomingMessage item in incoming)
                    {
                        DispatchMessage(
                        datagram.Remote,
                        item.Type,
                        item.ConnectionId,
                        item.Data);
                        if(connection.connectionState==ConnectionState.DisConnect)
                        break;
                    }
                    
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
                SpawnPlayerForConnection(connection);
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
            else if (type == NetMessageType.ActorBunch)
            {
                HandleActorBunch(existing,data);
            }
            else if (type == NetMessageType.DisConnect)
            {
                RemoveServerConnection( existing,"客户端主动断开");
            }
            else if (type == NetMessageType.FragmentAck)
            {
                HandleFragmentAck(existing,data);
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
                 EndClientConnection(connection,"服务器主动断开");
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
                connection.FragmentReceiver.Expire(now);
                if (now - connection.lastReceiveTime >= TimeoutSeconds)
                {
                    connection.connectionState =
                        ConnectionState.DisConnect;
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
               if (connections.TryGetValue(
                address,
                out Connection connection))
                {
                    RemoveServerConnection(connection,"收包超时");
                }
            }
            if (now >= nextStateSyncTime)
            {
                nextStateSyncTime=now+StateSyncInterval;
                ProcessPendingPlayerDespawns();
                ReleaseDisconnectedObjectOwners();
                SampleServerMovements(now);
                foreach(Connection connection in connections.Values)
                {
                    if (connection.connectionState == ConnectionState.Connected)
                    {
                        UpdateActorRelevancy(connection);
                    }
                }
                BroadcastObjectStates();
                foreach (Connection connection
                        in connections.Values)
                {
                    if (connection.connectionState !=
                        ConnectionState.Connected)
                    {
                        continue;
                    }
                    UpdateActorDormancy(
                        connection,
                        now);
                }
            }
        }
        private void UpdateClient()
        {
            Connection connection = serverConnection;
            if (connection == null ||
                connection.connectionState == ConnectionState.DisConnect)
                return;
            double now = Now;
            connection.FragmentReceiver.Expire(now);
            if (now - connection.lastReceiveTime >= TimeoutSeconds)
            {
                EndClientConnection(connection,"连接超时");
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
            string data=string.Empty;
            if(type==NetMessageType.ConnectRequest||
                type == NetMessageType.ConnectAccept)
            {
                data=NetProtocol.CreateHandshakeJson(connection.ClientNonce,connection.SessionId);
            }
            byte[] payload=NetMessage.Encode(type,connection.connectionID,0,false,data);
            SendPayload(connection,payload,0);
        }
        private uint SendPayload(
            Connection connection,
            byte[] payload,
            uint reliableMessageId)
        {
            NetPacket packet =
                connection.Packets.CreatePacket(payload,connection.SessionId);
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
        [ContextMenu("客户端断开连接")]
        public void Disconnect()
        {
            EndClientConnection(serverConnection,"主动断开",notifyServer:true);
        }
        [ContextMenu("客户端重新连接")]
        public void Reconnect()
        {
            if (!Application.isPlaying || isServer)
                return;
            if (serverConnection != null &&
                serverConnection.connectionState !=
                    ConnectionState.DisConnect)
            {
                Debug.LogWarning("请先断开当前连接，再执行重新连接。",
                    this);
                return;
            }
            try
            {
                CreateTransport();
                BeginClientConnection();
                enabled = true;
                Debug.Log("客户端开始重新连接。",this);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                CloseTransport();
                enabled = false;
            }
        }
        private void CloseTransport()
        {
            if (!isServer)
            {
                EndClientConnection(serverConnection,"传输关闭");
            }
            pendingPlayerDespawns.Clear();
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
            networkObjectRegistry.Clear();
            if (transport == null)
                return;
            transport.Dispose();
            transport = null;
            simulatedTransport=null;
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
                       uint connectionId,NetHandshakeData handshake=null)
        {
            if (isServer)
            {
                if (type == NetMessageType.ConnectRequest)
                {
                    if (connectionId != 0 ||handshake == null ||
                        handshake.ClientNonce == 0)
                    {
                        return null;
                    }
                    if(connections.TryGetValue(remote,out Connection repeated))
                    {
                        return repeated.connectionState==ConnectionState.Connected&&
                                repeated.ClientNonce==handshake.ClientNonce?repeated:null;
                    }
                    if (nextConnectionId == 0)
                    {
                        throw new InvalidOperationException("连接编号已耗尽。");
                    }
                     var connection = new Connection(remote, Now)
                    {
                        connectionID = nextConnectionId++,
                        connectionState = ConnectionState.Connected,
                        ClientNonce = handshake.ClientNonce,
                        SessionId = NetProtocol.CreateToken()
                    };
                    connections.Add(remote, connection);
                    Debug.Log($"客户端接入：{connection.connectionID}，{remote}",
                        this);
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
                    type!=NetMessageType.ActorBunch&&
                    type!=NetMessageType.FragmentAck
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
             if (connection.connectionState !=
                ConnectionState.Connected)
            {
                return;
            }
            int roundBudget = Mathf.Max(1, propertyBytesPerTick);
            int remainingBytes = roundBudget;
            int packetLimit=Math.Min(roundBudget,UdpTransport.RecommendedPacketBytes);
            PruneFragmentGroups(connection,packetLimit);
            bool fragmentsFirst=connection.SendFragmentsFirst;
            connection.SendFragmentsFirst=!fragmentsFirst;
            if (fragmentsFirst)
            {
                remainingBytes-=SendFragmentPackets(connection,remainingBytes,now);
            }
            List<ObjectUpdateData> due=connection.Replication.CollectDue(now,RetryInterval);
            var candidates=new List<ReplicationScheduler.Candidate>();
            bool hasViewer=networkObjectRegistry.TryGet(connection.PlayerObjectId,out NetworkObject viewer)&&
                    viewer!=null;
            foreach(ObjectUpdateData update in due)
            {
                if (connection.FragmentSender.HasObject(update.ObjectId)
                ||!connection.ActorChannels.TryGetValue(update.ObjectId,
                out ActorChannel channel)||channel==null||channel.State!=ActorChannelState.Open
                        ||channel.Target==null||!channel.Target.IsServerObject)
                {
                    continue;
                }
                NetworkObject actor=channel.Target;
                bool isOwner=actor.OwnerConnectId!=0&&actor.OwnerConnectId==connection.connectionID;
                 float distance = hasViewer? Vector3.Distance(
                viewer.transform.position,
                actor.transform.position): 0f;
                NetBunch bunch = new NetBunch
                {
                    ObjectId = update.ObjectId,
                    Type = NetBunchType.Properties,
                    PayloadJson = JsonUtility.ToJson(update)
                };
                byte[] payload = PropertyMessageCodec.Encode(
                connection.connectionID,update);

                candidates.Add(new ReplicationScheduler.Candidate
                {
                    Update=update,
                    Payload=payload,
                    Priority=connection.Scheduler.GetPriority(update.ObjectId
                            ,isOwner,distance,now)
                });
            }
            candidates.Sort((a, b) =>
            {
                int order=b.Priority.CompareTo(a.Priority);
                return order!=0?order:a.Update.ObjectId.CompareTo(b.Update.ObjectId);
            });
            int emptyPacketBytes=NetPacketCodec.HeaderSize+PacketPayloadCodec.HeaderSize;
            int batchBytes=emptyPacketBytes;
            var batch=new List<ReplicationScheduler.Candidate>();
            foreach(ReplicationScheduler.Candidate candidate in candidates)
            {
                int entryBytes =
                    PacketPayloadCodec.LengthSize +
                    candidate.Payload.Length;
                int standaloneBytes = emptyPacketBytes + entryBytes;
                if (standaloneBytes > packetLimit)
                {
                    bool queued=connection.FragmentSender.TryEnqueue(connection.connectionID
                            ,candidate.Update.ObjectId,candidate.Payload,packetLimit,
                                out FragmentSendQueue.Group group);
                    if (queued &&
                        !connection.Replication.RecordFragmentGroup(
                            group.GroupId,candidate.Update, now))
                    {
                        connection.FragmentSender.RemoveGroup(
                            group.GroupId);
                        queued = false;
                    }            
                    if (!queued &&connection.Scheduler.ShouldWarnOversized(
                            candidate.Update.ObjectId))
                    {
                        Debug.LogWarning(
                            $"对象 {candidate.Update.ObjectId} " +
                            "暂时无法加入分片队列，" +
                            "请检查消息大小、分片数、缓存和每轮预算。",
                            this);
                    }
                    continue;
                }
                if (batch.Count > 0 &&
                    (batchBytes + entryBytes > packetLimit ||
                    batch.Count >= PacketPayloadCodec.MaxMessages))
                {
                    remainingBytes -= SendPropertyBatch(
                        connection,
                        batch);
                    batch.Clear();
                    batchBytes = emptyPacketBytes;
                }
                if (batchBytes + entryBytes > remainingBytes)
                    continue;
                batch.Add(candidate);
                batchBytes += entryBytes;
            }
            if (batch.Count > 0)
            {
                remainingBytes -= SendPropertyBatch(
                    connection,
                    batch);
            }
            remainingBytes -= SendFragmentPackets(
                    connection,remainingBytes,now);
        }
        private void SendSpawnObject(Connection connection,NetworkObject networkObject)
        {
            if(networkObject==null)return;
            if (!prefabRegistry.TryGetPrefab(
                    networkObject.ObjectType,out _))
            {
                throw new InvalidOperationException(
                    $"服务器对象类型未登记：{networkObject.ObjectType}");
            }
            uint objectId = networkObject.NetObjectId;
            // 同一连接，不重复打开同一个对象通道。
            if (connection.ActorChannels.ContainsKey(objectId))
                return;
            SpawnData spawnData = new SpawnData
            {
                ObjectId = networkObject.NetObjectId,
                ObjectType = networkObject.ObjectType,
                OwnerConnectId=networkObject.OwnerConnectId,
                OwnerVersion=networkObject.OwnerVersion,
                IsPlayerObject=networkObject.IsPlayerObject,
                StateVersion=networkObject.StateVersion,
                InitialProperties=BuildInitialProperties(connection,networkObject),
                HasInventory=networkObject.Inventory!=null,
                Inventory=networkObject.Inventory!=null?InventoryRepLayout.Capture(networkObject.Inventory):null
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
            foreach (ReplicatedItemData item
                    in networkObject.ReplicatedItems.Values)
            {
                SendItemCreate(
                    connection,
                    networkObject.NetObjectId,
                    item);
            }
        }
        private void QueueInitialObjects(Connection connection)
        {
            // 同一条连接只排入一次。
            if (connection.InitialObjectsQueued)
                return;
            foreach (NetworkObject networkObject
                    in networkObjectRegistry.GetAllObject())
            {
                if (networkObject == null||!IsActorRelevant(connection,networkObject))
                    continue;
                SendSpawnObject(connection, networkObject);
            }
            connection.InitialObjectsQueued = true;
            Debug.Log(
                $"已为客户端 {connection.connectionID} " +
                "排入已有对象的生成消息。",
                this);
        }
        [ContextMenu("测试服务器补齐对象")]
        public void SpawnTestObject()
        {
            if(!isServer||transport==null)
            {
                Debug.LogWarning("请在运行中的服务器上调用。", this);
                return;
            }
             foreach (Connection connection in connections.Values)
            {
                SpawnPlayerForConnection(connection);
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
             Connection connection = serverConnection;
            if (connection == null ||
                connection.connectionState != ConnectionState.Connected)
            {
                Debug.LogWarning("尚未连接服务器。", this);
                return;
            }
            if (localPlayerObjectId == 0)
            {
                Debug.LogWarning(
                    "尚未收到属于当前客户端的玩家对象。",
                    this);
                return;
            }
            if(!networkObjectRegistry.TryGet(localPlayerObjectId,out NetworkObject player)||
                                player==null||player.IsServerObject
                                || player.OwnerConnectId != connection.connectionID)
            {
                Debug.LogWarning("本地玩家不存在或所有者不匹配",this);
                return;
            }
            if (!connection.ActorChannels.TryGetValue(
                        localPlayerObjectId,
                        out ActorChannel channel) ||
                    channel.State != ActorChannelState.Open)
                {
                    Debug.LogWarning(
                        "本地玩家对象的通道尚未打开。",
                        this);
                    return;
                }
            if (testAttackTargetObject == 0 ||
                testAttackTargetObject == localPlayerObjectId)
            {
                Debug.LogWarning(
                    "请选择其他有效对象作为攻击目标。",
                    this);
                return;
            }
            SendServerRpc(
                localPlayerObjectId,
                101,
                JsonUtility.ToJson(new AttackRpcArgs { TargetObjectId=testAttackTargetObject}),
                testAttackTargetObject
                );
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
                        PayloadJson = string.Empty,
                        DestroyObject=true
                    };
                    connection.Replication.RemoveObject(objectId);
                    connection.FragmentSender.RemoveObject(objectId);
                    QueueReliableBunch(connection,bunch);
                    channel.BeginClose();
                }
                connection.Replication.RemoveObject(objectId);
                connection.FragmentSender.RemoveObject(objectId);
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
        private void BroadcastObjectStates()
        {
            double now=Now;
            foreach(Connection connection in connections.Values)
            {
                if(connection.connectionState!=ConnectionState.Connected)continue;
                var channels=new List<ActorChannel>(connection.ActorChannels.Values);
                foreach(ActorChannel channel in channels)
                {
                    if(channel==null||channel.State!=ActorChannelState.Open||channel.Target==null)
                    continue;
                    if (!IsActorRelevant(connection, channel.Target))
                    {
                        continue;
                    }
                    channel.CheckPositionAndWake(now);
                    channel.CheckRotationAndWake(now);
                    channel.CheckVelocityAndWake(now);
                    if(channel.Isdormant&&!channel.HasForcedUpdate)continue;
                    if(channel.TryBuildActorUpdate(now,out ObjectUpdateData update))
                    {
                        ObjectUpdateData filtered=FilterForConnection(connection,channel.Target,update);
                        if(filtered.Properties.Count>0)
                        {
                            connection.Replication.Merge(filtered);
                            channel.MarkActivity(now);                  
                        }
                    }
                    channel.QueueInventoryUpdates(connection.connectionID);
                    channel.QueueItemUpdates(connection.connectionID);
                    if(channel.HasForcedUpdate)
                    channel.ClearForcedUpdate();
                }
                
                FlushPropertyUpdates(connection,now);
            }
        }
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
                    HandleChannelClose(
                        connection,
                        bunch.ObjectId,
                        bunch.DestroyObject);
                    break;
                case NetBunchType.Rpc:
                    HandleChannelRpc(connection,bunch);
                    break;
                case NetBunchType.SubobjectProperties:
                    HandleInventoryProperties(connection,bunch);
                    break;
                case NetBunchType.SubobjectCreate:
                     {
                    if (!connection.ActorChannels.TryGetValue(
                            bunch.ObjectId,
                            out ActorChannel channel) ||
                        channel == null)
                            {
                                Debug.LogWarning(
                                    $"找不到子对象所属通道：" +
                                    $"ObjectId={bunch.ObjectId}",
                                    this);
                                break;
                            }
                    channel.ReceiveSubobjectCreate(
                        bunch.PayloadJson);
                        break;
                    }
                case NetBunchType.SubobjectDestroy:
                    {
                        if (!connection.ActorChannels.TryGetValue(
                                bunch.ObjectId,
                                out ActorChannel channel) ||
                            channel == null)
                        {
                            Debug.LogWarning(
                                $"找不到子对象所属通道：" +
                                $"ObjectId={bunch.ObjectId}",
                                this);
                            break;
                        }
                        channel.ReceiveSubobjectDestroy(
                            bunch.PayloadJson);
                        break;
                    }
                case NetBunchType.SubobjectItemProperties:
                    {
                        if (!connection.ActorChannels.TryGetValue(
                                bunch.ObjectId,
                                out ActorChannel channel) ||
                            channel == null)
                        {
                            break;
                        }
                        channel.ReceiveSubobjectItemProperties(bunch.PayloadJson);
                        break;
                    }
                case NetBunchType.OwnerChanged:
                    HandleOwnerChanged(connection,bunch);
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
                    serverObject.OwnerConnectId != connection.connectionID||
                    call.OwnerVersion!=serverObject.OwnerVersion)
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
            if (!channel.ReceiveRpc(call,networkObjectRegistry,Now,allowUnmappedDelay:!isServer))
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
                string.IsNullOrWhiteSpace(spawnData.ObjectType)||
                spawnData.InitialProperties==null)
            {
                return;
            }
            if (!prefabRegistry.TryGetPrefab(
                    spawnData.ObjectType,out _))
            {
                EndClientConnection(connection,
                    $"本地未登记服务器对象类型：{spawnData.ObjectType}",
                    notifyServer: true);
                return;
            }
            if (spawnData.HasInventory)
            {
                if (!InventoryRepLayout.IsValid(
                        spawnData.Inventory) ||
                    spawnData.Inventory.PlayerObjectId !=
                        bunch.ObjectId ||
                    spawnData.Inventory.SubobjectId != 1)
                {
                    return;
                }
            }
            if (!connection.ActorChannels.TryGetValue(
                    bunch.ObjectId,
                    out ActorChannel channel))
            {
                channel = new ActorChannel(bunch.ObjectId);
                connection.ActorChannels.Add(bunch.ObjectId,channel);
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
                if(!prefabRegistry.TryInstantiate(spawnData.ObjectType,out networkObject))
                {
                    EndClientConnection(connection,
                    $"无法生成网路对象:{spawnData.ObjectType}",
                    notifyServer:true);
                    return;
                }
                networkObject.gameObject.name=$"{name}_{spawnData.ObjectType}_{bunch.ObjectId}";
                networkObject.StateVersion=spawnData.StateVersion;
                try
                {
                    networkObjectRegistry.RegisterClientObject(networkObject,bunch.ObjectId);
                }
                catch
                {
                    Destroy(networkObject.gameObject);
                    throw;
                }
            }
            if (networkObject.ObjectType != spawnData.ObjectType)
            {
                EndClientConnection(connection,
                    $"对象 {bunch.ObjectId} 的类型不一致。",
                    notifyServer: true);
                return;
            }
            networkObject.OwnerConnectId=spawnData.OwnerConnectId;
            networkObject.OwnerVersion=spawnData.OwnerVersion;
            networkObject.IsPlayerObject=spawnData.IsPlayerObject;
            if (networkObject.OwnerConnectId != connection.connectionID)
                 networkObject.PrivateScore = 0;
            foreach(PropertyUpdateData item in spawnData.InitialProperties)
            {
                if(item==null)continue;
                if(!RepLayout.Instance.TryGetProperty(item.PropertyId,out RepProperty property))
                {
                    continue;
                }
                var decoded=new ObjectStateData();
                if (!property.TryReadUpdate(item, decoded))
                {
                    continue;
                }
                property.Apply(networkObject,decoded);
            }
            if (spawnData.HasInventory)
            {
                InventoryComponent inventory =networkObject.gameObject
                        .GetComponent<InventoryComponent>();
                if (inventory == null)
                {
                    inventory =networkObject.gameObject
                            .AddComponent<InventoryComponent>();
                }
                inventory.Owner = networkObject;
                inventory.SubobjectId =
                    spawnData.Inventory.SubobjectId;
                InventoryRepLayout.Apply(
                    inventory,
                    spawnData.Inventory);
                networkObject.Inventory = inventory;
            }
            channel.Bind(networkObject,connection.connectionID);
            if (networkObject.IsPlayerObject &&
                networkObject.OwnerConnectId != 0 &&
                networkObject.OwnerConnectId == connection.connectionID)
            {
                localPlayerObjectId = networkObject.NetObjectId;

                Debug.Log(
                    $"{name} 识别到自己的玩家对象：" +
                    $"ConnectionId={connection.connectionID}, " +
                    $"ObjectId={localPlayerObjectId}",
                    this);
            }
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
            uint objectId,
            bool destroyObject)
        {

            if (destroyObject && localPlayerObjectId == objectId)
            {
                localPlayerObjectId = 0;
            }
            // 只有永久销毁才加入黑名单。相关性关闭后，Actor 还可以重新 Open。
            if (destroyObject)
            {
                despawnedObjectIds.Add(objectId);
            }
            if (connection.ActorChannels.TryGetValue(
                    objectId,
                    out ActorChannel channel))
            {
                channel.Close();
                connection.ActorChannels.Remove(objectId);
                connection.Scheduler.RemoveObject(objectId);
            }
            if (networkObjectRegistry.TryGet(
                    objectId,
                    out NetworkObject networkObject))
            {
                networkObjectRegistry.Remove(objectId,permanently:destroyObject);
                if (networkObject != null)
                {
                    Destroy(networkObject.gameObject);
                }
            }
            Debug.Log(
                destroyObject
                    ? $"关闭通道并销毁对象：ObjectId={objectId}"
                    : $"关闭不相关 Actor 通道：ObjectId={objectId}",
                this);
        }
        private void ClearActorChannels(Connection connection)
        {
            if (!isServer)
            {
                localPlayerObjectId = 0;
            }
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
            connection.Scheduler.Clear();
            connection.FragmentReceiver.Clear();
            connection.FragmentSender.Clear();
            if(!isServer)
            {
                foreach (NetworkObject actor in networkObjectRegistry.GetAllObject())
                {
                    if (actor != null)
                        Destroy(actor.gameObject);
                }
                networkObjectRegistry.Clear();
            }
        }        
        private void SendClientRpc(Connection connection,uint objectId,
                            ushort rpcId,string argsJson,params uint[] objectReferenceIds)
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
                ArgsJson=argsJson??string.Empty,
                ObjectReferenceIds=objectReferenceIds==null?null:new List<uint>(objectReferenceIds)
            };
            var bunch=new NetBunch
            {
                ObjectId=objectId,
                Type=NetBunchType.Rpc,
                PayloadJson=JsonUtility.ToJson(call)
            };
            QueueReliableBunch(connection,bunch);
        }
        private void SendServerRpc(uint objectId,ushort rpcId,string argsJson,params uint[]objectReferenceIds)
        {
            Connection connection=serverConnection;
            if(isServer || connection == null ||
                connection.connectionState != ConnectionState.Connected ||
                !connection.ActorChannels.TryGetValue(
                    objectId, out ActorChannel channel) ||
                channel.State != ActorChannelState.Open ||
                channel.Target == null)
                {
                    return;
                }
            NetworkObject actor=channel.Target;
             if (actor.OwnerConnectId != connection.connectionID)
            {
                Debug.LogWarning("当前客户端不拥有这个对象。", this);
                return;
            }
            var call=new RpcCallData
            {
                RpcId=rpcId,
                Direction=RpcDirection.Server,
                OwnerVersion=actor.OwnerVersion,
                ArgsJson=argsJson??string.Empty,
                ObjectReferenceIds=objectReferenceIds==null?null:new List<uint>(objectReferenceIds)
            };
            QueueReliableBunch(connection, new NetBunch
            {
                ObjectId = objectId,
                Type = NetBunchType.Rpc,
                PayloadJson = JsonUtility.ToJson(call)
            });
        }
        private void SpawnPlayerForConnection(Connection owner)
        {
            if (!isServer ||
                transport == null ||
                owner == null ||
                owner.connectionState != ConnectionState.Connected)
            {
                return;
            }
             if (owner.PlayerObjectId != 0)
                return;
            if (!prefabRegistry.TryInstantiate(
                    playerObjectType,out NetworkObject player))
            {
                throw new InvalidOperationException(
                    $"无法生成玩家 Prefab：{playerObjectType}");
            }
            GameObject instance = player.gameObject;
            instance.name =$"ServerPlayerObject_{owner.connectionID}";
            player.Health = 100;
            player.PrivateScore=1000;
            // 注册服务器对象，分配网络对象编号。
            try
            {
                networkObjectRegistry.RegisterServerObject(player);
            }
            catch
            {
                Destroy(instance);
                throw;
            }
            // 指定这个对象由哪个客户端控制。
            player.OwnerConnectId = owner.connectionID;
            player.IsPlayerObject=true;
            InventoryComponent inventory =player.GetComponent<InventoryComponent>();
            if (inventory == null)
            {
                inventory =instance.AddComponent<InventoryComponent>();
            }
            inventory.Owner=player;
            inventory.SubobjectId=1;
            inventory.StateVersion=0;
            inventory.Capacity=20;
            inventory.ItemCount=3;
            player.Inventory=inventory;
            // 建立“连接 → 玩家对象”的关系。
            owner.PlayerObjectId = player.NetObjectId;
            // 创建服务器属性复制器。
            objectReplicators.Add(
                player.NetObjectId,
                new ObjectReplicator(player));
            // 所有客户端都需要看到这个新玩家。
            foreach (Connection recipient in connections.Values)
            {
                if (recipient.connectionState !=
                    ConnectionState.Connected)
                {
                    continue;
                }
                SendSpawnObject(recipient, player);
            }
            Debug.Log(
                $"自动生成玩家：" +
                $"ConnectionId={owner.connectionID}, " +
                $"ObjectId={player.NetObjectId}",
                player);
                }
        private void RemoveServerConnection(
            Connection connection,
            string reason)
        {
            if (!isServer || connection == null)
                return;
            uint playerObjectId = connection.PlayerObjectId;
            connection.connectionState =
                ConnectionState.DisConnect;
            connection.PlayerObjectId = 0;
            connection.Replication.Clear();
            connection.ReliableReceiver.Clear();
            // 服务器这里只清理这个连接的通道，
            // 不直接销毁服务器的玩家对象。
            ClearActorChannels(connection);
            connections.Remove(connection.remoteAddress);
            // 玩家销毁统一交给后续处理。
            if (playerObjectId != 0)
            {
               pendingPlayerDespawns.Add(playerObjectId);
            }
            Debug.Log(
                $"移除客户端：" +
                $"ConnectionId={connection.connectionID}, " +
                $"PlayerObjectId={playerObjectId}, " +
                $"Reason={reason}",
                this);
        }
        private void ProcessPendingPlayerDespawns()
        {
            if (pendingPlayerDespawns.Count == 0)
                return;
            // 遍历副本，允许处理过程中删除原集合里的编号。
            var objectIds =
                new List<uint>(pendingPlayerDespawns);
            foreach (uint objectId in objectIds)
            {
                // 对象可能已经通过其他入口销毁。
                if (!networkObjectRegistry.TryGet(
                        objectId,
                        out _))
                {
                    pendingPlayerDespawns.Remove(objectId);
                    continue;
                }
                // 复用现有逻辑：
                // 发 Close Bunch、清理复制器、删除服务器对象。
                if (DespawnServerObject(objectId))
                {
                    pendingPlayerDespawns.Remove(objectId);
                }
                // 失败时保留编号，下次再尝试。
            }
        }
        private void UpdatePendingRpcs()
        {
            if(isServer)return;
            Connection connection=serverConnection;
            if (connection == null || connection.connectionState != ConnectionState.Connected)
            {
                return;
            }
            double now=Now;
            var channels=new List<ActorChannel>(connection.ActorChannels.Values);
            foreach(ActorChannel channel in channels)
            {
                if(connection.connectionState!=ConnectionState.Connected)break;
                channel.ResolvePendingRpc(networkObjectRegistry,now);
            }
        }
        private List<PropertyUpdateData> BuildInitialProperties(Connection recipient,NetworkObject obj)
        {
            RepLayout layout=RepLayout.Instance;
            ObjectStateData snapshot=layout.Capture(obj);
            var result=new List<PropertyUpdateData>();
            foreach(RepProperty property in layout.Properties)
            {
                if (!property.ShouldSendTo(obj.OwnerConnectId, recipient.connectionID))
                {
                    continue;
                }
                result.Add(property.CreateUpdate(snapshot,obj.StateVersion));
            }
            return result;
        }
        private ObjectUpdateData FilterForConnection(
            Connection recipient,
            NetworkObject obj,
            ObjectUpdateData update)
        {
            var result = new ObjectUpdateData
            {
                ObjectId = update.ObjectId,
                OwnerVersion=update.OwnerVersion
            };
            foreach (PropertyUpdateData item in update.Properties)
            {
                if (item == null)
                    continue;
                if (!RepLayout.Instance.TryGetProperty(
                        item.PropertyId,
                        out RepProperty property))
                {
                    continue;
                }
                if (!property.ShouldSendTo(
                        obj.OwnerConnectId,
                        recipient.connectionID))
                {
                    continue;
                }
                result.Properties.Add(new PropertyUpdateData
                {
                    PropertyId = item.PropertyId,
                    PropertyVersion = item.PropertyVersion,
                    ValueJson = item.ValueJson
                });
            }

            return result;
        }
        private void HandleInventoryProperties(Connection connection,NetBunch bunch)
        {
            if(isServer)return ;
            InventoryStateData update;
            try
            {
                update=JsonUtility.FromJson<InventoryStateData>(bunch.PayloadJson);
            }
            catch
            {
                return;
            }
            if (!InventoryRepLayout.IsValid(update) || update.PlayerObjectId != bunch.ObjectId)
            {
                return;
            }
            if(!connection.ActorChannels.TryGetValue(bunch.ObjectId,out ActorChannel channel))
            {
                return;
            }
            channel.ReceiveInventoryProperties(update);
        }
        [ContextMenu("测试服务器增加背包物品")]
        public void TestAddInventoryItem()
{
    if (!isServer || transport == null)
    {
        Debug.LogWarning(
            "请在运行中的服务器上执行。",
            this);
        return;
    }

    if (!networkObjectRegistry.TryGet(
            testRpcObjectId,
            out NetworkObject player) ||
        player == null ||
        player.Inventory == null)
    {
        Debug.LogWarning(
            $"玩家或背包不存在：ObjectId={testRpcObjectId}",
            this);
        return;
    }

    InventoryComponent inventory = player.Inventory;

    if (inventory.ItemCount >= inventory.Capacity)
    {
        Debug.LogWarning("背包已满。", inventory);
        return;
    }

    if (!ServerSetInventoryItemCount(
        player.NetObjectId,
        inventory.ItemCount + 1))
{
    Debug.LogWarning("修改背包数量失败。", inventory);
    return;
}

    Debug.Log(
        $"服务器修改背包：" +
        $"Player={player.NetObjectId}, " +
        $"ItemCount={inventory.ItemCount}",
        inventory);
}
        private uint ServerCreateItem(uint ownerObjectId,int itemTypeId,int count)
        {
            if(!isServer)return 0;
            if(itemTypeId<=0||count<=0)return 0;
            if(!networkObjectRegistry.TryGet(ownerObjectId,out NetworkObject owner)
                    || owner == null || !owner.IsServerObject)
            {
                return 0;
            }
            var item=new ReplicatedItemData
            {
                SubobjectId=owner.AllocatedSubobjectId(),
                ItemTypeId=itemTypeId,
                Count=count
            };
            owner.ReplicatedItems.Add(item.SubobjectId,item);
            foreach(Connection connection in connections.Values)
            {
                SendItemCreate(connection,ownerObjectId,item);
            }
            return item.SubobjectId;
        }

        private void SendItemCreate(Connection connection, uint ownerObjectId, ReplicatedItemData item)
        {
            if(!connection.ActorChannels.TryGetValue(ownerObjectId,out ActorChannel channel))return;
            var bunch=new NetBunch
            {
                ObjectId=ownerObjectId,
                Type=NetBunchType.SubobjectCreate,
                PayloadJson=JsonUtility.ToJson(item)
            };
            channel.QueueReliableBunch(connection.connectionID,bunch);
            channel.RegisterItemForReplication(item);
        }
        public bool ServerDestroyItem(uint ownerObjectId,uint subobjectId)
        {
            if(!isServer)return false;
            if(!networkObjectRegistry.TryGet(ownerObjectId,out NetworkObject owner)
                        ||owner==null||!owner.IsServerObject)
            return false;
            if(!owner.ReplicatedItems.Remove(subobjectId))return false;
            string json=JsonUtility.ToJson(new SubobjectDestroyData{SubobjectId=subobjectId});
            foreach(Connection connection in connections.Values)
            {
                if(!connection.ActorChannels.TryGetValue(ownerObjectId,out ActorChannel channel))
                {
                    continue;
                }
                var bunch=new NetBunch
                {
                    ObjectId=ownerObjectId,
                    Type=NetBunchType.SubobjectDestroy,
                    PayloadJson=json
                };
                channel.QueueReliableBunch(connection.connectionID,bunch);
                channel.UnregisterItemForReplication(subobjectId);
            }
            return true;
        }
        public bool ServerSetItemCount(uint ownerObjectId,uint subobjectId,int count)
        {
            if(!isServer||count<=0)return false;
            if(!networkObjectRegistry.TryGet(ownerObjectId,out NetworkObject owner)||
                    owner==null||!owner.IsServerObject||
                    !owner.ReplicatedItems.TryGetValue(subobjectId,out ReplicatedItemData item))
            {
                return false;
            }
            if(item.Count==count)return true;
            item.Count=count;
            item.StateVersion=unchecked(item.StateVersion+1);
            foreach (Connection connection in connections.Values)
            {
                if (connection.ActorChannels.TryGetValue(
                        ownerObjectId,
                        out ActorChannel channel))
                {
                    channel.ForceNetUpdate();
                }
            }
            return true;
        }
        public bool IsActorRelevant(Connection connection,NetworkObject actor)
        {
            if(actor==null)return false;
            if (actor.OwnerConnectId == connection.connectionID)
            {
                return true;
            }
            if(!networkObjectRegistry.TryGet(connection.PlayerObjectId,out NetworkObject viewer)
                    || viewer == null)
            {
                return true;
            }
            return Vector3.SqrMagnitude(viewer.transform.position-
            actor.transform.position)<=RelevancyDistance*RelevancyDistance;
        }
        private void UpdateActorRelevancy(Connection connection)
        {
            foreach(NetworkObject actor in networkObjectRegistry.GetAllObject())
            {
                if(actor==null)continue;
                bool relevant=IsActorRelevant(connection,actor);
                bool hasChannel=connection.ActorChannels.TryGetValue(actor.NetObjectId,out ActorChannel channel);
                if (relevant)
                {
                    if(!hasChannel)SendSpawnObject(connection,actor);
                    continue;
                }
                if (hasChannel && channel.State == ActorChannelState.Open)
                {
                    NetBunch bunch=new NetBunch
                    {
                        ObjectId=actor.NetObjectId,
                        Type=NetBunchType.Close,
                        PayloadJson=string.Empty,
                        DestroyObject=false
                    };
                    connection.Replication.RemoveObject(actor.NetObjectId);
                    connection.FragmentSender.RemoveObject(actor.NetObjectId);
                    QueueReliableBunch(connection,bunch);
                    channel.BeginClose();
                }
            }
        }
        public bool ServerSetActorDormant(uint objectId,bool dormant)
        {
            if (!isServer)
                return false;
            if (!networkObjectRegistry.TryGet(objectId,
                    out NetworkObject actor) ||
                actor == null)
            {
                return false;
            }
            foreach(Connection connection in connections.Values)
            {
                if(!connection.ActorChannels.TryGetValue(objectId,out ActorChannel channel))
                continue;
                if(dormant)channel.SetDormant();
                else channel.Wake(Now);
            }
            return true;
        }
        private void UpdateActorDormancy(Connection connection,double now)
        {
            foreach(ActorChannel channel in connection.ActorChannels.Values)
            {
                bool hasPendingProperties=connection.Replication.HasPendingProperties(channel.ObjectId);
                if (channel.ShouldEnterDormancy(now, 2.0,hasPendingProperties))
                {
                    channel.SetDormant();
                    Debug.Log($"Actor 自动进入休眠：Conn={connection.connectionID}, " +
                $"Object={channel.ObjectId}",
                this);
                }
            }           
        }
        public bool ServerForceNetUpdate(uint objectId)
        {
             if (!isServer || objectId == 0)
                return false;
            if (!networkObjectRegistry.TryGet(
                    objectId,
                    out NetworkObject actor) ||
                actor == null ||
                !actor.IsServerObject)
            {
                return false;
            }
            foreach (Connection connection in connections.Values)
            {
                if (connection.connectionState !=
                    ConnectionState.Connected)
                {
                    continue;
                }

                if (!connection.ActorChannels.TryGetValue(
                        objectId,
                        out ActorChannel channel) ||
                    channel == null ||
                    channel.State != ActorChannelState.Open)
                {
                    continue;
                }
                channel.ForceNetUpdate();
            }
            return true;
        }
        public bool ServerSetHealth(uint objectId, int health)
        {
            if (!isServer || objectId == 0 || health < 0)
                return false;
            if (!networkObjectRegistry.TryGet(
                    objectId,
                    out NetworkObject actor) ||
                actor == null ||
                !actor.IsServerObject)
            {
                return false;
            }
            // 数值相同，不需要唤醒。
            if (actor.Health == health)
                return true;
            actor.Health = health;
            return ServerForceNetUpdate(objectId);
        }
        public bool ServerSetInventoryItemCount(
            uint objectId,
            int itemCount)
        {
            if (!isServer || objectId == 0 || itemCount < 0)
                return false;
            if (!networkObjectRegistry.TryGet(
                    objectId,
                    out NetworkObject actor) ||
                actor == null ||
                !actor.IsServerObject ||
                actor.Inventory == null)
            {
                return false;
            }
            InventoryComponent inventory = actor.Inventory;
            if (inventory.Owner != actor ||
                itemCount > inventory.Capacity)
            {
                return false;
            }
            // 数量相同，不修改，也不唤醒。
            if (inventory.ItemCount == itemCount)
                return true;
            inventory.ItemCount = itemCount;
            return ServerForceNetUpdate(objectId);
        }
        public bool ServerSetTarget(
            uint objectId,
            uint targetObjectId)
        {
            if (!isServer || objectId == 0)
                return false;
            if (!networkObjectRegistry.TryGet(
                    objectId,
                    out NetworkObject actor) ||
                actor == null ||
                !actor.IsServerObject)
            {
                return false;
            }
            NetworkObject target = null;
            // 0 表示清空目标；非零编号必须对应服务器对象。
            if (targetObjectId != 0)
            {
                if (!networkObjectRegistry.TryGet(
                        targetObjectId,
                        out target) ||
                    target == null ||
                    !target.IsServerObject)
                {
                    return false;
                }
            }
            if (actor.TargetObjectId == targetObjectId)
                return true;
            // 通过已有 setter 维护注册表中的引用关系。
            actor.Target = target;
            return ServerForceNetUpdate(objectId);
        }
        public int SendPropertyBatch(Connection connection,
                List<ReplicationScheduler.Candidate>batch)
        {
            var messages=new List<byte[]>(batch.Count);
            var updates = new List<ObjectUpdateData>(batch.Count);
            foreach (ReplicationScheduler.Candidate candidate in batch)
            {
                messages.Add(candidate.Payload);
                updates.Add(candidate.Update);
            }
            byte[]payload=PacketPayloadCodec.Encode(messages);
             uint packetSequence = SendPayload(
                connection,
                payload,
                0);
            double sentTime = Now;
            // 同一个包，记录它携带的全部 Actor 属性更新。
            connection.Replication.RecordSent(
                packetSequence,
                updates,
                sentTime);
            foreach (ReplicationScheduler.Candidate candidate in batch)
            {
                connection.Scheduler.RecordSent(
                    candidate.Update.ObjectId,
                    sentTime);
            }
            return NetPacketCodec.HeaderSize + payload.Length;
        }
        public bool TryDecodePacketMessage(byte[]payload,out List<IncomingMessage>incoming)
        {
            incoming=null;
            bool isBatch=PacketPayloadCodec.IsBatch(payload);
            List<byte[]>parts;
            if (isBatch)
            {
                if(isServer||!PacketPayloadCodec.TryDecode(payload,out parts))
                {
                    return false;
                }
            }
            else
            {
                parts=new List<byte[]>{payload};
            }
            var decoded=new List<IncomingMessage>(parts.Count);
            foreach(byte[] part in parts)
            {
                if (!TryDecodeIncomingMessage(part,
                        out IncomingMessage item))
                {
                    return false;
                }
                if (isBatch)
                {
                    if(item.Type!=NetMessageType.ActorBunch||item.Reliable||
                            item.ConnectionId==0)
                    return false;
                    if(decoded.Count>0&&item.ConnectionId!=decoded[0].ConnectionId)
                    return false;
                    try
                    {
                        NetBunch bunch=JsonUtility.FromJson<NetBunch>(item.Data);
                        if (bunch == null ||
                            bunch.ObjectId == 0 ||
                            bunch.Type != NetBunchType.Properties)
                        {
                            return false;
                        }
                        ObjectUpdateData update =
                            JsonUtility.FromJson<ObjectUpdateData>(
                                bunch.PayloadJson);
                         if (update == null ||
                            update.ObjectId != bunch.ObjectId ||
                            update.Properties == null)
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }
                decoded.Add(item);
            }
            incoming=decoded;
            return true;
        }
        public void HandleFragmentPacket(string remote,NetPacket packet)
        {
            if(!MatchesSession(remote,packet))
            return ;
            if (isServer ||!FragmentCodec.TryDecode(packet.Payload,
                    out FragmentData fragment))
            {
                return;
            }
            Connection connection = FindPacketConnection(remote,
                    NetMessageType.ActorBunch,fragment.ConnectionId);
            if (connection == null)
                return;
            double now = Now;
            ProcessPacketAcknowledgements(
                connection,
                packet,
                now);
            connection.Packets.RecordReceived(packet.Sequence);
            FragmentReceiveResult result =
                connection.FragmentReceiver.Add(
                    fragment,
                    connection.connectionID,
                    now,
                    out byte[] message);
            if(result==FragmentReceiveResult.Rejected)return;
             connection.lastReceiveTime = now;
            if (result == FragmentReceiveResult.AlreadyCompleted)
            {
                // 上次的整组确认可能丢了，再确认一次。
                SendFragmentAck(connection, fragment.GroupId);
                return;
            }
            if (result != FragmentReceiveResult.Ready)
                return;
            if (!TryDecodeFragmentProperties(
                    message,
                    connection.connectionID,
                    out List<IncomingMessage> incoming))
            {
                return;
            }
            foreach (IncomingMessage item in incoming)
            {
                DispatchMessage(
                    remote,
                    item.Type,
                    item.ConnectionId,
                    item.Data);
                if (connection.connectionState !=
                    ConnectionState.Connected)
                {
                    return;
                }
            }
             if (connection.FragmentReceiver.MarkCompleted(
                    fragment.GroupId,
                    Now))
            {
                SendFragmentAck(connection, fragment.GroupId);
            }
        }
        private bool TryDecodeFragmentProperties(byte[]payload,uint connectionId,out List<IncomingMessage> incoming)
        {
            if (!TryDecodePacketMessage(payload, out incoming))
                return false;
            try
            {
                foreach(IncomingMessage item in incoming)
                {
                    if (item.Type != NetMessageType.ActorBunch ||
                        item.Reliable ||
                        item.ConnectionId != connectionId)
                    {
                        return false;
                    }
                    NetBunch bunch=JsonUtility.FromJson<NetBunch>(item.Data);
                    if (bunch == null ||
                        bunch.ObjectId == 0 ||
                        bunch.Type != NetBunchType.Properties)
                    {
                        return false;
                    }
                    ObjectUpdateData update =JsonUtility.FromJson<ObjectUpdateData>(
                            bunch.PayloadJson);
                     if (update == null ||
                        update.ObjectId != bunch.ObjectId ||
                        update.Properties == null ||
                        update.Properties.Count == 0)
                    {
                        return false;
                    }
                     var state = new ObjectStateData();
                    foreach (PropertyUpdateData property in update.Properties)
                    {
                        if (property == null ||
                            !RepLayout.Instance.TryGetProperty(
                                property.PropertyId,
                                out RepProperty definition) ||
                            !definition.TryReadUpdate(property, state))
                        {
                            return false;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
            return true;
        }
        private void SendFragmentAck(Connection connection,uint groupId)
        {
            if (transport == null ||
                connection.connectionState !=
                    ConnectionState.Connected)
            {
                return;
            }
            var ack = new FragmentAckData
            {
                groupId = groupId
            };
            byte[] payload = NetMessage.Encode(
                NetMessageType.FragmentAck,
                connection.connectionID,
                0,
                false,
                JsonUtility.ToJson(ack));
            SendPayload(connection, payload, 0);
        }
        private void HandleFragmentAck(Connection connection,string data)
        {
            FragmentAckData ack;
            try
            {
                ack = JsonUtility.FromJson<FragmentAckData>(data);
            }
            catch (ArgumentException)
            {
                return;
            }
            if (ack == null || ack.groupId == 0
                    ||!connection.FragmentSender.RemoveGroup(ack.groupId))
                return;
            int confirmed =
                connection.Replication.AcknowledgeFragmentGroup(ack.groupId);
            if (confirmed > 0)
            {
                Debug.Log(
                    $"分片组 {ack.groupId} 确认了 " +
                    $"{confirmed} 个属性",
                    this);
            }
        }
        private void ProcessPacketAcknowledgements(Connection connection,NetPacket packet,double now)
        {
            connection.Packets.ProcessAck(packet,now,acknowledgedSequences);
            foreach(uint sequence in acknowledgedSequences)
            {
                connection.ReliableMessageQueue.AcknowledgePacket(sequence);
                foreach(ActorChannel channel in connection.ActorChannels.Values)
                {
                    channel.ReliableSender.AcknowledgePacket(sequence);
                }
                int confirmed=connection.Replication.AcknowledgePacket(sequence);
                if (confirmed > 0)
                {
                    Debug.Log($"包 {sequence} 确认了 {confirmed} 个属性",this);
                }
            }

        }
        private void PruneFragmentGroups(Connection connection, int packetLimit)
        {
            foreach(FragmentSendQueue.Group group in 
                            connection.FragmentSender.GetGroups())
            {
                bool validChannel=connection.ActorChannels.TryGetValue(group.ObjectId,
                                out ActorChannel channel)&&channel!=null&&
                                channel.State==ActorChannelState.Open&&
                                channel.Target!=null&&channel.Target.IsServerObject;
                bool fits =NetPacketCodec.HeaderSize +
                        group.Fragments[0].Length <= packetLimit;
                if (!validChannel || !fits)
                {
                    connection.FragmentSender.RemoveGroup(group.GroupId);
                    connection.Replication.ForgetFragmentGroup(group.GroupId);
                }
            }
        }
        private int SendFragmentPackets(Connection connection,int budget,double now)
        {
            if(budget<=0)return 0;
            int remaining=budget;
            List<FragmentSendQueue.Group>ready=connection.FragmentSender
                            .GetReadyGroups(now,RetryInterval);
            bool progressed;
            do
            {
                progressed=false;
                foreach (FragmentSendQueue.Group group in ready)
                {
                    if (group.NextFragmentIndex >= group.Fragments.Count)
                    {
                        continue;
                    }
                    byte[] fragment=group.Fragments[group.NextFragmentIndex];
                    int packetBytes=NetPacketCodec.HeaderSize+fragment.Length;
                    if(packetBytes>remaining)continue;
                    SendPayload(connection,fragment,0);
                    double sentTime=Now;
                    connection.FragmentSender.RecordSent(group.GroupId,sentTime);
                    connection.Scheduler.RecordSent(group.ObjectId,sentTime);
                    remaining-=packetBytes;
                    progressed=true;
                }
            }
            while(progressed&&remaining>0);
            return budget-remaining;
        }
        [ContextMenu("测试分片重组")]
        private void TestFragmentReceiveBuffer()
        {
            var original = new byte[3000];
            for (int i = 0; i < original.Length; i++)
            {
                original[i] = (byte)(i % 256);
            }
            List<byte[]> fragments =
                FragmentCodec.Split(1, 100, original);
            var receiver = new FragmentReceiveBuffer();
            int[] order = { 2, 0, 0, 1 };
            double now = Now;
            foreach (int index in order)
            {
                if (!FragmentCodec.TryDecode(
                        fragments[index],
                        out FragmentData fragment))
                {
                    throw new InvalidOperationException("分片解析失败");
                }

                FragmentReceiveResult result = receiver.Add(
                    fragment,
                    1,
                    now,
                    out byte[] message);

                Debug.Log($"收到第 {index} 片：{result}", this);

                if (result != FragmentReceiveResult.Ready)
                    continue;

                if (message.Length != original.Length)
                {
                    throw new InvalidOperationException("重组长度错误");
                }

                for (int i = 0; i < original.Length; i++)
                {
                    if (message[i] != original[i])
                    {
                        throw new InvalidOperationException(
                            $"重组内容错误，位置：{i}");
                    }
                }

                if (!receiver.MarkCompleted(fragment.GroupId, now))
                {
                    throw new InvalidOperationException("记录完成状态失败");
                }

                Debug.Log("重组成功，3000 字节全部一致", this);
            }

            // 完成后再次收到某个分片，应识别为已经处理过。
            FragmentCodec.TryDecode(
                fragments[0],
                out FragmentData duplicate);

            FragmentReceiveResult duplicateResult = receiver.Add(
                duplicate,
                1,
                now,
                out byte[] ignored);

            Debug.Log($"完成后收到重传：{duplicateResult}", this);
        }
        [ContextMenu("测试分片编解码")]
        private void TestFragmentCodec()
{
    byte[] message = new byte[3000];

    List<byte[]> pieces =
        FragmentCodec.Split(1, 1, message);

    foreach (byte[] piece in pieces)
    {
        if (!FragmentCodec.TryDecode(
                piece,
                out FragmentData fragment))
        {
            Debug.LogError("分片解析失败。", this);
            return;
        }

        Debug.Log(
            $"片序号={fragment.FragmentIndex}, " +
            $"总片数={fragment.FragmentCount}, " +
            $"本片内容={fragment.PayLoad.Length}, " +
            $"完整消息={fragment.TotalLength}, " +
            $"加外层包头后={NetPacketCodec.HeaderSize + piece.Length}",
            this);
    }
}
        [ContextMenu("打印连接同步状态")]
        private void PrintReplicationStats()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("请在运行时查看同步状态。", this);
                return;
            }

            if (isServer)
            {
                Debug.Log(
                    $"服务器当前连接数：{connections.Count}",
                    this);

                foreach (Connection connection in connections.Values)
                {
                    PrintConnectionStats(connection);
                }

                return;
            }

            if (serverConnection == null)
            {
                Debug.Log("客户端尚未创建服务器连接。", this);
                return;
            }
            PrintConnectionStats(serverConnection);
        }
        private void PrintConnectionStats(Connection connection)
        {
            if (connection == null)
                return;

            int actorReliableMessages = 0;
            int openChannels = 0;
            int dormantChannels = 0;

            foreach (ActorChannel channel
                    in connection.ActorChannels.Values)
            {
                if (channel == null)
                    continue;

                // 累加每个 Actor 通道中尚未确认的可靠消息。
                actorReliableMessages +=
                    channel.ReliableSender.PendingCount;

                if (channel.State == ActorChannelState.Open)
                {
                    openChannels++;

                    if (channel.Isdormant)
                        dormantChannels++;
                }
            }

            string ackDelay =
                connection.Packets.AcknowledgedCount == 0
                    ? "暂无"
                    : $"{connection.Packets.LastAckDelaySeconds * 1000.0:F1} ms";

            Debug.Log(
                $"连接编号={connection.connectionID}\n" +
                $"远端地址={connection.remoteAddress}\n" +
                $"连接状态={connection.connectionState}\n" +
                $"通道总数={connection.ActorChannels.Count}，" +
                $"已打开={openChannels}，" +
                $"其中休眠={dormantChannels}\n" +

                $"连接级待确认可靠消息=" +
                $"{connection.ReliableMessageQueue.PendingCount}\n" +

                $"Actor 通道待确认可靠消息=" +
                $"{actorReliableMessages}\n" +

                $"待确认属性条目=" +
                $"{connection.Replication.PendingPropertyCount}\n" +

                $"发送端未完成分片组=" +
                $"{connection.FragmentSender.PendingGroupCount}，" +
                $"队列占用={connection.FragmentSender.QueuedBytes} B\n" +

                $"接收端未完成分片组=" +
                $"{connection.FragmentReceiver.PendingGroupCount}，" +
                $"预留={connection.FragmentReceiver.ReservedBytes} B\n" +

                $"跟踪中的未确认包=" +
                $"{connection.Packets.PendingCount}\n" +

                $"累计已确认包=" +
                $"{connection.Packets.AcknowledgedCount}\n" +

                $"最近一次包确认耗时={ackDelay}",
                this);
        }
        [ContextMenu("测试位置量化")]
        private void TestPositionQuantization()
        {
            float original = 12.3456f;

            int encoded =
                PositionQuantization.Encode(original);

            float decoded =
                PositionQuantization.Decode(encoded);

            if (encoded != 1235 ||
                Mathf.Abs(decoded - 12.35f) > 0.00001f ||
                PositionQuantization.Encode(-12.3456f) != -1235)
            {
                throw new InvalidOperationException(
                    "坐标量化结果错误。");
            }

            Vector3 a = new Vector3(1.001f, 0f, 0f);
            Vector3 b = new Vector3(1.004f, 0f, 0f);
            Vector3 c = new Vector3(1.006f, 0f, 0f);

            if (!PositionQuantization.SamePosition(a, b) ||
                PositionQuantization.SamePosition(a, c))
            {
                throw new InvalidOperationException(
                    "量化位置比较错误。");
            }

            Debug.Log(
                $"位置量化测试通过：" +
                $"{original:F4} → {encoded} → {decoded:F2}",
                this);
        }
        [ContextMenu("打印网络模拟统计")]
        private void PrintSimulationStats()
        {
            if (simulatedTransport == null)
            {
                Debug.Log("当前未启用网络模拟。", this);
                return;
            }
            Debug.Log(
                $"发送请求={simulatedTransport.RequestedPackets}, " +
                $"实际发包={simulatedTransport.SentPackets}, " +
                $"实际发送字节={simulatedTransport.SentBytes}, " +
                $"随机丢弃={simulatedTransport.RandomDroppedPackets}, " +
                $"队列满丢弃={simulatedTransport.QueueDroppedPackets}, " +
                $"额外排队副本={simulatedTransport.QueuedDuplicates}, " +
                $"等待包数={simulatedTransport.PendingPackets}, " +
                $"等待字节={simulatedTransport.PendingBytes}",
                this);
        }
        [ContextMenu("测试二进制属性消息")]
        private void TestPropertyMessageCodec()
        {
            var state = new ObjectStateData
            {
                Health = 80,
                Position = new Vector3(
                1.2345f,
                -2.3456f,
                3.4567f),
                TargetObjectId = 9,
                PrivateScore = -50,
                Rotation = Quaternion.Euler(10f, 35f, 5f),
                Velocity = new Vector3(1.5f, 0f, -2f),
                MovementServerTime=12.5
            };

            var update = new ObjectUpdateData
            {
                ObjectId = 7
            };

            foreach (RepProperty property
                    in RepLayout.Instance.Properties)
            {
                update.Properties.Add(
                    property.CreateUpdate(state, 10));
            }

            byte[] bytes = PropertyMessageCodec.Encode(2, update);

            if (!PropertyMessageCodec.TryDecode(
                    bytes,
                    out uint connectionId,
                    out ObjectUpdateData decoded) ||
                connectionId != 2 ||
                decoded.ObjectId != 7 ||
                decoded.Properties.Count != 4)
            {
                throw new InvalidOperationException(
                    "二进制属性消息解析失败。");
            }
            var restored = new ObjectStateData();
            foreach (PropertyUpdateData item in decoded.Properties)
            {
                if (item.PropertyVersion != 10 ||
                    !RepLayout.Instance.TryGetProperty(
                        item.PropertyId,
                        out RepProperty property) ||
                    !property.TryReadUpdate(item, restored))
                {
                    throw new InvalidOperationException(
                        "属性值或版本解析失败。");
                }
            }
            if (restored.MovementServerTime !=
                    state.MovementServerTime ||
                restored.Health != state.Health ||
                !PositionQuantization.SamePosition(
                    restored.Position,
                    state.Position) ||
                restored.TargetObjectId != state.TargetObjectId ||
                restored.PrivateScore != state.PrivateScore ||
                Quaternion.Angle(
                    restored.Rotation,
                    state.Rotation) > 0.1f ||
                (restored.Velocity - state.Velocity).sqrMagnitude
                    > 0.000001f)
            {
                throw new InvalidOperationException(
                    "属性往返后内容不一致。");
            }
            Debug.Log(
                $"二进制属性测试通过，消息长度={bytes.Length} 字节。",
                this);
        }
       [ContextMenu("测试变长整数")]
    private void TestPackedIntegerCodec()
{
    int[] values =
    {
        0,
        1,
        -1,
        63,
        -64,
        64,
        -65,
        1000,
        -1000,
        int.MinValue,
        int.MaxValue
    };

    foreach (int value in values)
    {
        byte[] bytes;

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            PackedIntegerCodec.WriteInt32(writer, value);

            writer.Flush();
            bytes = stream.ToArray();
        }

        using (var stream = new MemoryStream(bytes, false))
        using (var reader = new BinaryReader(stream))
        {
            int restored =
                PackedIntegerCodec.ReadInt32(reader);

            if (restored != value ||
                stream.Position != stream.Length)
            {
                throw new InvalidOperationException(
                    $"变长整数往返失败：{value}");
            }
        }

        Debug.Log(
            $"整数={value}，编码长度={bytes.Length} 字节。",
            this);
    }

    Debug.Log("变长整数测试全部通过。", this);
}
        private bool TryDecodeIncomingMessage(byte[]bytes,out IncomingMessage item)
        {
            item = null;
            if (PropertyMessageCodec.IsPropertyMessage(bytes))
            {
                if (isServer ||
                    !PropertyMessageCodec.TryDecode(
                        bytes,out uint connectionId,
                        out ObjectUpdateData update))
                {
                    return false;
                }
                var bunch = new NetBunch
                {
                    ObjectId = update.ObjectId,
                    Type = NetBunchType.Properties,
                    PayloadJson = JsonUtility.ToJson(update)
                };
                item = new IncomingMessage
                {
                    Type = NetMessageType.ActorBunch,
                    ConnectionId = connectionId,
                    MessageId = 0,
                    Reliable = false,
                    Data = JsonUtility.ToJson(bunch)
                };
                return true;
            }
            var legacy = new IncomingMessage();
            if (!NetMessage.TryDecode(bytes,out legacy.Type,
                    out legacy.ConnectionId,out legacy.MessageId,
                    out legacy.Reliable,out legacy.Data))
            {
                return false;
            }
            item = legacy;
            return true;
        }
        private void SampleServerMovements(double now)
        {
            if(!isServer)return;
            foreach(NetworkObject actor in networkObjectRegistry.GetAllObject())
            {
                if (actor != null && actor.IsServerObject)
                {
                    actor.SampleServerMovement(now);
                }
            }
        }
        private bool CheckHandshake(string remote,NetPacket packet,
                IncomingMessage message,out NetHandshakeData handshake)
        {
            handshake=null;
            bool request=message.Type==NetMessageType.ConnectRequest;
            bool accept=message.Type==NetMessageType.ConnectAccept;
            bool reject =message.Type == NetMessageType.ConnectReject;
            if (!request && !accept && !reject)
                return MatchesSession(remote,packet);
            if (!NetProtocol.TryRead(message.Data,
                    out handshake) ||handshake.ClientNonce == 0)
            {
                return false;
            }
            if (isServer)
            {
                if (!request||packet.SessionId != 0 ||
                    packet.HasAck ||handshake.SessionId != 0)
                    return false;
                if(!NetProtocol.TryValidate(message.Data,out string reason))
                {
                    SendConnectionRejected(remote,handshake.ClientNonce,reason);
                    return false;
                }
                if(connections.TryGetValue(remote,out Connection existing)&&
                                    existing.ClientNonce != handshake.ClientNonce)
                {
                    SendConnectionRejected(remote,handshake.ClientNonce,
                        "该地址的旧连接尚未结束，请断开或等待超时后重试。");
                    return false;
                }
                return true;
            }
            Connection connection = serverConnection;
            if (connection == null ||
                connection.connectionState ==ConnectionState.DisConnect ||
                remote != connection.remoteAddress||
                handshake.ClientNonce!=connection.ClientNonce)
            {
                return false;
            }
            if (reject)
            {
                if (packet.SessionId != 0 ||packet.HasAck ||
                    handshake.SessionId != 0)
                {
                    return false;
                }
                 if (connection.connectionState ==ConnectionState.Connecting)
                {
                    FailHandshake(connection,
                        string.IsNullOrEmpty(handshake.Reason)
                        ? "服务器拒绝连接。"
                        : handshake.Reason);
                }
                return false;
            }
            if(!accept||message.ConnectionId == 0 ||
                handshake.SessionId == 0 ||packet.SessionId != handshake.SessionId)
                return false;
            if(!NetProtocol.TryValidate(message.Data,out string error))
            {
                if (connection.connectionState == ConnectionState.Connecting)
                {
                    FailHandshake(connection,error);
                }
                return false;
            }
            if (connection.connectionState ==ConnectionState.Connected)
            {
                return connection.connectionID ==message.ConnectionId &&
                    connection.SessionId ==handshake.SessionId;
            }
            connection.SessionId = handshake.SessionId;
            return true;
        }
        private void FailHandshake(Connection connection,string reason)
        {
            EndClientConnection(connection,$"握手失败：{reason}");
        }
        private void SendConnectionRejected(string remote,ulong clientNoce,string reason)
        {
            byte[]payload=NetMessage.Encode(
                NetMessageType.ConnectReject,
                0,
                0,
                false,
                NetProtocol.CreateHandshakeJson(clientNoce,0,reason)
            );
            var packet=new NetPacket(0,false,0,0,0,payload);
            transport.Send(remote,NetPacketCodec.Encode(packet));
        }
        private bool MatchesSession(string remote,NetPacket packet)
        {
            Connection connection;
            if (isServer)
            {
                if (!connections.TryGetValue(remote,out connection))
                {
                    return false;
                }
            }
            else
            {
                connection=serverConnection;
            }
            return connection != null &&
                connection.remoteAddress == remote &&
                connection.connectionState ==ConnectionState.Connected &&
                connection.SessionId != 0 &&packet.SessionId == connection.SessionId;
        }
        private void CreateTransport()
        {
            if (prefabRegistry == null)
            {
                throw new InvalidOperationException(
                    "请给 NetworkDriver 设置 PrefabRegistry。");
            }
            prefabRegistry.Initialize();
            if (isServer &&!prefabRegistry.TryGetPrefab
                                (playerObjectType,out _))
            {
                throw new InvalidOperationException(
                    $"玩家类型未登记：{playerObjectType}");
            }
            if (transport != null)
                return;
            ITransport udp =new UdpTransport(isServer ? serverPort : 0);
            try
            {
                var simulation = new SimulatedTransport(
                    udp,
                    () => Now,
                    simulatedLoss,
                    simulatedDuplicate,
                    simulatedLatencyMs / 1000.0,
                    simulatedJitterMs / 1000.0,
                    unchecked(simulationSeed + (isServer ? 0 : 1)))
                {
                    Enabled = simulateNetwork
                };
                simulatedTransport = simulation;
                transport = simulation;
            }
            catch
            {
                udp.Dispose();
                throw;
            }
        }
        private void BeginClientConnection()
        {
            double now = Now;
            despawnedObjectIds.Clear();
            acknowledgedSequences.Clear();
            localPlayerObjectId = 0;
            serverConnection = new Connection(serverAddress, now)
            {
                ClientNonce = NetProtocol.CreateToken()
            };
            nextConnectionRequestTime = now;
        }
        private void EndClientConnection(Connection connection,string reason,bool notifyServer=false)
        {
            if (isServer ||connection == null ||connection != serverConnection)
            {
                return;
            }
            try
            {
            simulatedTransport?.DiscardPending();
            if (notifyServer &&transport != null &&
                    connection.connectionState ==ConnectionState.Connected)
                {
                    Send(connection,NetMessageType.DisConnect);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"结束连接时发送通知失败：{exception.Message}",
                    this);
            }
            finally
            {
                connection.connectionState=ConnectionState.DisConnect;
                ClearActorChannels(connection);
                connection.Replication.Clear();
                connection.ReliableReceiver.Clear();
                despawnedObjectIds.Clear();
                acknowledgedSequences.Clear();
                localPlayerObjectId=0;
                nextConnectionRequestTime=double.PositiveInfinity;
                serverConnection=null;
                Debug.Log($"客户端连接已结束{reason}",this);
            }
        }
        [ContextMenu("测试按类型生成网络对象")]
        private void SpawnTypedTestObject()
        {
             if (!Application.isPlaying ||
                    !isServer ||
                    transport == null)
                {
                    return;
                }
            if (!prefabRegistry.TryInstantiate(
                    testSpawnObjectType,
                    out NetworkObject actor))
            {
                Debug.LogWarning(
                    $"测试类型未登记：{testSpawnObjectType}",
                    this);
                return;
            }
            actor.transform.position = testSpawnPosition;
            try
            {
                networkObjectRegistry.RegisterServerObject(actor);
            }
            catch
            {
                Destroy(actor.gameObject);
                throw;
            }
            InventoryComponent inventory =actor.GetComponent<InventoryComponent>();
            if (inventory != null)
            {
                inventory.Owner = actor;
                inventory.SubobjectId = 1;
                actor.Inventory = inventory;
            }
            objectReplicators.Add(actor.NetObjectId,new ObjectReplicator(actor));
             foreach (Connection recipient in connections.Values)
            {
                if (recipient.connectionState ==ConnectionState.Connected &&
                    IsActorRelevant(recipient, actor))
                {
                    SendSpawnObject(recipient, actor);
                }
            }
        }
        private Connection FindConnectedOwner(uint connectionId)
        {
            foreach (Connection connection in connections.Values)
            {
                if (connection.connectionID == connectionId &&
                    connection.connectionState == ConnectionState.Connected)
                {
                    return connection;
                }
            }
            return null;
        }
        public bool ServerSetObjectOwner(uint objectId,uint newOwnerConnectionId)
        {
             if (!isServer || transport == null ||
                !networkObjectRegistry.TryGet(
                    objectId, out NetworkObject actor) ||
                actor == null || !actor.IsServerObject ||
                actor.IsPlayerObject)
            {
                return false;
            }
             if (newOwnerConnectionId != 0 &&
                FindConnectedOwner(newOwnerConnectionId) == null)
            {
                return false;
            }
            if (actor.OwnerConnectId == newOwnerConnectionId)
                return true;
            if (actor.OwnerVersion == uint.MaxValue)
                return false;
             foreach (Connection recipient in connections.Values)
            {
                if (recipient.connectionState == ConnectionState.Connected &&
                    recipient.ActorChannels.TryGetValue(
                        objectId, out ActorChannel channel) &&
                    channel.State == ActorChannelState.Open &&
                    channel.ReliableSender.PendingCount >= 256)
                {
                    return false;
                }
            }
            uint previousOwner=actor.OwnerConnectId;
            actor.OwnerConnectId=newOwnerConnectionId;
            actor.OwnerVersion++;
            double now=Now;
            foreach(Connection recipient in connections.Values)
            {
                if(recipient.connectionState!=ConnectionState.Connected)
                    continue;
                recipient.Replication.RemoveObject(objectId);
                recipient.FragmentSender.RemoveObject(objectId);
                recipient.Scheduler.RemoveObject(objectId);
                if (!recipient.ActorChannels.TryGetValue(
                        objectId, out ActorChannel channel))
                {
                    if (IsActorRelevant(recipient, actor))
                        SendSpawnObject(recipient, actor);
                    continue;
                }
                if (channel.State != ActorChannelState.Open)
                    continue;
                ObjectUpdateData full =channel.CaptureOwnerChangeState(now);
                var change = new OwnerChangeData
                {
                    OwnerConnectId = newOwnerConnectionId,
                    State = FilterForConnection(recipient, actor, full)
                };
                QueueReliableBunch(recipient, new NetBunch
                {
                    ObjectId = objectId,
                    Type = NetBunchType.OwnerChanged,
                    PayloadJson = JsonUtility.ToJson(change)
                });
                channel.ForceNetUpdate();
            }
            Debug.Log(
                $"对象 {objectId} 所有者：{previousOwner} → " +
                $"{newOwnerConnectionId}，归属版本={actor.OwnerVersion}",
                this);
            return true;
        }
        private void ReleaseDisconnectedObjectOwners()
        {
             foreach (NetworkObject actor
                     in networkObjectRegistry.GetAllObject())
            {
                if (actor != null && !actor.IsPlayerObject &&
                    actor.OwnerConnectId != 0 &&
                    FindConnectedOwner(actor.OwnerConnectId) == null)
                {
                    ServerSetObjectOwner(actor.NetObjectId, 0);
                }
            }
        }
        private void HandleOwnerChanged(Connection connection,NetBunch bunch)
        {
            if(isServer)return;
            OwnerChangeData data;
            try
            {
                data=JsonUtility.FromJson<OwnerChangeData>(bunch.PayloadJson);
            }
            catch
            {
                return;
            }
            if(connection.ActorChannels.TryGetValue(bunch.ObjectId,out ActorChannel channel))
            {
                channel.ReceiveOwnerChange(data);
            }
        }
        [SerializeField] private uint testOwnerObjectId;
        [SerializeField] private uint testNewOwnerConnectionId;

        [ContextMenu("测试修改普通对象所有者")]
        private void TestChangeObjectOwner()
        {
            if (!Application.isPlaying || !isServer)
                return;
            if (!ServerSetObjectOwner(
                    testOwnerObjectId, testNewOwnerConnectionId))
            {
                Debug.LogWarning(
                    "修改失败：检查对象、连接、玩家对象限制或可靠队列。",
                    this);
            }
        }
        [ContextMenu("测试通过拥有的对象发送服务器 RPC")]
        private void TestOwnedObjectRpc()
        {
            if (!Application.isPlaying || isServer)
                return;
            SendServerRpc(
                testOwnerObjectId,
                101,
                JsonUtility.ToJson(new AttackRpcArgs
                {
                    TargetObjectId = testAttackTargetObject
                }),
                testAttackTargetObject);
        }
    }

}
