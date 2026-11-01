using System;
using System.Collections.Generic;


namespace UeNet
{
    public class SimulatedTransport : ITransport
    {
        public const int MaxPendingPackets=4096;
        public const int MaxPendingBytes = 4 * 1024 * 1024;
        public const int MaxPacketsPerPump = 256;
        public sealed class PendingPacket
        {
            public string Remote;
            public byte[] Bytes;
            public double DueTime;
            public ulong Order;
        }
        private readonly ITransport inner;
        private readonly Func<double> clock;
        private readonly Random random;
        private readonly double lossProbability;
        private readonly double duplicateProbability;
        private readonly double latencySeconds;
        private readonly double jitterSeconds;
        private readonly SortedSet<PendingPacket> pending=new SortedSet<PendingPacket>(
            Comparer<PendingPacket>.Create((a, b) =>
            {
              int result=a.DueTime.CompareTo(b.DueTime);
              return result!=0?result:a.Order.CompareTo(b.Order);  
            })
        );
        private ulong nextOrder;
        private int pendingBytes;
        private bool disposed;
        public long RequestedPackets { get; private set; }
        public long RandomDroppedPackets { get; private set; }
        public long QueueDroppedPackets { get; private set; }
        public long QueuedDuplicates { get; private set; }
        public long SentPackets { get; private set; }
        public long SentBytes { get; private set; }
        public int PendingPackets
        {
            get { return pending.Count; }
        }
        public int PendingBytes
        {
            get { return pendingBytes; }
        }
        public string LocalAddress
        {
            get
            {
                ThrowIfDisposed();
                return inner.LocalAddress;
            }
        }
        public bool Enabled { get; set; } = true;
        public SimulatedTransport(ITransport inner,Func<double> clock,
            double lossProbability = 0.1,double duplicateProbability = 0.02,
            double latencySeconds = 0.1,double jitterSeconds = 0.05,
            int seed = 12345)
        {
            this.inner = inner ??throw new ArgumentNullException(nameof(inner));
            this.clock = clock ??throw new ArgumentNullException(nameof(clock));
            ValidateNumber(lossProbability,1.0,nameof(lossProbability));
            ValidateNumber(duplicateProbability,1.0,nameof(duplicateProbability));
            ValidateNumber(latencySeconds,double.MaxValue,nameof(latencySeconds));
            ValidateNumber(jitterSeconds,double.MaxValue,nameof(jitterSeconds));
            this.lossProbability = lossProbability;
            this.duplicateProbability = duplicateProbability;
            this.latencySeconds = latencySeconds;
            this.jitterSeconds = jitterSeconds;
            random = new Random(seed);
        }
        private static void ValidateNumber(double value,double maximum,string name)
        {
            if(double.IsNaN(value)||double.IsInfinity(value)||
                value < 0.0 || value > maximum)
            {
                throw new ArgumentOutOfRangeException(name);
            }            
        }
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            pending.Clear();
            pendingBytes = 0;
            inner.Dispose();
        }
        public void Send(string remote, byte[] bytes)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(remote))
            {
                throw new ArgumentException("Remote address is required.",nameof(remote));
            }
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length > UdpTransport.MaxPayloadBytes)
            {
                throw new ArgumentException("UDP payload is too large.",
                    nameof(bytes));
            }
            RequestedPackets++;
            if (!Enabled)
            {
                inner.Send(remote, bytes);
                SentPackets++;
                SentBytes += bytes.Length;
                return;
            }
            if (random.NextDouble() < lossProbability)
            {
                RandomDroppedPackets++;
                return;
            }
            double now = clock();
            if (!Enqueue(remote, bytes, now))
                return;
            // 未丢弃的包，有一定概率额外排入一份副本。
            if (random.NextDouble() < duplicateProbability &&
                Enqueue(remote, bytes, now))
            {
                QueuedDuplicates++;
            }
        }
        private bool Enqueue(string remote,byte[] bytes,double now)
        {
            if (pending.Count >= MaxPendingPackets ||
                pendingBytes + bytes.Length > MaxPendingBytes)
            {
                QueueDroppedPackets++;
                return false;
            }
            double jitter =(random.NextDouble() * 2.0 - 1.0) *jitterSeconds;
            double delay =Math.Max(0.0, latencySeconds + jitter);
            pending.Add(new PendingPacket
            {
                Remote = remote,
                Bytes = (byte[])bytes.Clone(),
                DueTime = now + delay,
                Order = nextOrder++
            });
            pendingBytes += bytes.Length;
            return true;
        }
        public void Pump()
        {
            ThrowIfDisposed();
            double now=clock();
            int sent=0;
             while (pending.Count > 0 &&sent < MaxPacketsPerPump)
            {
                PendingPacket packet = pending.Min;
                // 最早的包都没到期，后面的也不用检查。
                if (packet.DueTime > now)
                    break;
                inner.Send(packet.Remote, packet.Bytes);
                pending.Remove(packet);
                pendingBytes -= packet.Bytes.Length;
                SentPackets++;
                SentBytes += packet.Bytes.Length;
                sent++;
            }
        }
        public bool TryReceive(out Datagram datagram)
        {
            ThrowIfDisposed();
            // 接收直接交给底层。
            // 对方的发送模拟器负责影响这个方向。
            return inner.TryReceive(out datagram);
        }
        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(SimulatedTransport));
            }
        }
        public void DiscardPending()
        {
            ThrowIfDisposed();
            pending.Clear();
            pendingBytes=0;
        }

    }
}

