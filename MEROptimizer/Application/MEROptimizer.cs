using System;
using System.Collections.Generic;
using System.Linq;
using AdminToys;
using AdvancedMERTools.API;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Player;
using MEC;
using MEROptimizer.Application.Components;
using MEROptimizer.MEROptimizer.Application.Components;
using Mirror;
using PlayerRoles;
using ProjectMER.Events.Arguments;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;
using PrimitiveObjectToy = AdminToys.PrimitiveObjectToy;
using Player = LabApi.Features.Wrappers.Player;

namespace MEROptimizer.MEROptimizer.Application;

public class MerOptimizer
{
    public static uint PrimitiveAssetId;

    private bool _excludeCollidables;

    private List<string> _excludedNames;

    private bool _hideDistantPrimitives;

    public static bool ShouldSpectatorsBeAffectedByPds;

    public static bool ShouldTutorialsBeAffectedByDistanceSpawning;

    private float _distanceRequiredForUnspawning;

    private Dictionary<string, float> _customSchematicSpawnDistance = new();

    private float _maxDistanceForPrimitiveCluster;

    private int _maxPrimitivesPerCluster;

    private List<string> _excludedNamesForUnspawningDistantObjects;

    public static float NumberOfPrimitivePerSpawn;

    public static float MinimumSizeBeforeBeingBigPrimitive;

    public static bool IsDynamiclyDisabled = false;

    public static bool OptimizeSpawnedWhileRound;
    public static bool IsDebug = false;

    public List<OptimizedSchematic> OptimizedSchematics = new();

    public void Load(Config config)
    {
        IsDebug = config.Debug;
        _excludeCollidables = config.OptimizeOnlyNonCollidable;

        _excludedNames = new();
        foreach (string name in config.ExcludeObjects)
        {
            _excludedNames.Add(name.ToLower());
        }

        OptimizeSpawnedWhileRound = config.OptimizeSpawnedWhileRound;
        _hideDistantPrimitives = config.ClusterizeSchematic;
        _distanceRequiredForUnspawning = config.SpawnDistance;
        _excludedNamesForUnspawningDistantObjects = config.ExcludeUnspawningDistantObjects;
        _maxDistanceForPrimitiveCluster = config.MaxDistanceForPrimitiveCluster;
        _maxPrimitivesPerCluster = config.MaxPrimitivesPerCluster;
        ShouldSpectatorsBeAffectedByPds = config.ShouldSpectatorBeAffectedByDistanceSpawning;
        NumberOfPrimitivePerSpawn = config.NumberOfPrimitivePerSpawn;
        MinimumSizeBeforeBeingBigPrimitive = config.MinimumSizeBeforeBeingBigPrimitive;
        ShouldTutorialsBeAffectedByDistanceSpawning = config.ShouldTutorialsBeAffectedByDistanceSpawning;
        _customSchematicSpawnDistance = config.CustomSchematicSpawnDistance;

#if EXILED
        Exiled.Events.Handlers.Player.Verified += OnVerified;
        Exiled.Events.Handlers.Player.Spawned += OnSpawned;
        Exiled.Events.Handlers.Player.ChangingSpectatedPlayer += OnChangingSpectatedPlayer;
        Exiled.Events.Handlers.Server.WaitingForPlayers += OnWaitingForPlayers;
#else
            LabApi.Events.Handlers.PlayerEvents.Joined += OnJoined;
            LabApi.Events.Handlers.PlayerEvents.Spawned += OnSpawned;
            LabApi.Events.Handlers.PlayerEvents.ChangedSpectator += OnChangedSpectator;
            LabApi.Events.Handlers.ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
#endif

        ProjectMER.Events.Handlers.Schematic.SchematicSpawned += OnSchematicSpawned;
        ProjectMER.Events.Handlers.Schematic.SchematicDestroyed += OnSchematicDestroyed;
    }

    public void Unload()
    {
#if EXILED
        Exiled.Events.Handlers.Player.Verified -= OnVerified;
        Exiled.Events.Handlers.Player.Spawned -= OnSpawned;
        Exiled.Events.Handlers.Player.ChangingSpectatedPlayer -= OnChangingSpectatedPlayer;
        Exiled.Events.Handlers.Server.WaitingForPlayers -= OnWaitingForPlayers;
#else
            LabApi.Events.Handlers.PlayerEvents.Joined -= OnJoined;
            LabApi.Events.Handlers.PlayerEvents.Spawned -= OnSpawned;
            LabApi.Events.Handlers.PlayerEvents.ChangedSpectator -= OnChangedSpectator;
            LabApi.Events.Handlers.ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
#endif

        ProjectMER.Events.Handlers.Schematic.SchematicSpawned -= OnSchematicSpawned;
        ProjectMER.Events.Handlers.Schematic.SchematicDestroyed -= OnSchematicDestroyed;

        Clear();
    }

