using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
namespace UeNet
{
    public enum NetMessageType : byte
    {
        ConnectRequest=1,
        ConnectAccept=2,
        Heartbeat=3,
        DisConnect=4,
        ReliableTest=5,

        SpawnObject=6,
        DespawnObject=7,
        StateUpdate=8
    }
    [Serializable]
        public class SpawnData
        {
            public uint ObjectId;
            public string ObjectType;
            public int health;
            public uint StateVersion;
            public Vector3 Position;
        }
    [Serializable]
    public class ObjectStateData
    {
        public uint ObjectId;
        public uint StateVersion;
        public int Health;
        public Vector3 Position;
    }
    [Serializable]
    public class DespawnData
    {
        public uint ObjectId; 
    }
    public static class NetMessage 
    {
         [Serializable]
        private sealed class MessageData
        {
            public int Type;
            public uint ConnectionId;
            public uint MessageId;
            public bool Reliable;
            public string Data;
        }
        

        public static byte[] Encode(
            NetMessageType type,
            uint connectionId)
        {
            return Encode(type,connectionId,0,false,string.Empty);
        }
        public static byte[] Encode(NetMessageType type,
            uint connectionId,
            uint messageId,
            bool reliable)
        {
            return Encode(
                type,
                connectionId,
                messageId,
                reliable,
                string.Empty);
        }
        public static byte[] Encode(NetMessageType type,uint connectionId,
                            uint messageId,bool reliable,string data)
        {
            MessageData message = new MessageData
            {
                Type = (int)type,
                ConnectionId = connectionId,
                MessageId = messageId,
                Reliable = reliable,
                Data=data??string.Empty
            };

            string json = JsonUtility.ToJson(message);
            return Encoding.UTF8.GetBytes(json);
        }

        public static bool TryDecode(byte[] bytes,out NetMessageType type,out uint connectionId)
        {
            return TryDecode(bytes,out type,out connectionId,out _,out _,out 
            _);
        }
        public static bool TryDecode(byte[] bytes,out NetMessageType type,out uint connectionId,
                                        out uint messageId,out bool reliable,out string data)
        {
            type = default(NetMessageType);
            connectionId = 0;
            messageId = 0;
            reliable = false;
            data=string.Empty;

            if (bytes == null || bytes.Length == 0)
                return false;

            try
            {
                string json = Encoding.UTF8.GetString(bytes);
                MessageData message =JsonUtility.FromJson<MessageData>(json);
                if (message == null)
                    return false;
                if (message.Type <
                        (int)NetMessageType.ConnectRequest ||
                    message.Type >
                        (int)NetMessageType.StateUpdate)
                    return false;
                type = (NetMessageType)message.Type;
                connectionId = message.ConnectionId;
                messageId = message.MessageId;
                reliable = message.Reliable;
                data=message.Data??string.Empty;
                // 可靠消息必须有有效 MessageId。
                if (reliable && messageId == 0)
                    return false;
                // 不可靠消息不应该携带 MessageId。
                if (!reliable && messageId != 0)
                    return false;
                return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
        }
    }
}

