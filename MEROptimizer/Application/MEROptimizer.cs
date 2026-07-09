using System;
using System.Collections.Generic;
using System.Linq;
using AdminToys;
using AdvancedMERTools.API.Core;
using AdvancedMERTools.Components;
using AdvancedMERTools.Events.EventArgs;
using AdvancedMERTools.Events.Handlers;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Player;
using MEC;
using MEROptimizer.MEROptimizer.Application.Components;
using Mirror;
using PlayerRoles;
using ProjectMER.Events.Arguments;
using ProjectMER.Events.Handlers;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;
using Object = UnityEngine.Object;
using PrimitiveObjectToy = AdminToys.PrimitiveObjectToy;
using Player = LabApi.Features.Wrappers.Player;
using Server = Exiled.Events.Handlers.Server;

namespace MEROptimizer.MEROptimizer.Application;

public class MerOptimizer
{
    public static uint PrimitiveAssetId;

    private bool _excludeCollidables;

    private List<string> _excludedNames;

    private bool _hideDistantPrimitives;

    public static bool ShouldSpectatorsBeAffectedByPds;

    public static bool ShouldTutorialsBeAffectedByDistanceSpawning;

    public float _distanceRequiredForUnspawning;
    public float _unspawnHysteresisMultiplier;

    private Dictionary<string, float> _customSchematicSpawnDistance = new();

    private float _maxDistanceForPrimitiveCluster;

    private int _maxPrimitivesPerCluster;

    private List<string> _excludedNamesForUnspawningDistantObjects;

    public static float NumberOfPrimitivePerSpawn;
    public static bool ShouldSpectatorsSeeNothing;

    public static float MinimumSizeBeforeBeingBigPrimitive;

    public static bool IsDynamiclyDisabled = false;
    
    public static bool IsDebug;

    public List<OptimizedSchematic> OptimizedSchematics = [];

    private GameObject _cullingManagerObject;

    public void Load(Config config)
    {
        IsDebug = config.Debug;
        _excludeCollidables = config.OptimizeOnlyNonCollidable;

        _excludedNames = [];
        if (config.ExcludeObjects != null)
        {
            foreach (string name in config.ExcludeObjects.Where(name => !string.IsNullOrWhiteSpace(name)))
                _excludedNames.Add(name.ToLowerInvariant());
        }
        
        _hideDistantPrimitives = config.ClusterizeSchematic;
        _distanceRequiredForUnspawning = config.SpawnDistance;
        _unspawnHysteresisMultiplier = config.UnspawnHysteresisMultiplier;
        ShouldSpectatorsSeeNothing = config.ShouldSpectatorsEvenSeeOptimized;

        _excludedNamesForUnspawningDistantObjects = [];
        if (config.ExcludeUnspawningDistantObjects != null)
        {
            foreach (string name in config.ExcludeUnspawningDistantObjects.Where(name => !string.IsNullOrWhiteSpace(name)))
                _excludedNamesForUnspawningDistantObjects.Add(name);
        }

        _maxDistanceForPrimitiveCluster = config.MaxDistanceForPrimitiveCluster;
        _maxPrimitivesPerCluster = config.MaxPrimitivesPerCluster;
        ShouldSpectatorsBeAffectedByPds = config.ShouldSpectatorBeAffectedByDistanceSpawning;
        NumberOfPrimitivePerSpawn = config.NumberOfPrimitivePerSpawn;
        MinimumSizeBeforeBeingBigPrimitive = config.MinimumSizeBeforeBeingBigPrimitive;
        ShouldTutorialsBeAffectedByDistanceSpawning = config.ShouldTutorialsBeAffectedByDistanceSpawning;
        _customSchematicSpawnDistance = config.CustomSchematicSpawnDistance ?? new();

        Exiled.Events.Handlers.Player.Verified += OnVerified;
        Exiled.Events.Handlers.Player.Spawned += OnSpawned;
        Exiled.Events.Handlers.Player.ChangingSpectatedPlayer += OnChangingSpectatedPlayer;
        Exiled.Events.Handlers.Player.Left += OnPlayerLeft;
        Server.WaitingForPlayers += OnWaitingForPlayers;

        Schematic.SchematicSpawned += OnSchematicSpawned;
        Schematic.SchematicDestroyed += OnSchematicDestroyed;
        AmertHandlers.HealthObjectDead += OnHealthObjectDead;
    }

    public void Unload()
    {
        Exiled.Events.Handlers.Player.Verified -= OnVerified;
        Exiled.Events.Handlers.Player.Spawned -= OnSpawned;
        Exiled.Events.Handlers.Player.ChangingSpectatedPlayer -= OnChangingSpectatedPlayer;
        Exiled.Events.Handlers.Player.Left -= OnPlayerLeft;
        Server.WaitingForPlayers -= OnWaitingForPlayers;

        Schematic.SchematicSpawned -= OnSchematicSpawned;
        Schematic.SchematicDestroyed -= OnSchematicDestroyed;
        AmertHandlers.HealthObjectDead -= OnHealthObjectDead;

        Clear();
    }