    public static void Debug(string message)
    {
        if (!IsDebug) return;

#if EXILED
        Exiled.API.Features.Log.Debug(message);
#else
            Logger.Debug(message);
#endif
    }

    private void Clear()
    {
        OptimizedSchematics.Clear();
    }

    Dictionary<PrimitiveObjectToy, bool> GetPrimitivesToOptimize(Transform parent, List<Transform> parentToExclude,
        Dictionary<PrimitiveObjectToy, bool> primitives = null, bool clusterChilds = true)
    {
        if (primitives == null) primitives = new();

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child == null || parentToExclude.Contains(child)) continue;

            if (child.GetComponent<Rigidbody>() != null)
                continue;

            if (clusterChilds)
            {
                foreach (string name in _excludedNamesForUnspawningDistantObjects)
                {
                    if (child.name.Contains(name))
                    {
                        clusterChilds = false;
                    }
                }
            }

            if (child.TryGetComponent(out PrimitiveObjectToy primitive))
            {
                if (_excludedNames.Any(n => primitive.name.ToLower().Contains(n.ToLower())))
                    continue;

                if (this._excludeCollidables && primitive.PrimitiveFlags.HasFlag(PrimitiveFlags.Collidable))
                    continue;

                if (child.GetComponent<Rigidbody>() != null)
                    continue;

                if (primitive.PrimitiveFlags != PrimitiveFlags.None)
                    primitives.Add(primitive, clusterChilds);
            }

            if (!parentToExclude.Contains(child))
            {
                if (!_excludedNames.Any(n => child.name.ToLower().Contains(n.ToLower())))
                    GetPrimitivesToOptimize(child, parentToExclude, primitives, clusterChilds: clusterChilds);
            }
        }

        return primitives;
    }

    private static bool ShouldPlayerSeeAllClusters(Player player)
    {
        if (player == null) return false;

        var role = player.Role;

        if (role == RoleTypeId.Filmmaker || role == RoleTypeId.Scp079)
            return true;

        if (!ShouldSpectatorsBeAffectedByPds &&
            (role == RoleTypeId.Spectator || role == RoleTypeId.Overwatch))
            return true;

        if (!ShouldTutorialsBeAffectedByDistanceSpawning &&
            role == RoleTypeId.Tutorial)
            return true;

        return false;
    }

    private void SpawnAllClustersForPlayer(Player player)
    {
        foreach (OptimizedSchematic schematic in OptimizedSchematics.Where(s => s?.Schematic != null))
        {
            Debug($"Spawning all clusters of {schematic.Schematic.Name} to {player.DisplayName}");

            foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
            {
                if (cluster.instantSpawn)
                {
                    cluster.SpawnFor(player);
                }
                else
                {
                    cluster.AwaitingSpawn.Remove(player);
                    cluster.AwaitingSpawn.Add(player, cluster.Primitives.ToList());
                    cluster.spawning = true;
                }
            }
        }
    }

    private void UnspawnDistantClustersForPlayer(Player player)
    {
        foreach (OptimizedSchematic schematic in OptimizedSchematics)
        {
            Debug($"Unspawning distant clusters of {schematic.Schematic.Name} for {player.DisplayName}");

            foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
            {
                if (!cluster.InsidePlayers.Contains(player))
                    cluster.UnspawnFor(player);
            }
        }
    }

#if EXILED
    private void OnVerified(VerifiedEventArgs ev)
    {
        OnPlayerJoined(ev.Player);
    }

    private void OnSpawned(SpawnedEventArgs ev)
    {
        OnPlayerSpawned(ev.Player);
    }

    private void OnChangingSpectatedPlayer(ChangingSpectatedPlayerEventArgs ev)
    {
        if (ev.Player == null || ev.NewTarget == null) return;

        Player oldTarget = null;
        if (ev.OldTarget != null) oldTarget = ev.OldTarget;
        OnPlayerChangedSpectator(ev.Player, oldTarget, ev.NewTarget);
    }
#else
        private void OnJoined(PlayerJoinedEventArgs ev)
        {
            OnPlayerJoined(ev.Player);
        }

        private void OnSpawned(PlayerSpawnedEventArgs ev)
        {
            OnPlayerSpawned(ev.Player);
        }

        private void OnChangedSpectator(PlayerChangedSpectatorEventArgs ev)
        {
            OnPlayerChangedSpectator(ev.Player, ev.OldTarget, ev.NewTarget);
        }
