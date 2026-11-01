using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UeNet
{
    public class NetworkObject : MonoBehaviour
    {
        public uint NetObjectId{get;internal set;}
        public int Health=100;
        public uint OwnerConnectId{get;internal set;}
        public uint OwnerVersion { get; internal set; }
        public bool IsPlayerObject { get; internal set; }
        public bool IsServerObject{get;internal set;}
        public uint StateVersion{get;internal set;}
        internal NetworkObjectRegistry Registry{get;set;}
        private NetworkObject target;
        public uint TargetObjectId{get;internal set;}
        public NetworkObject Target
        {
            get=>target;
            set
            {
                if(Registry==null)throw new InvalidOperationException(
                        "请先注册网络对象，再设置 Target。");
                if (value != null &&
                    (value.Registry != Registry ||
                     value.NetObjectId == 0))
                {
                    throw new ArgumentException(
                        "Target 必须属于同一个对象注册表。");
                }
                Registry.SetTarget(this,value!=null?value.NetObjectId:0);
            }
        }
        public event Action<int,int>HealthChanged;
        public event Action<NetworkObject,NetworkObject>TargetChanged;
        private bool healthNotifyPending;
        private int previousHealth;
        private bool targetNotifyPending;
        private NetworkObject previousTarget;
        private uint previousTargetId;
        public int PrivateScore;
        public InventoryComponent Inventory{get;internal set;}
        private uint nextSubobjectId=2;
        internal readonly Dictionary<uint,ReplicatedItemData>ReplicatedItems=new Dictionary<uint, ReplicatedItemData>();
        [SerializeField]private bool smoothPosition = true;
        [SerializeField, Min(0.01f)]private float positionSnapDistance = 5f;
        [SerializeField]private bool smoothRotation = true;
        [SerializeField, Range(0f, 180f)]private float rotationSnapAngle = 90f;
        [SerializeField, Min(0f)]private float movementInterpolationDelay = 0.2f;
        [SerializeField,Min(0f)]private float maxExtrapolationTime=0.1f;
        [SerializeField, Min(0.001f)]private float positionCorrectionTime = 0.1f;
        private const double SnapshotResetGap = 1.0;
        private readonly MovementSnapshotBuffer movementSnapshots =
            new MovementSnapshotBuffer();
        private bool hasReceivedMovement;
        private double renderServerTime;
        private Vector3 positionCorrection;
        private bool previousSmoothPosition;
        private bool previousSmoothRotation;
        private double previousInterpolationDelay;
        private double previousMaxExtrapolation;

        [SerializeField]private Vector3 velocity;
        public Vector3 Velocity => velocity;
        private bool hasVelocitySample;
        private Vector3 lastVelocitySamplePosition;
        private double lastVelocitySampleTime;
        private MovementStateData movement;
        private bool hasServerMovement;
        public double MovementServerTime => movement.ServerTime;
        [SerializeField] private string objectType="NetworkObject";
        public string ObjectType=>objectType;
        internal void ApplyTargetId(uint objectId)
        {
            if (Registry == null)
            {
                throw new InvalidOperationException(
                    "对象尚未注册。");
            }

            Registry.SetTarget(this, objectId);
        }
        // 只有注册表直接修改实际保存的引用，
        // 避免再次进入 Target 的 setter。
        internal void SetResolvedTarget(
            uint objectId,
            NetworkObject resolved)
        {
             bool changed =
                TargetObjectId != objectId ||
                !object.ReferenceEquals(target, resolved);
            if (changed &&
                !IsServerObject &&
                !targetNotifyPending)
            {
                previousTargetId = TargetObjectId;
                previousTarget = target;
                targetNotifyPending = true;
            }
            TargetObjectId = objectId;
            target = resolved;
        }
        internal void ApplyReplicatedHealth(int value)
        {
            if(IsServerObject)return;
            if(Health==value)return;
            if (!healthNotifyPending)
            {
                previousHealth=Health;
                healthNotifyPending=true;
            }
            Health=value;
        }
        void LateUpdate()
        {
            UpdateMovementInterpolation(Time.unscaledDeltaTime);
            bool notifyHealth=healthNotifyPending;
            int oldHealth = previousHealth;
            int newHealth = Health;
            bool notifyTarget = targetNotifyPending;
            uint oldTargetId = previousTargetId;
            uint newTargetId = TargetObjectId;
            NetworkObject oldTarget = previousTarget;
            NetworkObject newTarget = target;
            healthNotifyPending=false;
            targetNotifyPending=false;
            previousTarget=null;
            if(IsServerObject||Registry==null)return;
            if (notifyHealth && oldHealth != newHealth)
            {
                OnRepHealth(oldHealth,newHealth);
            }
            if(this==null||Registry==null)return;
            bool targetChanged=oldTargetId!=newTargetId||!object.ReferenceEquals(oldTarget,newTarget);
            if (notifyTarget && targetChanged)
            {
                OnRepTarget(oldTarget,newTarget,oldTargetId,newTargetId);
            }
        }

        private void OnRepTarget(NetworkObject oldTarget, NetworkObject newTarget, uint oldTargetId, uint newTargetId)
        {
            TargetChanged?.Invoke(oldTarget,newTarget);
        }

        private void OnRepHealth(int oldHealth, int newHealth)
        {
            HealthChanged?.Invoke(oldHealth,newHealth);
        }
        public uint AllocatedSubobjectId()
        {
            if (!IsServerObject)
            {
                throw new InvalidOperationException(
                    "只有服务器可以分配子对象编号。");
            }
            if (nextSubobjectId == 0)
            {
                throw new InvalidOperationException(
                    "子对象编号已经耗尽。");
            }
            uint id = nextSubobjectId;
            nextSubobjectId =
                unchecked(nextSubobjectId + 1);
            return id;
        }
        internal void SampleServerVelocity(double now)
        {
            if(!IsServerObject||double.IsNaN(now)||double.IsInfinity(now))
            return;
            Vector3 currentPosition=transform.position;
            if(!VelocityUtility.IsFinite(currentPosition))
            return;
            if (!hasVelocitySample)
            {
                hasVelocitySample=true;
                lastVelocitySamplePosition=currentPosition;
                lastVelocitySampleTime=now;
                velocity=Vector3.zero;
                return;
            }
            double elapsed=now-lastVelocitySampleTime;
            if(elapsed<=0.0)return;
            Vector3 sampled=(currentPosition-lastVelocitySamplePosition)/(float)elapsed;
            if (!VelocityUtility.IsFinite(sampled) || sampled.sqrMagnitude <= VelocityUtility.ChangeThreshold * VelocityUtility.ChangeThreshold)
            {
                sampled=Vector3.zero;
            }        
            velocity=sampled;
            lastVelocitySamplePosition=currentPosition;
            lastVelocitySampleTime=now;    
        }
        internal void ApplyReplicatedVelocity(Vector3 value)
        {
            if (IsServerObject || !VelocityUtility.IsFinite(value))
            {
                return;
            }
            velocity=value;
        }
        internal void SampleServerMovement(double now)
        {
            if (!IsServerObject || (hasServerMovement && now <= movement.ServerTime))
            {
                return;
            }
            var candidate=new MovementStateData
            {
                ServerTime=now,
                Position=transform.position,
                Rotation=transform.rotation,
                Velocity=Vector3.zero
            };
            if (!candidate.TryNormalize(out MovementStateData snapshot))
            {
                return;
            }
            SampleServerVelocity(now);
            snapshot.Velocity = Velocity;
            movement = snapshot;
            hasServerMovement = true;
        }
        internal MovementStateData GetServerMovement()
        {
            if (!IsServerObject)
            {
                throw new InvalidOperationException("Only server objects can capture movement.");
            }
            if (!hasServerMovement)
            {
                SampleServerMovement(Time.realtimeSinceStartupAsDouble);
            }
            if (!hasServerMovement)
            {
                throw new InvalidOperationException("Unable to capture valid movement.");
            }
            return movement;
        }
        internal void ApplyReplicatedMovement(MovementStateData value)
        {
            if(IsServerObject||!value.TryNormalize(out MovementStateData snapshot))
            {
                return;
            }
            if (hasReceivedMovement &&
                snapshot.ServerTime <= movement.ServerTime)
            {
                return;
            }
            double delay = ReadNonNegative(movementInterpolationDelay, 0.2);
            double limit = ReadNonNegative(maxExtrapolationTime, 0.1);
            Vector3 displayedPosition = transform.position;
            bool needsCorrection =hasReceivedMovement &&
                smoothPosition &&(renderServerTime > movement.ServerTime ||
                positionCorrection.sqrMagnitude > 0f);
            bool reset=!hasReceivedMovement ||
                    snapshot.ServerTime - movement.ServerTime >
                        Math.Max(SnapshotResetGap, delay * 2.0) ||
                    (snapshot.Position - movement.Position).sqrMagnitude >=
                        positionSnapDistance * positionSnapDistance ||
                    Quaternion.Angle(movement.Rotation,
                        snapshot.Rotation) >= rotationSnapAngle;
            if (reset)
            {
                ResetMovementPlayback(snapshot,delay,limit);
            }
            else
            {
                 if (!movementSnapshots.Add(snapshot))
                    return;
                renderServerTime = Math.Max(renderServerTime,
                    snapshot.ServerTime - delay);
                    if (needsCorrection &&
                        movementSnapshots.TrySample(renderServerTime,
                            limit,out MovementStateData corrected))
                    {
                        Vector3 error =displayedPosition - corrected.Position;
                        if (!VelocityUtility.IsFinite(error) ||
                            error.sqrMagnitude >=positionSnapDistance * positionSnapDistance)
                        {
                            ResetMovementPlayback(snapshot, delay, limit);
                        }
                        else
                        {
                            positionCorrection = error;
                        }
                    }
                if (!smoothPosition)
                {
                    positionCorrection = Vector3.zero;
                    transform.position = snapshot.Position;
                }
                if (!smoothRotation)
                    transform.rotation = snapshot.Rotation;
            }
            movement=snapshot;
            hasReceivedMovement=true;
            ApplyReplicatedVelocity(snapshot.Velocity);
        }
        private static double ReadNonNegative(float value,double fallback)
        {
            if (float.IsNaN(value) ||
                float.IsInfinity(value))
            {
                return fallback;
            }
            return Math.Max(0.0, (double)value);
        }
        private void ResetMovementPlayback(MovementStateData snapshot,double delay,double limit)
        {
            movementSnapshots.Clear();
            movementSnapshots.Add(snapshot);
            renderServerTime=snapshot.ServerTime-delay;
            positionCorrection=Vector3.zero;
            transform.position=snapshot.Position;
            transform.rotation=snapshot.Rotation;
            previousSmoothPosition=smoothPosition;
            previousSmoothRotation=smoothRotation;
            previousInterpolationDelay=delay;
            previousMaxExtrapolation=limit;
        }
        private void UpdateMovementInterpolation(float deltaTime)
        {
            if (IsServerObject ||Registry == null ||!hasReceivedMovement)
            {
                return;
            }
            double delay = ReadNonNegative(movementInterpolationDelay, 0.2);
            double limit = ReadNonNegative(maxExtrapolationTime, 0.1);
            if (previousSmoothPosition != smoothPosition ||
                previousSmoothRotation != smoothRotation ||
                previousInterpolationDelay != delay ||
                previousMaxExtrapolation != limit)
            {
                ResetMovementPlayback(movement, delay,limit);
            }
             if (deltaTime > 0f &&!float.IsNaN(deltaTime) &&
                !float.IsInfinity(deltaTime))
            {
                renderServerTime = Math.Min(
                    movement.ServerTime+limit,
                    renderServerTime + deltaTime);
                double duration = ReadNonNegative(positionCorrectionTime, 0.1);
                float decay = duration > 0.0? 
                        (float)Math.Exp(-deltaTime / duration): 0f;
                positionCorrection *= decay;
                if (positionCorrection.sqrMagnitude <= 0.000001f)
                    positionCorrection = Vector3.zero;
            }
            if(!movementSnapshots.TrySample(renderServerTime,limit,out MovementStateData display))
            {
                return;
            }
            Vector3 position =display.Position + positionCorrection;
            if (!VelocityUtility.IsFinite(position))
            {
                positionCorrection = Vector3.zero;
                position = display.Position;
            }
            transform.position =smoothPosition
                    ? position: movement.Position;
            transform.rotation =smoothRotation
                    ? display.Rotation: movement.Rotation;
        }
    }
}

