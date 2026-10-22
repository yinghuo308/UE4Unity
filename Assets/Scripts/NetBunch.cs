using System;


public enum NetBunchType
{
    Open = 1,
    Properties = 2,
    Close = 3,
    Rpc=4
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
}
[Serializable]
public class RpcCallData
{
    public ushort RpcId;
    public RpcDirection Direction;
    public string ArgsJson;
}
[Serializable]
public class HitRpcArgs
{
    public int Damage;
}
