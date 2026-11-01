using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace UeNet
{
    public static class PackedIntegerCodec 
    {
        public const int MaxBytes32=5;
        public static void WriteUInt32(BinaryWriter writer,uint value)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            while (value >= 128)
            {
                writer.Write((byte)((value&0x7fu)|0x80u));
                value>>=7;
            }
            writer.Write((byte)value);
        }
        public static uint ReadUInt32(BinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            uint value=0;
            for(int i = 0; i < MaxBytes32; i++)
            {
                byte part=reader.ReadByte();
                if (i == 4 && (part & 0xF0) != 0)
                {
                    throw new FormatException("VarInt32 overflow.");
                }
                value|=(uint)(part&0x7f)<<(i*7);
                if ((part & 0x80) == 0)
                {
                    if (i > 0 && (part & 0x7f) == 0)
                    {
                        throw new FormatException("Non-minimal VarInt32.");
                    }
                    return value;
                }
            }
            throw new FormatException("VarInt32 is too long.");
        }
        public static void WriteInt32(BinaryWriter writer,int value)
        {
            uint encoded=unchecked(((uint)value<<1)^(uint)(value>>31));
            WriteUInt32(writer,encoded);
        }
        public static int ReadInt32(BinaryReader reader)
        {
            uint encoded=ReadUInt32(reader);
            return (int)(encoded>>1)^-(int)(encoded&1u);
        }
    }
}

