using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace UeNet
{
    /// <summary>一次 UDP 收包的结果。Bytes 是独立副本，不会被下一次收包覆盖。</summary>
    public readonly struct Datagram
    {
        public readonly string Remote;
        public readonly byte[] Bytes;

        public Datagram(string remote, byte[] bytes)
        {
            Remote = remote;
            Bytes = bytes;
        }
    }

    /// <summary>传输层只负责地址和原始字节，不负责 ACK、重传、Actor 或 RPC。</summary>
    public interface ITransport : IDisposable
    {
        string LocalAddress { get; }
        void Send(string remote, byte[] bytes);
        bool TryReceive(out Datagram datagram);
    }

    /// <summary>
    /// IPv4 UDP 传输层。
    /// 必须在 Unity 主线程调用；TryReceive 不阻塞，适合放在 MonoBehaviour.Update 中。
    /// </summary>
    public sealed class UdpTransport : ITransport
    {
        // UDP/IPv4 的理论最大负载；UE4 风格协议建议上层将包控制在 1200 字节以内。
        public const int MaxPayloadBytes = 65507;
        public const int RecommendedPacketBytes = 1200;

        private readonly Socket socket;
        private readonly byte[] receiveBuffer = new byte[MaxPayloadBytes];
        private bool disposed;

        public string LocalAddress
        {
            get
            {
                ThrowIfDisposed();
                return ((IPEndPoint)socket.LocalEndPoint).ToString();
            }
        }

        /// <param name="port">服务端使用固定端口；客户端传 0 让系统分配端口。</param>
        /// <param name="bindAddress">本地 IPv4 地址，默认监听所有网卡。</param>
        public UdpTransport(int port, string bindAddress = "0.0.0.0")
        {
            if (port < 0 || port > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (!IPAddress.TryParse(bindAddress, out IPAddress address) ||
                address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Bind address must be an IPv4 address.", nameof(bindAddress));

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(address, port));
                // Unity 的 Update 线程不能被网络等待阻塞。
                socket.Blocking = false;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>发送一个完整 UDP 数据报；成功只表示本机已接受发送请求。</summary>
        public void Send(string remote, byte[] bytes)
        {
            ThrowIfDisposed();
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length > MaxPayloadBytes)
                throw new ArgumentException("Payload exceeds the IPv4 UDP limit.", nameof(bytes));

            IPEndPoint endpoint = ParseEndpoint(remote);
            int sent = socket.SendTo(bytes, endpoint);
            if (sent != bytes.Length)
                throw new InvalidOperationException("The complete UDP datagram was not sent.");
        }

        /// <summary>
        /// 尝试取出一个数据报；没有数据时立即返回 false。
        /// Unity 每帧应循环调用，但由上层限制每帧最大处理数量。
        /// </summary>
        public bool TryReceive(out Datagram datagram)
        {
            ThrowIfDisposed();
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                int length = socket.ReceiveFrom(receiveBuffer, ref remote);
                byte[] bytes = new byte[length];
                Buffer.BlockCopy(receiveBuffer, 0, bytes, 0, length);
                datagram = new Datagram(remote.ToString(), bytes);
                return true;
            }
            catch (SocketException exception) when (
                exception.SocketErrorCode == SocketError.WouldBlock ||
                exception.SocketErrorCode == SocketError.Interrupted ||
                exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                // UDP 没有连接关闭事件；连接超时由上层 Connection 判断。
                datagram = default(Datagram);
                return false;
            }
        }

        private static IPEndPoint ParseEndpoint(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            int colon = value.LastIndexOf(':');
            if (colon <= 0 || colon == value.Length - 1 ||
                !IPAddress.TryParse(value.Substring(0, colon), out IPAddress address) ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                !ushort.TryParse(value.Substring(colon + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out ushort port) || port == 0)
                throw new ArgumentException(
                    "Use an IPv4 endpoint such as 127.0.0.1:7777.", nameof(value));
            return new IPEndPoint(address, port);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(UdpTransport));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            socket.Dispose();
        }
    }
}

/// <summary>
/// Unity 测试组件：挂到场景中的 GameObject 即可测试 Transport。
/// 勾选 Is Server 的实例监听 Local Port；客户端实例向 Remote Address 发送 Hello。
/// </summary>
public sealed class Transport : MonoBehaviour
{
    [Header("Local UDP")]
    [SerializeField] private bool isServer = false;
    [SerializeField] private string bindAddress = "0.0.0.0";
    [Tooltip("服务端监听的端口；客户端始终绑定端口 0，让系统自动分配。")]
    [Range(1, 65535)]
    [SerializeField] private int localPort = 7777;

    [Header("Remote UDP")]
    [SerializeField] private string remoteAddress = "127.0.0.1:7777";
    [SerializeField] private bool sendHelloOnStart = true;
    [Min(1)]
    [SerializeField] private int receiveBudgetPerFrame = 128;

    private UeNet.UdpTransport transport;
    private bool helloPending;

    public string LocalAddress
    {
        get { return transport == null ? string.Empty : transport.LocalAddress; }
    }

    private void Start()
    {
        try
        {
            transport = new UeNet.UdpTransport(isServer ? localPort : 0, bindAddress);
            Debug.Log(name + " UDP started at " + transport.LocalAddress, this);
            // 等到第一次 Update 再发包，使同一场景中的服务端先完成 Start/Bind。
            helloPending = !isServer && sendHelloOnStart;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
            StopTransport();
            enabled = false;
        }
    }

    private void Update()
    {
        if (transport == null) return;
        try
        {
            if (helloPending)
            {
                helloPending = false;
                SendText("Hello");
            }

            int budget = Mathf.Max(1, receiveBudgetPerFrame);
            for (int i = 0; i < budget; i++)
            {
                if (!transport.TryReceive(out UeNet.Datagram datagram)) break;
                string text = Encoding.UTF8.GetString(datagram.Bytes);
                Debug.Log(name + " UDP received from " + datagram.Remote + ": " + text, this);

                // 示例只回复 Hello，防止两个服务端互相回复形成无限回包。
                // 正式项目应把 Bytes 交给 NetworkDriver。
                if (isServer && text == "Hello")
                    transport.Send(datagram.Remote, Encoding.UTF8.GetBytes("Echo: Hello"));
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
            StopTransport();
            enabled = false;
        }
    }

    /// <summary>供 UI 按钮或其他网络层发送 UTF-8 文本测试包。</summary>
    public void SendText(string text)
    {
        if (transport == null) throw new InvalidOperationException("Transport has not started.");
        if (string.IsNullOrEmpty(remoteAddress)) throw new ArgumentException("Remote address is empty.");
        transport.Send(remoteAddress, Encoding.UTF8.GetBytes(text ?? string.Empty));
    }

    private void StopTransport()
    {
        helloPending = false;
        if (transport == null) return;
        transport.Dispose();
        transport = null;
    }

    private void OnDestroy()
    {
        StopTransport();
    }

    private void OnApplicationQuit()
    {
        StopTransport();
    }
}
