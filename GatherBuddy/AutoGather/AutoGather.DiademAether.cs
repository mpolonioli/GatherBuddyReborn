using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Helpers;
using System;
using System.Linq;
using System.Numerics;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private const int AetherGaugeOffset = 0x268;
        private const int AetherGaugeReadyThreshold = 200;
        private const float AetherTargetScanRadius = 25f;
        private const uint AethercannonActionId = 19700;
        private DateTime _nextAetherAttempt = DateTime.MinValue;
        private readonly TimeSpan _aetherDebounce = TimeSpan.FromSeconds(2);
        private readonly TimeSpan _aetherFailurePenalty = TimeSpan.FromSeconds(10);
        private bool _aetherShotFired;
        private bool _aetherShotConfirmed;
        private int _aetherGaugeBeforeShot;
        private DateTime _lastAetherLosLog = DateTime.MinValue;

        private unsafe bool TryGetDiademAetherGauge(out int gauge)
        {
            gauge = 0;
            var addonPtr = Dalamud.GameGui.GetAddonByName("HWDAetherGauge");
            if (addonPtr == nint.Zero)
                return false;

            var addon = (AtkUnitBase*)(nint)addonPtr;
            if (addon == null || !addon->IsVisible)
                return false;

            gauge = *(int*)((nint)addonPtr + AetherGaugeOffset);
            return true;
        }

        private bool IsDiademAetherGaugeReady()
            => TryGetDiademAetherGauge(out var gauge) && gauge >= AetherGaugeReadyThreshold;
        
        private IGameObject? FindNearbyEnemyForAether()
        {
            var player = Dalamud.Objects.LocalPlayer;
            if (player == null)
                return null;

            Vector3 pPos = player.Position;
            Vector3 eye  = pPos with { Y = pPos.Y + 2f };
            IGameObject? best = null;
            float bestDistSq = AetherTargetScanRadius * AetherTargetScanRadius;
            var losRejected = 0;

            foreach (var obj in Dalamud.Objects)
            {
                if (obj is not IBattleNpc bnpc)
                    continue;

                if (!IsValidDiademEnemy(bnpc))
                    continue;

                float distSq = Vector3.DistanceSquared(pPos, bnpc.Position);
                if (distSq >= bestDistSq)
                    continue;

                if (!HasLineOfSight(eye, bnpc.Position with { Y = bnpc.Position.Y + 1f }))
                {
                    ++losRejected;
                    continue;
                }

                bestDistSq = distSq;
                best = bnpc;
            }

            if (best == null && losRejected > 0 && DateTime.UtcNow - _lastAetherLosLog > TimeSpan.FromSeconds(5))
            {
                _lastAetherLosLog = DateTime.UtcNow;
                GatherBuddy.Log.Debug($"[Diadem] {losRejected} enemies in range but none in line of sight");
            }

            return best;
        }

        private static bool HasLineOfSight(Vector3 from, Vector3 to)
        {
            var offset   = to - from;
            var distance = offset.Length();
            //Stop the ray one yalm short so grazing the target's own perch doesn't count as a block.
            if (distance <= 1f)
                return true;

            return !BGCollisionModule.RaycastMaterialFilter(from, offset / distance, out _, distance - 1f);
        }

        private bool IsValidDiademEnemy(IBattleNpc bnpc)
        {
            if (bnpc.IsDead)
                return false;

            if (!bnpc.IsTargetable)
                return false;

            if (bnpc.SubKind is 2 or 9)
                return false;

            return true;
        }

        private void AetherShotFailed(string reason)
        {
            //LoS is positional: resume gathering for a while so the next attempt comes from a different spot.
            _nextAetherAttempt = DateTime.UtcNow + _aetherFailurePenalty;
            GatherBuddy.Log.Debug($"[Diadem] Aethercannon shot failed ({reason}), retrying in {_aetherFailurePenalty.TotalSeconds:F0}s");
        }
        
        private unsafe void TargetByGameObject(IGameObject gameObject)
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return;
                
            targetSystem->Target = (CSGameObject*)gameObject.Address;
        }
        
        private unsafe bool TryUseAetherCannon()
        {
            if (!GatherBuddy.Config.AutoGatherConfig.DiademAutoAetherCannon)
                return false;
            if (!Diadem.IsInside)
                return false;
            if (Dalamud.Conditions[ConditionFlag.Mounted])
                return false;
            if (IsPathing)
                return false;
            if (DateTime.UtcNow < _nextAetherAttempt)
                return false;
            if (!IsDiademAetherGaugeReady())
                return false;

            var enemy = FindNearbyEnemyForAether();
            if (enemy == null)
                return false;

            var enemyId = enemy.GameObjectId;
            TargetByGameObject(enemy);
            _nextAetherAttempt   = DateTime.UtcNow + _aetherDebounce;
            _aetherShotFired     = false;
            _aetherShotConfirmed = false;
            TryGetDiademAetherGauge(out _aetherGaugeBeforeShot);
            GatherBuddy.Log.Debug($"[Diadem] Targeting enemy {enemy.Name} (ID: {enemyId}) at {enemy.Position}");

            TaskManager.DelayNext(100);

            TaskManager.Enqueue(() =>
            {
                var currentTarget = Dalamud.Targets.Target;
                if (currentTarget == null || currentTarget.GameObjectId != enemyId)
                {
                    GatherBuddy.Log.Debug($"[Diadem] Target not set properly. Current: {currentTarget?.Name ?? "null"}");
                    return true;
                }

                GatherBuddy.Log.Debug($"[Diadem] Target confirmed: {currentTarget.Name}, distance: {Vector3.Distance(Player.Position, currentTarget.Position):F1}y");
                return true;
            });

            EnqueueActionWithDelay(() =>
            {
                var currentTarget = Dalamud.Targets.Target;
                if (currentTarget == null)
                {
                    GatherBuddy.Log.Debug($"[Diadem] No target when trying to fire");
                    return;
                }

                var amInstance = ActionManager.Instance();
                if (amInstance == null)
                {
                    GatherBuddy.Log.Debug($"[Diadem] ActionManager.Instance() is null");
                    return;
                }

                var targetId = currentTarget.GameObjectId;
                var actionStatus = amInstance->GetActionStatus(ActionType.Action, AethercannonActionId);
                GatherBuddy.Log.Debug($"[Diadem] Firing at target ID {targetId}, action status: {actionStatus}");

                if (actionStatus == 0)
                {
                    var result = amInstance->UseAction(ActionType.Action, AethercannonActionId, targetId);
                    GatherBuddy.Log.Debug($"[Diadem] UseAction returned: {result}");
                    if (result)
                        _aetherShotFired = true;
                    else
                        AetherShotFailed("UseAction rejected the shot");
                }
                else
                {
                    AetherShotFailed($"action status code {actionStatus}");
                }
            });

            //The aethercannon never sets ConditionFlag.Casting, so a gauge drop is the only reliable success signal.
            TaskManager.Enqueue(() =>
            {
                if (!_aetherShotFired)
                    return true;

                if (!TryGetDiademAetherGauge(out var gauge) || gauge >= _aetherGaugeBeforeShot)
                    return false;

                _aetherShotConfirmed = true;
                GatherBuddy.Log.Debug($"[Diadem] Aethercannon shot confirmed, gauge {_aetherGaugeBeforeShot} -> {gauge}");
                return true;
            }, 3000, "Wait for aethercannon gauge drop");
            TaskManager.Enqueue(() =>
            {
                if (_aetherShotFired && !_aetherShotConfirmed)
                    AetherShotFailed("gauge did not drop, shot likely rejected by server");
            }, "Check aethercannon result");
            TaskManager.DelayNext(500);
            return true;
        }
    }
}