    public static void Debug(string message)
    {
        if (!IsDebug)
            return;

        Log.Debug(message);
    }

    private void Clear()
    {
        foreach (OptimizedSchematic schematic in OptimizedSchematics.Where(s => s != null))
        {
            if (!schematic.Schematic)
                continue;
            
            schematic?.Destroy();
        }

        OptimizedSchematics.Clear();

        if (_cullingManagerObject != null)
        {
            Object.Destroy(_cullingManagerObject);
            _cullingManagerObject = null;
        }
    }

    private Dictionary<PrimitiveObjectToy, bool> GetPrimitivesToOptimize(
        Transform parent,
        List<Transform> parentToExclude,
        Dictionary<PrimitiveObjectToy, bool> primitives = null,
        bool clusterChilds = true)
    {
        primitives ??= new();

        if (parentToExclude.Contains(parent))
            return primitives;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child == null || parentToExclude.Contains(child))
                continue;

            if (child.GetComponent<Rigidbody>() != null)
                continue;
            
            bool childClusterChilds = clusterChilds;
            if (childClusterChilds && _excludedNamesForUnspawningDistantObjects is { Count: > 0 })
            {
                foreach (string name in _excludedNamesForUnspawningDistantObjects)
                {
                    if (string.IsNullOrEmpty(name))
                        continue;

                    if (child.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    childClusterChilds = false;
                    break;
                }
            }

            string childLower = child.name.ToLowerInvariant();
            if (child.TryGetComponent(out PrimitiveObjectToy primitive))
            {
                string primLower = primitive.name.ToLowerInvariant();
                if (_excludedNames.Count > 0 && _excludedNames.Any(n => primLower.Contains(n)))
                    continue;

                if (_excludeCollidables && primitive.PrimitiveFlags.HasFlag(PrimitiveFlags.Collidable))
                    continue;

                primitives.Add(primitive, childClusterChilds);
            }

            if (parentToExclude.Contains(child))
                continue;
            
            if (_excludedNames.Count == 0 || !_excludedNames.Any(n => childLower.Contains(n)))
                GetPrimitivesToOptimize(child, parentToExclude, primitives, childClusterChilds);
        }

