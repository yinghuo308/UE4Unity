using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public enum ConnectionState
    {
        Connecting,
        Connected,
        DisConnect
    }
    public class Connection 
    {
        public uint connectionID{get; internal set;}
        public string remoteAddress{get;}
        public ConnectionState connectionState{get;internal set;}
        public double lastReceiveTime{get;internal set;}
        public double lastSendTime{get;internal set;}
        public PacketState Packets{get;}=new PacketState();
        public ReliableMessageQueue ReliableMessageQueue{get;}=new ReliableMessageQueue();
        public ConnectionReplicationState Replication{get;}=new ConnectionReplicationState();
        public ReliableReceiveBuffer ReliableReceiver{get;}=new ReliableReceiveBuffer();
        public bool InitialObjectsQueued{get;internal set;}
        public Connection(string remoteAs,double now)
        {
            remoteAddress=remoteAs;
            connectionState=ConnectionState.Connecting;
            lastReceiveTime=now;
            lastSendTime=now;
        }
    }
}

