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
        if (player == null || player.IsNpc) return;

        if (!_playerClusterState.ContainsKey(player))
            _playerClusterState[player] = new();

        RefreshPlayerCache();
    }

    public void OnPlayerLeft(Player player)
    {
        _playerClusterState.Remove(player);
        RefreshPlayerCache();
    }

    public void ForceSpawnAllClusters(Player player)
    {
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

    private void RefreshPlayerCache() =>
        _playerCache = Player.List
            .Where(p => !p.IsDestroyed && !p.IsNpc && !p.IsDummy)
            .ToArray();

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

            if (player == null || player.IsDestroyed)
                continue;

            if (ShouldSkipCulling(player))
                continue;

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
        if (!_playerClusterState.TryGetValue(player, out Dictionary<PrimitiveCluster, bool> states))
        {
            states = new();
            _playerClusterState[player] = states;
        }

        Vector3 playerPos = player.Position;
        foreach (OptimizedSchematic schematic in _schematics)
        {
            if (!schematic?.Schematic)
                continue;

            List<PrimitiveCluster> clusters = schematic.PrimitiveClusters;

            foreach (PrimitiveCluster cluster in clusters)
            {
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

                        if (MerOptimizer.ShouldSpectatorsBeAffectedByPds)
                            UnspawnForSpectators(player, cluster);

                        break;
                    }
                }
            }
        }
    }

    private void SpawnForSpectators(Player target, PrimitiveCluster cluster)
    {
        foreach (Player spectator in target.CurrentSpectators
                     .Where(spectator => !spectator.IsDestroyed && !spectator.IsNpc))
            cluster.SpawnFor(spectator);
    }

    private void UnspawnForSpectators(Player target, PrimitiveCluster cluster)
    {
        foreach (Player spectator in target.CurrentSpectators
                     .Where(spectator => !spectator.IsDestroyed && !spectator.IsNpc))
            cluster.UnspawnFor(spectator);
    }

    private void CleanupDisconnectedPlayers()
    {
        List<Player> toRemove = null;
        foreach (Player player in _playerClusterState.Keys.Where(player => !player.IsDestroyed))
        {
            toRemove ??= [];
            toRemove.Add(player);
        }

        if (toRemove == null)
            return;
        
        foreach (Player player in toRemove)
            _playerClusterState.Remove(player);
    }
}