        return primitives;
    }

    private static bool ShouldPlayerSeeAllClusters(Player player)
    {
        if (player == null) return false;

        RoleTypeId role = player.Role;

        if (role == RoleTypeId.Filmmaker || role == RoleTypeId.Scp079)
            return true;

        if (MerOptimizer.ShouldSpectatorsSeeNothing &&
            (role == RoleTypeId.Spectator || role == RoleTypeId.Overwatch))
            return false;
        
        if (!ShouldSpectatorsBeAffectedByPds &&
            (role == RoleTypeId.Spectator || role == RoleTypeId.Overwatch))
            return true;

        if (!ShouldTutorialsBeAffectedByDistanceSpawning &&
            role == RoleTypeId.Tutorial)
            return true;

        return false;
    }

    private void OnVerified(VerifiedEventArgs ev) => OnPlayerJoined(ev.Player);

    private void OnSpawned(SpawnedEventArgs ev) => OnPlayerSpawned(ev.Player);

    private void OnChangingSpectatedPlayer(ChangingSpectatedPlayerEventArgs ev)
    {
        if (ev.Player == null || ev.NewTarget == null)
            return;

        OnPlayerChangedSpectator(ev.Player, ev.NewTarget);
    }

    private void OnPlayerLeft(LeftEventArgs ev)
    {
        DistanceCullingManager.Instance?.OnPlayerLeft(ev.Player);
    }

    private void OnWaitingForPlayers()
    {
        Clear();

        _cullingManagerObject = new("[MERO] DistanceCullingManager");
        _cullingManagerObject.AddComponent<DistanceCullingManager>();

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
            Logger.Error("Could not find the PrimitiveObjectToy prefab! Client-side primitives will fail to spawn.");
    }

    private void OnPlayerJoined(Player player)
    {
        if (player.IsDestroyed || player.IsHost || player.IsNpc) 
            return;

        DistanceCullingManager.Instance?.OnPlayerJoined(player);

        foreach (OptimizedSchematic schematic in OptimizedSchematics.Where(s => s != null && s.Schematic != null))
        {
            Debug($"Displaying static client sided primitives of {schematic.Schematic.Name} to {player.DisplayName} because he just connected !");

            schematic.SpawnClientPrimitives(player);
        }
    }

    private void OnPlayerSpawned(Player player)
    {
        if (player.IsDestroyed || player.IsHost || player.IsNpc) 
            return;

        Debug($"[SPAWN] {player.DisplayName} role={player.Role} ShouldSeeAll={ShouldPlayerSeeAllClusters(player)}");
        Debug($"[SPAWN] OptimizedSchematics count={OptimizedSchematics.Count}");
        Debug($"[SPAWN] DistanceCullingManager exists={DistanceCullingManager.Instance != null}");

        if (ShouldPlayerSeeAllClusters(player))
        {
            Timing.CallDelayed(.5f, () =>
            {
                if (player == null) return;
                if (!ShouldPlayerSeeAllClusters(player)) return;
                Debug($"[SPAWN] ForceSpawnAllClusters for {player.DisplayName}");
                DistanceCullingManager.Instance?.ForceSpawnAllClusters(player);
            });
        }
        else
        {
            Debug($"[SPAWN] ForceUnspawnDistantClusters for {player.DisplayName}");
            DistanceCullingManager.Instance?.ForceUnspawnDistantClusters(player);
        }
    }

    private void OnPlayerChangedSpectator(Player player, Player newTarget)
    {
        if (MerOptimizer.ShouldSpectatorsSeeNothing) return;
        if (!ShouldSpectatorsBeAffectedByPds) return;
        if (player == null || player.IsNpc || newTarget == null) return;

        foreach (OptimizedSchematic schematic in OptimizedSchematics)
        {
            foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
            {
                bool newTargetInside = DistanceCullingManager.Instance != null &&
                                       DistanceCullingManager.Instance.IsPlayerInsideCluster(newTarget, cluster);

                if (!newTargetInside)
                    continue;

                if (DistanceCullingManager.Instance != null && 
                    !DistanceCullingManager.Instance.IsPlayerInsideCluster(player, cluster))
                {
                    cluster.EnqueueSlowSpawn(player);
                    DistanceCullingManager.Instance.SetSpectatorClusterState(player, cluster, true);
                }
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

        if (!ev.ShouldBeOptimized)
        {
            Log.Warn($"Skipping the optimisation of {ev.Schematic.name} because it is spawned manually");
            return;
        }

        if (ev.Schematic == null) return;

        if (_excludedNames.Any(n => ev.Schematic.Name.ToLower().Contains(n)))
            return;

        Log.Debug($"MERO: SchematicSpawned received for {ev.Schematic.Name}, scheduling optimization");
        Timing.CallDelayed(0.15f, () => ProcessSchematicOptimization(ev));
    }

        private void ProcessSchematicOptimization(SchematicSpawnedEventArgs ev)
        {
            if (ev.Schematic == null)
            {
                Log.Warn("MERO: Schematic is null, skipping optimization");
                return;
            }

            Log.Debug($"MERO: Starting optimization for {ev.Schematic.Name}");

            List<Transform> parentsToExclude = [];
            
            foreach (Animator anim in ev.Schematic.GetComponentsInChildren<Animator>())
            {
                if (anim == null || !anim.enabled || anim.runtimeAnimatorController == null)
                    continue;
                    
                parentsToExclude.Add(anim.transform);
            }

            Dictionary<PrimitiveObjectToy, bool> primitivesToOptimize =
                GetPrimitivesToOptimize(ev.Schematic.transform, parentsToExclude);

            if (primitivesToOptimize == null || primitivesToOptimize.IsEmpty()) return;

            Dictionary<ClientSidePrimitive, bool> clientSidePrimitive = new();
            List<Collider> serverSideColliders = [];
            
            List<PrimitiveObjectToy> primitivesToDestroy = [];
            List<PrimitiveObjectToy> primitivesToSoftDestroy = [];

            foreach (PrimitiveObjectToy primitive in primitivesToOptimize.Keys.ToList())
            {
                if (primitive.PrimitiveFlags == PrimitiveFlags.None)
                {
                    if (primitive.transform.childCount > 0)
                        primitivesToSoftDestroy.Add(primitive);
                    else
                        primitivesToDestroy.Add(primitive);
                        
                    continue;
                }

                Vector3 position = primitive.transform.position;
                Quaternion rotation = primitive.transform.rotation;
                Vector3 scale = primitive.transform.lossyScale;
                PrimitiveType primitiveType = primitive.PrimitiveType;
                Color color = primitive.NetworkMaterialColor;
                PrimitiveFlags primitiveFlags = primitive.PrimitiveFlags;
                string sourceName = primitive.name;

                clientSidePrimitive.Add(
                    new(position, rotation, scale, primitiveType, color, primitiveFlags, sourceName, primitive.transform),
                    primitivesToOptimize[primitive]);

                if (primitiveFlags.HasFlag(PrimitiveFlags.Collidable))
                {
                    Vector3 absScale = new(Math.Abs(scale.x), Math.Abs(scale.y), Math.Abs(scale.z));

                    GameObject colliderGo = new($"[MEROCOLLIDER] {primitive.transform.name}") { transform =
                    {
                        position = position, rotation = rotation, localScale = absScale
                    } };

                    int glassLayer = LayerMask.NameToLayer("Default");
                    colliderGo.layer = color.a < 1f && glassLayer >= 0 ? glassLayer : 0;

                    Collider col = CreateBestFitCollider(primitiveType, colliderGo);
                    if (col != null)
                        serverSideColliders.Add(col);
                    else
                        Object.Destroy(colliderGo);
                }

                bool isAmertObject = IsUnderAmert(primitive.transform);
                if (isAmertObject)
                {
                    primitivesToSoftDestroy.Add(primitive);
                }
                else
                {
                    primitivesToDestroy.Add(primitive);
                }
            }

            float distanceForClusterSpawn = _distanceRequiredForUnspawning;
            if (_customSchematicSpawnDistance.TryGetValue(ev.Schematic.Name, out float customDistance))
                distanceForClusterSpawn = customDistance;

            OptimizedSchematic schematic = new(ev.Schematic, serverSideColliders, clientSidePrimitive,
                _hideDistantPrimitives, distanceForClusterSpawn, _excludedNamesForUnspawningDistantObjects,
                _maxDistanceForPrimitiveCluster, _maxPrimitivesPerCluster);

            OptimizedSchematics.Add(schematic);

            if (ev.Schematic == null) return;
            
            foreach (PrimitiveObjectToy primitive in primitivesToDestroy)
            {
                if (primitive == null) continue;
                try
                {
                    NetworkServer.Destroy(primitive.gameObject);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Error destroying primitive: {ex.Message}");
                }
            }
            
            foreach (PrimitiveObjectToy primitive in primitivesToSoftDestroy)
            {
                if (primitive == null) continue;
                try
                {
                    NetworkServer.UnSpawn(primitive.gameObject);
                    Object.Destroy(primitive.GetComponent<NetworkIdentity>());
                    primitive.enabled = false; 
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Error soft-destroying AMERT primitive: {ex.Message}");
                }
            }

            Timing.CallDelayed(1f, () =>
            {
                if (ev.Schematic == null || schematic == null) return;
                schematic.SchematicServerSidePrimitiveCount =
                    ev.Schematic.GetComponentsInChildren<PrimitiveObjectToy>().Count(p => p != null);

                schematic.SchematicServerSidePrimitiveEmptiesCount = ev.Schematic
                    .GetComponentsInChildren<PrimitiveObjectToy>()
                    .Count(p => p != null && p.PrimitiveFlags == PrimitiveFlags.None);
            });
        }
        
        private void OnHealthObjectDead(HealthObjectDeadEventArgs ev)
        {
            if (ev.HealthObject == null) return;
            HealthObject ho = ev.HealthObject;

            if (ho.Base.DestroyEntireSchematicOnDisappear && ho.OSchematic != null)
            {
                foreach (OptimizedSchematic os in OptimizedSchematics.Where(s => s != null && s.Schematic == ho.OSchematic).ToList())
                {
                    os.Destroy();
                    OptimizedSchematics.Remove(os);
                }
                return;
            }

            foreach (OptimizedSchematic os in OptimizedSchematics.Where(s => s != null && s.Schematic == ho.OSchematic))
            {
                os.RemovePrimitivesUnderTransform(ho.transform);
            }
        }

        private bool IsUnderAmert(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                if (current.GetComponent<AMERTInteractable>() != null)
                    return true;
                
                current = current.parent;
            }
            return false;
        }

        private static Collider CreateBestFitCollider(PrimitiveType primitiveType, GameObject colliderGo)
        {
            Rigidbody rb = colliderGo.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeAll;

            switch (primitiveType)
            {
                case PrimitiveType.Cube:
                    return colliderGo.AddComponent<BoxCollider>();

                case PrimitiveType.Sphere:
                    return colliderGo.AddComponent<SphereCollider>();

                case PrimitiveType.Capsule:
                case PrimitiveType.Cylinder:
                    CapsuleCollider cc = colliderGo.AddComponent<CapsuleCollider>();
                    cc.direction = 1;
                    return cc;

                case PrimitiveType.Quad:
                    BoxCollider bc = colliderGo.AddComponent<BoxCollider>();
                    bc.size = new(1f, 1f, 0.05f);
                    return bc;

                case PrimitiveType.Plane: 
                    bc = colliderGo.AddComponent<BoxCollider>();
                    bc.size = new(10f, 0.05f, 10f);
                    return bc;

                default:
                    MeshCollider mc = colliderGo.AddComponent<MeshCollider>();
                    mc.convex = true;
                    return mc;
            }
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