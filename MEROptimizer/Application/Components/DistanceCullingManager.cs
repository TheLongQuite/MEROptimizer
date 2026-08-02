using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using Mirror;
using PlayerRoles;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class DistanceCullingManager : MonoBehaviour
{
    public static DistanceCullingManager Instance { get; private set; }

    private readonly List<OptimizedSchematic> _schematics = [];
    private readonly Dictionary<Player, Dictionary<PrimitiveCluster, bool>> _playerClusterState = new();
    
    private readonly Dictionary<Vector2Int, List<PrimitiveCluster>> _spatialGrid = new();
    private readonly List<PrimitiveCluster> _stateKeysBuffer = new();

    private Player[] _playerCache = [];
    private float _playerCacheTimer;
    private const float PlayerCacheInterval = 1f;

    private float _checkTimer;
    private const float CheckInterval = 0.2f;
    private int _currentPlayerIndex;
    private const int PlayersPerTick = 6;

    private const float GridCellSize = 50f;
    
    public void Awake() => Instance = this;

    public void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }
    
    private Vector2Int WorldToGrid(Vector3 pos) => 
        new(Mathf.FloorToInt(pos.x / GridCellSize), 
            Mathf.FloorToInt(pos.z / GridCellSize));
    
    private bool IsValidPlayer(Player player)
    {
        if (player.IsDestroyed || player.IsNpc || player.IsDummy || player.IsHost)
            return false;

        return player.Role != RoleTypeId.None;
    }

    private bool ShouldPlayerSeeNothing(Player player)
    {
        if (MerOptimizer.ShouldSpectatorsSeeNothing && 
            player.Role is RoleTypeId.Spectator or RoleTypeId.Overwatch)
            return true;

        return false;
    }

    public void RegisterSchematic(OptimizedSchematic schematic)
    {
        if (!_schematics.Contains(schematic))
            _schematics.Add(schematic);

        foreach (KeyValuePair<Player, Dictionary<PrimitiveCluster, bool>> kvp in _playerClusterState)
        {
            foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters.Where(cluster => !kvp.Value.ContainsKey(cluster)))
                kvp.Value[cluster] = false;
        }
        
        foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
        {
            Vector2Int cell = WorldToGrid(cluster.CenterPosition);
            if (!_spatialGrid.TryGetValue(cell, out List<PrimitiveCluster> list))
            {
                list = new List<PrimitiveCluster>();
                _spatialGrid[cell] = list;
            }
            list.Add(cluster);
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
        
        foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
        {
            Vector2Int cell = WorldToGrid(cluster.CenterPosition);
            if (_spatialGrid.TryGetValue(cell, out List<PrimitiveCluster> list))
            {
                list.Remove(cluster);
                if (list.Count == 0)
                    _spatialGrid.Remove(cell);
            }
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
        
        RefreshPlayerCache();
    }

    public void OnPlayerLeft(Player player)
    {
        if (player.Connection is NetworkConnectionToClient conn)
            NetworkBatcher.ClearQueue(conn);

        _playerClusterState.Remove(player);
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
        if (!IsValidPlayer(player)) 
            return;

        if (!_playerClusterState.TryGetValue(player, out Dictionary<PrimitiveCluster, bool> states))
            return;

        _stateKeysBuffer.Clear();
        foreach (PrimitiveCluster cluster in states.Keys)
            _stateKeysBuffer.Add(cluster);

        foreach (PrimitiveCluster cluster in _stateKeysBuffer)
        {
            if (!states.TryGetValue(cluster, out bool state) || !state) 
                continue;
            
            cluster.UnspawnFor(player);
            states[cluster] = false;
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

    public void SetSpectatorClusterState(Player player, PrimitiveCluster cluster, bool state)
    {
        if (!_playerClusterState.TryGetValue(player, out Dictionary<PrimitiveCluster, bool> states))
        {
            states = new();
            _playerClusterState[player] = states;
        }
        states[cluster] = state;
    }

    private void RefreshPlayerCache()
    {
        MerOptimizer.Debug($"[REFRESH] Total players: {Player.List.Count()}");
        _playerCache = Player.List.Where(IsValidPlayer).ToArray();
        MerOptimizer.Debug($"[REFRESH] Valid players: {_playerCache.Length}");
    }

    public void Update()
    {
        NetworkBatcher.Update();
        
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

            if (ShouldPlayerSeeNothing(player))
            {
                if (_playerClusterState.TryGetValue(player, out var states))
                {
                    _stateKeysBuffer.Clear();
                    foreach (PrimitiveCluster cluster in states.Keys)
                        _stateKeysBuffer.Add(cluster);

                    foreach (PrimitiveCluster cluster in _stateKeysBuffer)
                    {
                        if (!states.TryGetValue(cluster, out bool state) || !state) continue;
                        cluster.UnspawnFor(player);
                        states[cluster] = false;
                    }
                }
                
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
        
        int spawned = 0;
        int unspawned = 0;
        int checkedClusters = 0;
        
        Vector2Int playerCell = WorldToGrid(playerPos);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                Vector2Int cell = new Vector2Int(playerCell.x + dx, playerCell.y + dz);
                if (!_spatialGrid.TryGetValue(cell, out List<PrimitiveCluster> clustersInCell))
                    continue;

                foreach (PrimitiveCluster cluster in clustersInCell)
                {
                    checkedClusters++;

                    float sqrDist = (cluster.CenterPosition - playerPos).sqrMagnitude;
                    float threshold = cluster.SpawnDistance;
                    float sqrThreshold = threshold * threshold;

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
                                EnqueueSlowSpawnForSpectators(player, cluster);

                            break;
                        }
                        case false when wasInside:
                        {
                            float hysteresis = threshold * Plugin.MerOptimizer._unspawnHysteresisMultiplier;
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
        }

        if (MerOptimizer.IsDebug && spawned == 0 && unspawned == 0 && checkedClusters > 0)
            MerOptimizer.Debug($"[CULL-RESULT] {player.DisplayName} checked={checkedClusters} spawned=0 unspawned=0");
        else if (spawned > 0 || unspawned > 0)
            MerOptimizer.Debug($"[CULL-RESULT] {player.DisplayName} checked={checkedClusters} spawned={spawned} unspawned={unspawned}");
    }
    
    private void SpawnForPlayerAndSpectators(Player player, ClientSidePrimitive primitive)
    {
        primitive.SpawnClientPrimitive(player);
        foreach (Player spectator in player.CurrentSpectators)
        {
            if (IsValidPlayer(spectator))
                primitive.SpawnClientPrimitive(spectator);
        }
    }

    private void EnqueueSlowSpawnForSpectators(Player target, PrimitiveCluster cluster)
    {
        if (MerOptimizer.ShouldSpectatorsSeeNothing) return;

        foreach (Player spectator in target.CurrentSpectators)
        {
            if (!IsValidPlayer(spectator)) continue;
            if (!_playerClusterState.TryGetValue(spectator, out Dictionary<PrimitiveCluster, bool> specStates)) continue;
            if (specStates.TryGetValue(cluster, out bool state) && state) continue;

            cluster.EnqueueSlowSpawn(spectator);
            specStates[cluster] = true;
        }
    }

    private void UnspawnForSpectators(Player target, PrimitiveCluster cluster)
    {
        if (MerOptimizer.ShouldSpectatorsSeeNothing) return;

        foreach (Player spectator in target.CurrentSpectators)
        {
            if (!IsValidPlayer(spectator)) continue;
            if (!_playerClusterState.TryGetValue(spectator, out Dictionary<PrimitiveCluster, bool> specStates)) continue;
            if (!specStates.TryGetValue(cluster, out bool state) || !state) continue;

            cluster.UnspawnFor(spectator);
            specStates[cluster] = false;
        }
    }
    
    private void CleanupDisconnectedPlayers()
    {
        List<Player> toRemove = null;
        foreach (Player player in _playerClusterState.Keys)
        {
            if (IsValidPlayer(player))
                continue;

            toRemove ??= [];
            toRemove.Add(player);
        }

        if (toRemove == null)
            return;

        foreach (Player player in toRemove)
        {
            _playerClusterState.Remove(player);
            MerOptimizer.Debug($"[CLEANUP] Removed {player?.DisplayName ?? "null"} from state tracking");
        }
    }
}