#endif

    private void OnWaitingForPlayers()
    {
        Clear();

        if (PrimitiveAssetId != 0) return;

        foreach (GameObject prefab in NetworkClient.prefabs.Values)
        {
            if (prefab.TryGetComponent<PrimitiveObjectToy>(out _))
            {
                PrimitiveAssetId = prefab.GetComponent<NetworkIdentity>().assetId;
                Logger.Debug("PrimitiveObjectToy AssetId successfully found.");
                break;
            }
        }

        if (PrimitiveAssetId == 0)
        {
            Logger.Error("Could not find the PrimitiveObjectToy prefab! Client-side primitives will fail to spawn.");
        }
    }

    private void AddPlayerTrigger(Player player)
    {
        Debug($"Adding PlayerTrigger to {player.DisplayName}({player.PlayerId}) !");
        GameObject playerTrigger = new($"{player.PlayerId}_MERO_TRIGGER");
        playerTrigger.tag = "Player";

        Rigidbody rb = playerTrigger.AddComponent<Rigidbody>();
        rb.isKinematic = true;

        playerTrigger.AddComponent<BoxCollider>().size = new(1, 2, 1);

        playerTrigger.AddComponent<PlayerTrigger>().Player = player;
    }

    private void OnPlayerJoined(Player player)
    {
        if (player == null || player.IsNpc) return;

        AddPlayerTrigger(player);
        foreach (OptimizedSchematic schematic in OptimizedSchematics.Where(s => s != null && s.Schematic != null))
        {
            Debug($"Displaying static client sided primitives of {schematic.Schematic.Name} to {player.DisplayName} because he just connected !");
            schematic.SpawnClientPrimitives(player);
        }
    }

    private void OnPlayerSpawned(Player player)
    {
        if (player == null) return;

        if (player.IsNpc)
        {
            bool hasFound = false;

            for (int i = 0; i < player.GameObject.transform.childCount; i++)
            {
                Transform child = player.GameObject.transform.GetChild(i);
                if (child != null && child.name == $"{player.PlayerId}_MERO_TRIGGER")
                {
                    hasFound = true;
                    break;
                }
            }

            if (!hasFound)
                AddPlayerTrigger(player);

            return;
        }

        if (ShouldPlayerSeeAllClusters(player))
        {
            Timing.CallDelayed(.5f, () =>
            {
                if (player == null) return;
                if (!ShouldPlayerSeeAllClusters(player)) return;
                SpawnAllClustersForPlayer(player);
            });
        }
        else
        {
            UnspawnDistantClustersForPlayer(player);
        }
    }

    private void OnPlayerChangedSpectator(Player player, Player oldTarget, Player newTarget)
    {
        if (!ShouldSpectatorsBeAffectedByPds) return;

        if (player == null || player.IsNpc || newTarget == null) return;

        foreach (OptimizedSchematic schematic in OptimizedSchematics)
        {
            foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
            {
                if (oldTarget != null && (cluster.InsidePlayers.Contains(oldTarget) && !cluster.InsidePlayers.Contains(newTarget)))
                    cluster.UnspawnFor(player);

                if (cluster.InsidePlayers.Contains(newTarget) && (oldTarget == null || !cluster.InsidePlayers.Contains(oldTarget)))
                    cluster.SpawnFor(player);
            }
        }
    }

    private void OnSchematicSpawned(SchematicSpawnedEventArgs ev)
    {
        if (IsDynamiclyDisabled)
        {
            Logger.Warn($"Skipping the optimisation of {ev.Schematic.name} because the plugin is dynamically disabled by command (mero.disable)");
            return;
        }

        if (!OptimizeSpawnedWhileRound && !ev.IsEventBased)
        {
            Log.Warn($"Skipping the optimisation of {ev.Schematic.name} because it is spawned manually");
            return;
        }

        if (ev.Schematic == null) return;

        if (_excludedNames.Any(n => ev.Schematic.Name.ToLower().Contains(n)))
            return;

        Log.Debug($"MERO: SchematicSpawned received for {ev.Schematic.Name}, scheduling optimization in 0.15 seconds");
        Timing.CallDelayed(0.15f, () => ProcessSchematicOptimization(ev));
    }

    private void ProcessSchematicOptimization(SchematicSpawnedEventArgs ev)
    {
        if (ev.Schematic == null)
        {
            Log.Warn($"MERO: Schematic is null, skipping optimization");
            return;
        }

        Log.Debug($"MERO: Starting optimization for {ev.Schematic.Name}");

        List<Transform> parentsToExclude = new();

        foreach (Animator anim in ev.Schematic.GetComponentsInChildren<Animator>())
        {
            if (anim == null)
                continue;

            parentsToExclude.Add(anim.transform);
            Log.Debug($"MERO: Excluding animator: {anim.gameObject.name}");
        }

        foreach (AMERTInteractable amert in ev.Schematic.GetComponentsInChildren<AMERTInteractable>())
        {
            if (amert == null)
                continue;

            if (!parentsToExclude.Contains(amert.transform))
                parentsToExclude.Add(amert.transform);
        }

        Dictionary<PrimitiveObjectToy, bool> primitivesToOptimize = GetPrimitivesToOptimize(ev.Schematic.transform, parentsToExclude);

        if (primitivesToOptimize == null || primitivesToOptimize.IsEmpty()) return;

        Dictionary<ClientSidePrimitive, bool> clientSidePrimitive = new();

        List<Collider> serverSideColliders = new();

        List<PrimitiveObjectToy> primitivesToDestroy = new();

        foreach (PrimitiveObjectToy primitive in primitivesToOptimize.Keys.ToList())
        {
            Vector3 position = primitive.transform.position;
            Quaternion rotation = primitive.transform.rotation;
            Vector3 scale = primitive.transform.lossyScale;
            PrimitiveType primitiveType = primitive.PrimitiveType;
            Color color = primitive.NetworkMaterialColor;
            PrimitiveFlags primitiveFlags = primitive.PrimitiveFlags;

            clientSidePrimitive.Add(new ClientSidePrimitive(position, rotation, scale, primitiveType, color, primitiveFlags), primitivesToOptimize[primitive]);

            if (primitiveFlags.HasFlag(PrimitiveFlags.Collidable))
            {
                GameObject collider = new();
                collider.transform.localScale = new(Math.Abs(scale.x), Math.Abs(scale.y), Math.Abs(scale.z));
                collider.transform.position = position;
                collider.transform.rotation = rotation;
                collider.transform.name = $"[MEROCOLLIDER] {primitive.transform.name}";

                collider.gameObject.layer = (color.a < 1 ? LayerMask.NameToLayer("Glass") : 0);

                MeshCollider meshCollider = collider.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = PrimitiveObjectToy.PrimitiveTypeToMesh[primitiveType];

                if (meshCollider != null) serverSideColliders.Add(meshCollider);
                else UnityEngine.Object.Destroy(collider);
            }

            primitivesToDestroy.Add(primitive);
        }

        float distanceForClusterSpawn = _distanceRequiredForUnspawning;

        if (_customSchematicSpawnDistance.TryGetValue(ev.Schematic.Name, out float customDistance))
        {
            distanceForClusterSpawn = customDistance;
        }

        OptimizedSchematic schematic = new OptimizedSchematic(ev.Schematic, serverSideColliders, clientSidePrimitive,
            _hideDistantPrimitives, distanceForClusterSpawn, _excludedNamesForUnspawningDistantObjects,
            _maxDistanceForPrimitiveCluster, _maxPrimitivesPerCluster);

        OptimizedSchematics.Add(schematic);

        if (ev.Schematic == null) return;

        foreach (PrimitiveObjectToy primitive in primitivesToDestroy)
        {
            if (primitive == null) continue;

            try
            {
                GameObject.Destroy(primitive.gameObject);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error destroying primitive: {ex.Message}");
            }
        }

        Timing.CallDelayed(1f, () =>
        {
            if (ev.Schematic == null || schematic == null) return;
            schematic.SchematicServerSidePrimitiveCount = ev.Schematic.GetComponentsInChildren<PrimitiveObjectToy>().Count(p => p != null);
            schematic.SchematicServerSidePrimitiveEmptiesCount = ev.Schematic.GetComponentsInChildren<PrimitiveObjectToy>().Count(p => p != null && p.PrimitiveFlags == PrimitiveFlags.None);
        });
    }

    private void OnSchematicDestroyed(SchematicDestroyedEventArgs ev)
    {
        foreach (OptimizedSchematic optimizedSchematic in OptimizedSchematics.Where(s => s != null).ToList())
        {
            if (optimizedSchematic.Schematic == null || optimizedSchematic.Schematic == ev.Schematic)
            {
                optimizedSchematic.Destroy();
                OptimizedSchematics.Remove(optimizedSchematic);
            }
        }
    }
}