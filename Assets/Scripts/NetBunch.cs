using System;


public enum NetBunchType
{
    Open = 1,
    Properties = 2,
    Close = 3
}

[Serializable]
public sealed class NetBunch
{
    public uint ObjectId;
    public NetBunchType Type;
    public string PayloadJson;
}
