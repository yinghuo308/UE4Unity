using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace UeNet
{
    public static class PropertyMessageCodec 
    {
        private const uint Magic=0x31505250;
        private const byte Version=NetProtocol.PropertyFormatVersion;
        public const int HeaderSize=19;
        public const int PropertyHeaderSize=6;
        private const int MaxPropertyValueBytes=51;
        public const int MaxProperties=4;
        public static bool IsPropertyMessage(byte[] bytes)
        {
            return bytes != null &&
                   bytes.Length >= 4 &&
                   bytes[0] == 0x50 &&
                   bytes[1] == 0x52 &&
                   bytes[2] == 0x50 &&
                   bytes[3] == 0x31;
        }
        private static int GetMaxValueSize(ushort propertyId)
        {
            switch (propertyId)
            {
                case 1: return 4;  // Health：int。
                //case 2: return 3 * PackedIntegerCodec.MaxBytes32; // Position：三个int。
                case 3: return 4;  // TargetObjectId：uint。
                case 4: return 4;  // PrivateScore：int。
               // case 5: return 16;
                //case 6: return 12;
                case 7:return 8 +3 * PackedIntegerCodec.MaxBytes32 +
                   16 +12;
                default: return 0;
            }
        }
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) &&
                   !float.IsInfinity(value);
        }
        private static void WriteValue(BinaryWriter writer,
            ushort propertyId,ObjectStateData state)
        {
            switch (propertyId)
            {
                case 1:
                    writer.Write(state.Health);
                    break;
                case 3:
                    writer.Write(state.TargetObjectId);
                    break;
                case 4:
                    writer.Write(state.PrivateScore);
                    break;
                case 7:
                    // Encode 已通过 TryReadUpdate，
                    // 移动数据已在 WriteTo 中整组校验。
                    writer.Write(state.MovementServerTime);
                    PackedIntegerCodec.WriteInt32(
                        writer,
                        PositionQuantization.Encode(state.Position.x));
                    PackedIntegerCodec.WriteInt32(
                        writer,
                        PositionQuantization.Encode(state.Position.y));
                    PackedIntegerCodec.WriteInt32(
                        writer,
                        PositionQuantization.Encode(state.Position.z));
                    writer.Write(state.Rotation.x);
                    writer.Write(state.Rotation.y);
                    writer.Write(state.Rotation.z);
                    writer.Write(state.Rotation.w);
                    writer.Write(state.Velocity.x);
                    writer.Write(state.Velocity.y);
                    writer.Write(state.Velocity.z);
                    break;

                default:
                    throw new ArgumentException("Unknown property.");
            }
        }
        private static bool ReadValue(BinaryReader reader,
            ushort propertyId,ObjectStateData state)
        {
            switch (propertyId)
            {
                case 1:
                    state.Health = reader.ReadInt32();
                    return true;
                case 3:
                    state.TargetObjectId = reader.ReadUInt32();
                    return true;
                case 4:
                    state.PrivateScore = reader.ReadInt32();
                    return true;
                case 7:
                    var movement = new MovementStateData
                    {
                        ServerTime = reader.ReadDouble(),
                        Position = new Vector3(
                            PositionQuantization.Decode(
                                PackedIntegerCodec.ReadInt32(reader)),
                            PositionQuantization.Decode(
                                PackedIntegerCodec.ReadInt32(reader)),
                            PositionQuantization.Decode(
                                PackedIntegerCodec.ReadInt32(reader))),
                        Rotation = new Quaternion(
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle()),
                        Velocity = new Vector3(
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle())
                    };
                    // 全部合法以后，才写入 state。
                    // 校验失败抛出的 ArgumentException，
                    // 会由现有 TryDecode 捕获并返回 false。
                    movement.WriteTo(state);
                    return true;
                default:
                    return false;
            }
        }
        public static byte[] Encode(uint connectionId,ObjectUpdateData update)
        {
            if (connectionId == 0 ||
                update == null ||
                update.ObjectId == 0 ||
                update.Properties == null ||
                update.Properties.Count == 0 ||
                update.Properties.Count > MaxProperties)
            {
                throw new ArgumentException("Invalid property message.");
            }
            int capacity=HeaderSize+update.Properties.Count*
                    (PropertyHeaderSize+MaxPropertyValueBytes);
            using(var stream =new MemoryStream(capacity))
            using(var writer=new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(connectionId);
                writer.Write(update.ObjectId);
                writer.Write(update.OwnerVersion);
                writer.Write((ushort)update.Properties.Count);
                var seen=new HashSet<ushort>();
                foreach(PropertyUpdateData item in update.Properties)
                {
                    if(item==null||GetMaxValueSize(item.PropertyId)==0||
                        !seen.Add(item.PropertyId)||!RepLayout.Instance.
                        TryGetProperty(item.PropertyId,out RepProperty definition))
                    {
                        throw new ArgumentException("Invalid property");
                    }
                    var state=new ObjectStateData();
                    if (!definition.TryReadUpdate(item, state))
                    {
                        throw new ArgumentException("Invalid property value.");
                    }
                    writer.Write(item.PropertyId);
                    writer.Write(item.PropertyVersion);
                    WriteValue(writer,item.PropertyId,state);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }
        public static bool TryDecode(byte[]bytes,out uint connectionId,out ObjectUpdateData update)
        {
            connectionId=0;
            update=null;
            int maximumSize=HeaderSize+MaxProperties*(PropertyHeaderSize+MaxPropertyValueBytes);
            if (!IsPropertyMessage(bytes) ||
                bytes.Length < HeaderSize ||
                bytes.Length > maximumSize)
            {
                return false;
            }
            try
            {
                using(var stream=new MemoryStream(bytes,false))
                using(var reader=new BinaryReader(stream))
                {
                    if (reader.ReadUInt32() != Magic ||
                        reader.ReadByte() != Version)
                    {
                        return false;
                    }
                    uint decodedConnectionId =reader.ReadUInt32();
                    uint objectId=reader.ReadUInt32();
                    uint ownerVersion=reader.ReadUInt32();
                    int count=reader.ReadUInt16();
                    if (decodedConnectionId == 0 ||objectId == 0 ||
                        count == 0 ||count > MaxProperties)
                    {
                        return false;
                    }
                    var result = new ObjectUpdateData
                    {
                        ObjectId = objectId,
                        OwnerVersion=ownerVersion
                    };
                    var seen = new HashSet<ushort>();
                    for(int i = 0; i < count; i++)
                    {
                        if(stream.Length-stream.Position<PropertyHeaderSize)
                        return false;
                        ushort propertyId=reader.ReadUInt16();
                        uint propertyVersion=reader.ReadUInt32();
                        if (GetMaxValueSize(propertyId) == 0 ||
                            !seen.Add(propertyId) ||
                            !RepLayout.Instance.TryGetProperty(
                                propertyId,
                                out RepProperty definition))
                        {
                            return false;
                        }
                        var state=new ObjectStateData();
                        if(!ReadValue(reader,propertyId,state))
                        return false;
                        result.Properties.Add(
                            definition.CreateUpdate(state,propertyVersion)
                        );
                    }
                    if(stream.Position!=stream.Length)
                    return false;
                    connectionId=decodedConnectionId;
                    update=result;
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}

