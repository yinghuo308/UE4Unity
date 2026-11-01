using System;

namespace UeNet
{
    public struct NetPacket
    {
        public uint Sequence;
        public bool HasAck;
        public uint Ack;
        public uint AckBits;
        public ulong SessionId;
        public byte[]Payload;
        public NetPacket(uint sequence,
            bool hasAck,
            uint ack,
            uint ackBits,
            ulong sessionId,
            byte[] payload)
        {
            Sequence = sequence;
            HasAck = hasAck;
            Ack = ack;
            AckBits = ackBits;
            SessionId=sessionId;
            Payload = payload;
        }
    }
    public static class NetPacketCodec
    {
        public const int HeaderSize=21;
        public static byte[] Encode(NetPacket packet)
        {
            if (packet.Payload == null)
                throw new ArgumentNullException(nameof(packet.Payload));

            byte[] bytes =
                new byte[HeaderSize + packet.Payload.Length];

            WriteUInt32(bytes, 0, packet.Sequence);
            bytes[4] = packet.HasAck ? (byte)1 : (byte)0;
            WriteUInt32(bytes, 5, packet.Ack);
            WriteUInt32(bytes, 9, packet.AckBits);
            WriteUInt32(bytes, 13, (uint)packet.SessionId);
            WriteUInt32(bytes, 17, (uint)(packet.SessionId >> 32));
            Buffer.BlockCopy(packet.Payload,0,bytes,HeaderSize,packet.Payload.Length);
            return bytes;
        }
        private static void WriteUInt32(byte[]bytes,int offset,uint value)
        {
            bytes[offset]=(byte)value;
            bytes[offset+1]=(byte)(value>>8);
            bytes[offset+2]=(byte)(value>>16);
            bytes[offset + 3] = (byte)(value >> 24);
        }
        public static bool TryDecode(byte[]bytes,out NetPacket packet)
        {
            packet=default(NetPacket);
            if(bytes==null||bytes.Length<HeaderSize)return false;
            if(bytes[4]>1)return false;
            ulong sessionId=(ulong)ReadUInt32(bytes,13)|((ulong)ReadUInt32(bytes,17)<<32);
            byte[] payload =new byte[bytes.Length - HeaderSize];
            Buffer.BlockCopy(bytes,HeaderSize,payload,0,payload.Length);
            packet=new NetPacket( ReadUInt32(bytes, 0),
                            bytes[4] == 1,
                            ReadUInt32(bytes, 5),
                            ReadUInt32(bytes, 9),
                            sessionId,
                            payload);
        return true;
        }
        private static uint ReadUInt32(byte[]bytes,int offset)
        {
            return (uint)bytes[offset]|(uint)(bytes[offset+1]<<8)|
        ((uint)bytes[offset + 2] << 16) |
        ((uint)bytes[offset + 3] << 24);
        }
    }
    
}