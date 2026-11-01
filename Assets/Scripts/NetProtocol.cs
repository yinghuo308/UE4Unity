using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
namespace UeNet
{
    [Serializable]
    public sealed class NetHandshakeData
    {
        public int ProtocolVersion;
        public int PropertyLayoutVersion;
        public int PropertyFormatVersion;
        public string Reason;
        public ulong ClientNonce;
        public ulong SessionId;
    }
    public static class NetProtocol 
    {
        public const int ProtocolVersion=4;
        public const int PropertyLayoutVersion=1;
        public const byte PropertyFormatVersion=7;
        public const int MaxHandshakeChars=1024;
        public static string CreateHandshakeJson(ulong clientNonce,
                ulong sessionId, string reason="")
        {
            var data=new NetHandshakeData
            {
                ProtocolVersion=ProtocolVersion,
                PropertyLayoutVersion=PropertyLayoutVersion,
                PropertyFormatVersion=PropertyFormatVersion,
                ClientNonce=clientNonce,
                SessionId=sessionId,
                Reason=reason??string.Empty
            };
            return JsonUtility.ToJson(data);
        }
        public static bool TryRead(string json,out NetHandshakeData data)
        {
            data=null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxHandshakeChars)
            {
                return false;
            }
            try
            {
                NetHandshakeData value=JsonUtility.FromJson<NetHandshakeData>(json);
                if(value==null||value.ProtocolVersion<=0||
                    value.PropertyLayoutVersion<=0||value.PropertyFormatVersion<=0)
                return false;
                data=value;
                return true;
            }
            catch
            {
                return false;
            }
        }
        public static bool TryValidate(string json,out string reason)
        {
            reason=string.Empty;
            if(!TryRead(json,out NetHandshakeData peer))
            {
                reason="握手数据缺失";
                return false;                
            }
            if (peer.ProtocolVersion != ProtocolVersion ||
                peer.PropertyLayoutVersion != PropertyLayoutVersion ||
                peer.PropertyFormatVersion != PropertyFormatVersion)
            {
                reason =
                    $"版本不匹配：接收端要求 " +
                    $"{ProtocolVersion}/" +
                    $"{PropertyLayoutVersion}/" +
                    $"{PropertyFormatVersion}，" +
                    $"发送端提供 " +
                    $"{peer.ProtocolVersion}/" +
                    $"{peer.PropertyLayoutVersion}/" +
                    $"{peer.PropertyFormatVersion}。";

                return false;
            }
            return true;
        }
        public static ulong CreateToken()
        {
            byte[]bytes=new byte[8];
            ulong token;
            using(var random = RandomNumberGenerator.Create())
            {
                do
                {
                    random.GetBytes(bytes);
                    token=BitConverter.ToUInt64(bytes,0);
                }
                while(token==0);
            }
            return token;
        }
    }

}
