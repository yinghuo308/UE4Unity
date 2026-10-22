using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public static class RpcRegistry
    {
        private static readonly Dictionary<ushort,Action<NetworkObject,string>> 
                handlers=new Dictionary<ushort, Action<NetworkObject, string>>();
        public static void Register(ushort rpcId,Action<NetworkObject,string> handler)
        {
            if(rpcId==0)throw new ArgumentOutOfRangeException(nameof(rpcId));
            if(handler==null)throw new ArgumentNullException(nameof(handler));
            handlers[rpcId]=handler;
        }
        public static bool TryInvoke(ushort rpcId,NetworkObject target,string argsJson)
        {
            if(target==null||!handlers.TryGetValue(rpcId,out var handler))
            {
                return false;
            }
            handler(target,argsJson??string.Empty);
            return true;
        }

    }

}
