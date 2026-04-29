using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class DistanceCullingManager : MonoBehaviour
{
    public static DistanceCullingManager Instance { get; private set; }

    private readonly List<OptimizedSchematic> _schematics = [];
    private readonly Dictionary<Player, Dictionary<PrimitiveCluster, bool>> _playerClusterState = new();
    private readonly Dictionary<Player, Vector3> _lastPlayerPosition = new();
    private readonly Dictionary<Player, float> _lastTeleportCheckTime = new();

    private const float TeleportDetectDistance = 10f;
    private const float TeleportCheckCooldown = .35f;
    private const int ImmediateNonClusteredTeleportSpawn = 25;

    private Player[] _playerCache = [];
    private float _playerCacheTimer;
    private const float PlayerCacheInterval = 1f;

    private float _checkTimer;
    private const float CheckInterval = 0.3f;
    private int _currentPlayerIndex;
    private const int PlayersPerTick = 3;

    public void Awake() => Instance = this;

    public void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private bool IsValidPlayer(Player player)
    {
        if (player.IsDestroyed || player.IsNpc || player.IsDummy || player.IsHost)
            return false;

        return player.Role != RoleTypeId.None;
    }

    public void RegisterSchematic(OptimizedSchematic schematic)
    {
        if (!_schematics.Contains(schematic))
            _schematics.Add(schematic);

        foreach (KeyValuePair<Player, Dictionary<PrimitiveCluster, bool>> kvp in _playerClusterState)
        {
            foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters
                         .Where(cluster => !kvp.Value.ContainsKey(cluster)))
                kvp.Value[cluster] = false;
        }
    }

    public void UnregisterSchematic(OptimizedSchematic schematic)
    {
        _schematics.Remove(schematic);

        foreach (KeyValuePair<Player, Dictionary<PrimitiveCluster, bool>> kvp in _playerClusterState)
        {
            foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
                kvp.Value.Remove(cluster);
        }
    }

    public void OnPlayerJoined(Player player)
    {
        if (!IsValidPlayer(player))
        {
            MerOptimizer.Debug($"[JOIN] Skipping {player?.DisplayName ?? "null"} - not valid");
            return;
        }

        if (!_playerClusterState.ContainsKey(player))
        {
            _playerClusterState[player] = new();
            MerOptimizer.Debug($"[JOIN] Added {player.DisplayName} to state tracking");
        }

        _lastPlayerPosition[player] = player.Position;
        _lastTeleportCheckTime[player] = -100f;
        RefreshPlayerCache();
    }

    public void OnPlayerLeft(Player player)
    {
        _playerClusterState.Remove(player);
        _lastPlayerPosition.Remove(player);
        _lastTeleportCheckTime.Remove(player);
        RefreshPlayerCache();
    }

    public void ForceSpawnAllClusters(Player player)
    {
        if (!IsValidPlayer(player)) return;

        if (!_playerClusterState.TryGetValue(player, out Dictionary<PrimitiveCluster, bool> states))
        {
            states = new();
            _playerClusterState[player] = states;
        }

        foreach (PrimitiveCluster cluster in _schematics.SelectMany(schematic => schematic.PrimitiveClusters))
        {
            if (states.TryGetValue(cluster, out bool wasInside) && wasInside)
                continue;

            if (cluster.instantSpawn)
                cluster.SpawnFor(player);
            else
                cluster.EnqueueSpawn(player);

            states[cluster] = true;
        }
    }

    public void ForceUnspawnDistantClusters(Player player)
    {
        if (!IsValidPlayer(player)) return;

        if (!_playerClusterState.TryGetValue(player, out Dictionary<PrimitiveCluster, bool> states))
            return;

        foreach (KeyValuePair<PrimitiveCluster, bool> kvp in states.ToList().Where(kvp => kvp.Value))
        {
            kvp.Key.UnspawnFor(player);
            states[kvp.Key] = false;
        }
    }

    public bool IsPlayerInsideCluster(Player player, PrimitiveCluster cluster)
    {
        if (_playerClusterState.TryGetValue(player, out Dictionary<PrimitiveCluster, bool> states))
            return states.TryGetValue(cluster, out bool inside) && inside;

        return false;
    }

    public List<Player> GetPlayersInsideCluster(PrimitiveCluster cluster)
    {
        List<Player> result = [];
        foreach (KeyValuePair<Player, Dictionary<PrimitiveCluster, bool>> kvp in _playerClusterState)
        {
            if (kvp.Value.TryGetValue(cluster, out bool inside) && inside)
                result.Add(kvp.Key);
        }

        return result;
    }

    private void RefreshPlayerCache()
    {
        MerOptimizer.Debug($"[REFRESH] Total players: {Player.List.Count()}");
        _playerCache = Player.List.Where(IsValidPlayer).ToArray();
        MerOptimizer.Debug($"[REFRESH] Valid players: {_playerCache.Length}");
    }

    public void Update()
    {
        _playerCacheTimer += Time.deltaTime;
        if (_playerCacheTimer >= PlayerCacheInterval)
        {
            _playerCacheTimer = 0f;
            RefreshPlayerCache();
            CleanupDisconnectedPlayers();
        }

        if (_schematics.Count == 0 || _playerCache.Length == 0)
            return;

        _checkTimer += Time.deltaTime;
        if (_checkTimer < CheckInterval)
            return;

        _checkTimer = 0f;

        int playersToProcess = Math.Min(PlayersPerTick, _playerCache.Length);

        for (int i = 0; i < playersToProcess; i++)
        {
            _currentPlayerIndex = (_currentPlayerIndex + 1) % _playerCache.Length;
            Player player = _playerCache[_currentPlayerIndex];

            if (!IsValidPlayer(player))
            {
                MerOptimizer.Debug($"[UPDATE-SKIP] {player?.DisplayName ?? "null"} - not valid");
                continue;
            }

            if (ShouldSkipCulling(player))
            {
                MerOptimizer.Debug($"[UPDATE-SKIP] {player.DisplayName} - whitelisted role {player.Role}");
                continue;
            }

            ProcessPlayerCulling(player);
        }
    }

    private bool ShouldSkipCulling(Player player)
    {
        RoleTypeId role = player.Role;
        if (role is RoleTypeId.Filmmaker or RoleTypeId.Scp079)
            return true;

        if (!MerOptimizer.ShouldSpectatorsBeAffectedByPds &&
            role is RoleTypeId.Spectator or RoleTypeId.Overwatch)
            return true;

        return !MerOptimizer.ShouldTutorialsBeAffectedByDistanceSpawning &&
               role == RoleTypeId.Tutorial;
    }

    private void ProcessPlayerCulling(Player player)
    {
        if (!IsValidPlayer(player)) return;

        Vector3 playerPos = player.Position;

        if (playerPos == Vector3.zero || playerPos.sqrMagnitude < 0.01f)
            return;

        if (!_playerClusterState.TryGetValue(player, out Dictionary<PrimitiveCluster, bool> states))
        {
            states = new();
            _playerClusterState[player] = states;
            MerOptimizer.Debug($"[CULL] Created new state for {player.DisplayName}");
        }

        TryHandleTeleportPriority(player, states);

        int spawned = 0;
        int unspawned = 0;
        int totalClusters = 0;
        int checkedClusters = 0;

        float closestDist = float.MaxValue;
        Vector3 closestClusterPos = Vector3.zero;
        int closestClusterId = -1;
        string closestSchematicName = "";

        foreach (OptimizedSchematic schematic in _schematics)
        {
            if (!schematic?.Schematic)
                continue;

            List<PrimitiveCluster> clusters = schematic.PrimitiveClusters;
            totalClusters += clusters.Count;

            foreach (PrimitiveCluster cluster in clusters)
            {
                checkedClusters++;

                float sqrDist = (cluster.CenterPosition - playerPos).sqrMagnitude;
                float threshold = cluster.SpawnDistance;
                float sqrThreshold = threshold * threshold;

                if (sqrDist < closestDist)
                {
                    closestDist = sqrDist;
                    closestClusterPos = cluster.CenterPosition;
                    closestClusterId = cluster.ID;
                    closestSchematicName = schematic.Schematic?.Name ?? "unknown";
                }

                bool wasInside = states.TryGetValue(cluster, out bool prevState) && prevState;
                bool isInside = sqrDist <= sqrThreshold;

                switch (isInside)
                {
                    case true when !wasInside:
                    {
                        if (cluster.instantSpawn)
                            cluster.SpawnFor(player);
                        else
                            cluster.EnqueueSpawn(player);

                        states[cluster] = true;
                        spawned++;

                        if (MerOptimizer.ShouldSpectatorsBeAffectedByPds)
                            SpawnForSpectators(player, cluster);

                        break;
                    }
                    case false when wasInside:
                    {
                        float hysteresis = threshold + 5f;
                        if (sqrDist <= hysteresis * hysteresis)
                            continue;

                        cluster.UnspawnFor(player);
                        states[cluster] = false;
                        unspawned++;

                        if (MerOptimizer.ShouldSpectatorsBeAffectedByPds)
                            UnspawnForSpectators(player, cluster);

                        break;
                    }
                }
            }
        }

        if (spawned == 0 && totalClusters > 0)
        {
            float actualDist = Mathf.Sqrt(closestDist);
            MerOptimizer.Debug($"[CULL-NEAREST] {player.DisplayName} " +
                               $"nearest cluster #{closestClusterId} of '{closestSchematicName}' " +
                               $"at {closestClusterPos:F2}, dist={actualDist:F2}, " +
                               "spawnDist threshold=???");
        }

        MerOptimizer.Debug($"[CULL-RESULT] {player.DisplayName} checked={checkedClusters} " +
                           $"total={totalClusters} spawned={spawned} unspawned={unspawned}");
    }

    private void TryHandleTeleportPriority(Player player, Dictionary<PrimitiveCluster, bool> states)
    {
        Vector3 currentPos = player.Position;

        if (!_lastPlayerPosition.TryGetValue(player, out Vector3 lastPos))
        {
            _lastPlayerPosition[player] = currentPos;
            return;
        }

        float traveledSqr = (currentPos - lastPos).sqrMagnitude;
        _lastPlayerPosition[player] = currentPos;

        if (traveledSqr < TeleportDetectDistance * TeleportDetectDistance)
            return;

        float now = Time.time;
        if (_lastTeleportCheckTime.TryGetValue(player, out float lastCheck) &&
            now - lastCheck < TeleportCheckCooldown)
            return;

        _lastTeleportCheckTime[player] = now;

        foreach (OptimizedSchematic schematic in _schematics)
        {
            if (schematic?.Schematic == null)
                continue;

            OptimizedSchematic.TeleportPriorityEntry entry = schematic.GetClosestTeleportEntry(currentPos);
            if (entry == null)
                continue;

            int immediateSpawned = 0;
            foreach (ClientSidePrimitive primitive in entry.NonClustered)
            {
                if (immediateSpawned >= ImmediateNonClusteredTeleportSpawn)
                    break;

                SpawnForPlayerAndSpectators(player, primitive);
                immediateSpawned++;
            }

            foreach (KeyValuePair<PrimitiveCluster, List<ClientSidePrimitive>> kv in entry.Clustered)
            {
                PrimitiveCluster cluster = kv.Key;
                List<ClientSidePrimitive> critical = kv.Value;

                if (cluster == null || critical == null || critical.Count == 0)
                    continue;

                if (cluster.instantSpawn)
                {
                    foreach (ClientSidePrimitive primitive in critical)
                        SpawnForPlayerAndSpectators(player, primitive);
                }
                else
                {
                    if (!cluster.AwaitingSpawn.ContainsKey(player))
                        cluster.EnqueueSpawn(player);

                    cluster.EnqueuePrioritySpawn(player, critical);
                }

                states[cluster] = true;
            }

            MerOptimizer.Debug($"[TP-PRIORITY] {player.DisplayName} matched teleport in {schematic.Schematic.Name}");
            break;
        }
    }

    private void SpawnForPlayerAndSpectators(Player player, ClientSidePrimitive primitive)
    {
        primitive.SpawnClientPrimitive(player);
        foreach (Player spectator in player.CurrentSpectators.Where(IsValidPlayer))
            primitive.SpawnClientPrimitive(spectator);
    }

    private void SpawnForSpectators(Player target, PrimitiveCluster cluster)
    {
        foreach (Player spectator in target.CurrentSpectators
                     .Where(spectator => IsValidPlayer(spectator)))
            cluster.SpawnFor(spectator);
    }

    private void UnspawnForSpectators(Player target, PrimitiveCluster cluster)
    {
        foreach (Player spectator in target.CurrentSpectators
                     .Where(spectator => IsValidPlayer(spectator)))
            cluster.UnspawnFor(spectator);
    }

    private void CleanupDisconnectedPlayers()
    {
        List<Player> toRemove = null;
        foreach (Player player in _playerClusterState.Keys)
        {
            if (!IsValidPlayer(player))
            {
                toRemove ??= [];
                toRemove.Add(player);
            }
        }

        if (toRemove == null)
            return;

        foreach (Player player in toRemove)
        {
            _playerClusterState.Remove(player);
            _lastPlayerPosition.Remove(player);
            _lastTeleportCheckTime.Remove(player);
            MerOptimizer.Debug($"[CLEANUP] Removed {player?.DisplayName ?? "null"} from state tracking");
        }
    }
}