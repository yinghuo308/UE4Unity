using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.WSA;
namespace UeNet
{
    public class FragmentData 
    {
        public uint ConnectionId;
        public uint GroupId;
        public ushort FragmentIndex;
        public ushort FragmentCount;
        public int TotalLength;
        public byte[] PayLoad;
    }
    public static class FragmentCodec
    {
        private const uint Magic=0x31475246;
        // 标记4 + 连接4 + 组编号4 + 序号2
        // + 总片数2 + 总长度4 + 本片长度2
        public const int HeaderSize = 22;
        public const int MaxFragments = 64;
        public const int MaxMessageBytes = 64 * 1024;
        public static bool IsFragment(byte[] bytes)
        {
            return bytes!=null&&bytes.Length>=4&&
                    bytes[0] == 0x46 &&
                    bytes[1] == 0x52 &&
                    bytes[2] == 0x47 &&
                    bytes[3] == 0x31;
        }
        public static List<byte[]> Split(uint connectionId,uint groupId,
                    byte[]message,int maxPacketBytes = UdpTransport.RecommendedPacketBytes)
        {
            if(connectionId==0)throw new ArgumentOutOfRangeException(nameof(connectionId));
            if (groupId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(groupId));
            }
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (message.Length == 0 ||
                message.Length > MaxMessageBytes)
            {
                throw new ArgumentException(
                    "Invalid message size.",
                    nameof(message));
            }
            if(maxPacketBytes<=NetPacketCodec.HeaderSize+HeaderSize||
                    maxPacketBytes > UdpTransport.MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPacketBytes));
            }
            int capacity=maxPacketBytes-NetPacketCodec.HeaderSize-HeaderSize;
            int count=(message.Length+capacity-1)/capacity;
            if (count > MaxFragments)
            {
                throw new ArgumentException("Too many fragments. Increase packet size.");
            }
            var result=new List<byte[]>(count);
            for(int index = 0; index < count; index++)
            {
                int offset=index*capacity;
                int length=Math.Min(capacity,message.Length-offset);
                using(var stream=new MemoryStream(HeaderSize+length))
                using(var writer=new BinaryWriter(stream))
                {
                    writer.Write(Magic);
                    writer.Write(connectionId);
                    writer.Write(groupId);
                    writer.Write((ushort)index);
                    writer.Write((ushort)count);
                    writer.Write(message.Length);
                    writer.Write((ushort)length);
                    writer.Write(message,offset,length);
                    writer.Flush();
                    result.Add(stream.ToArray());
                }
            }
            return result;
        }
        public static bool TryDecode(byte[] bytes,out FragmentData fragment)
        {
            fragment=null;
            if(!IsFragment(bytes)||bytes.Length<=HeaderSize||
                    bytes.Length > UdpTransport.MaxPayloadBytes - NetPacketCodec.HeaderSize)
            {
                return false;
            }
             using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != Magic)
                    return false;
                uint connectionId = reader.ReadUInt32();
                uint groupId = reader.ReadUInt32();
                ushort index = reader.ReadUInt16();
                ushort count = reader.ReadUInt16();
                int totalLength = reader.ReadInt32();
                int length = reader.ReadUInt16();
                if (connectionId == 0 ||groupId == 0 ||count == 0 ||
                    count > MaxFragments ||index >= count ||totalLength <= 0 ||
                    totalLength > MaxMessageBytes ||count > totalLength)
                {
                    return false;
                }
                if (length == 0 ||
                    length != stream.Length - stream.Position ||
                    length > totalLength - (count - 1))
                {
                    return false;
                }
                if (count == 1 && length != totalLength)
                    return false;
                fragment = new FragmentData
                {
                    ConnectionId = connectionId,
                    GroupId = groupId,
                    FragmentIndex = index,
                    FragmentCount = count,
                    TotalLength = totalLength,
                    PayLoad = reader.ReadBytes(length)
                };
                return true;
            }
        }
    }
}

