using System;
using System.Collections.Generic;
using UeNet;


public enum NetBunchType
{
    Open = 1,
    Properties = 2,
    Close = 3,
    Rpc=4,
    SubobjectProperties=5,
    SubobjectCreate=6,
    SubobjectDestroy=7,
    SubobjectItemProperties=8,
    OwnerChanged=9
}
public enum RpcDirection
{
    Server=1,
    Client=2
}
[Serializable]
public sealed class NetBunch
{
    public uint ObjectId;
    public NetBunchType Type;
    public string PayloadJson;

    // Close Bunch 的语义：true 表示对象永久销毁，false 表示暂时离开相关范围。
    // 旧格式没有这个字段时，JsonUtility 会得到 false，兼容临时关闭。
    public bool DestroyObject;
}
[Serializable]
public class RpcCallData
{
    public ushort RpcId;
    public RpcDirection Direction;
    public string ArgsJson;
    public uint OwnerVersion;
    public List<uint>ObjectReferenceIds;
}
[Serializable]
public class DelayRpcTestArgs
{
    public int Order;
    public uint ReferenceId;
}
[Serializable]
public class HitRpcArgs
{
    public int Damage;
}
[Serializable]
public class AttackRpcArgs
{
    public uint TargetObjectId;
}
[Serializable]
public sealed class OwnerChangeData
{
    public uint OwnerConnectId;
    public ObjectUpdateData State;
}