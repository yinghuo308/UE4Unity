using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace UeNet
{
    public static class PacketPayloadCodec
    {
        private const uint Magic=0x31544142;
        public const int HeaderSize=6;
        public const int LengthSize=2;
        public const int MaxMessages=64;
        public const int MaxPayloadBytes=UdpTransport.MaxPayloadBytes-NetPacketCodec.HeaderSize;
        public static bool IsBatch(byte[] bytes)
        {
            return bytes!=null&&bytes.Length>=4&&bytes[0]==0x42
                &&bytes[1]==0x41&&
                   bytes[2] == 0x54 &&
                   bytes[3] == 0x31;
        }
        public static byte[] Encode(List<byte[]> messages)
        {
            if (messages == null || messages.Count == 0 || messages.Count > MaxMessages)
            {
                throw new ArgumentException("Invalid message count.");
            }
            int size=HeaderSize;
            foreach(byte[] message in messages)
            {
                if (message == null || message.Length == 0 || message.Length > ushort.MaxValue)
                {
                    throw new ArgumentException("Invalid message size.");
                }
                size+=LengthSize+message.Length;
                if (size > MaxPayloadBytes)
                {
                     throw new ArgumentException("Packet payload is too large.");
                }
            }
            using(var stream=new MemoryStream(size))
            using(var writer=new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write((ushort)messages.Count);
                foreach(byte[] message in messages)
                {
                    writer.Write((ushort)message.Length);
                    writer.Write(message);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }
        public static bool TryDecode(byte[]bytes,out List<byte[]>messages)
        {
            messages=null;
            if (!IsBatch(bytes) || bytes.Length < HeaderSize || bytes.Length > MaxPayloadBytes)
            {
                return false;
            }
            using(MemoryStream stream=new MemoryStream(bytes,false))
            using(BinaryReader reader=new BinaryReader(stream))
            {
                if(reader.ReadUInt32()!=Magic)return false;
                int count=reader.ReadUInt16();
                if(count==0||count>MaxMessages)
                return false;
                var reslut=new List<byte[]>(count);
                for(int i = 0; i < count; i++)
                {
                    if(stream.Length-stream.Position<LengthSize)return false;
                    int length=reader.ReadUInt16();
                    if (length == 0 || length > stream.Length - stream.Position)
                    {
                        return false;
                    }
                    reslut.Add(reader.ReadBytes(length));
                }
                if(stream.Position!=stream.Length)
                return false;
                messages=reslut;
                return true;
            }
        }
    }
}
