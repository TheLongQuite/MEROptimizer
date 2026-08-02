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
using ProjectMER.Features.Components;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;
using Object = UnityEngine.Object;
using PrimitiveObjectToy = AdminToys.PrimitiveObjectToy;
using Player = LabApi.Features.Wrappers.Player;
using Server = Exiled.Events.Handlers.Server;
using Newtonsoft.Json;
using ProjectMER.Events.Handlers;

namespace MEROptimizer.MEROptimizer.Application;

public class MerOptimizer
{
    public static uint PrimitiveAssetId;

    private const string DiagSchematicName = "HCZ_Sklad_Tech";
    private const int DiagSampleLimit = 8;

    private bool _excludeCollidables;

    private HashSet<string> _excludedNames;

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

    public static bool AnimOptimizationEnabled;

    public List<OptimizedSchematic> OptimizedSchematics = [];

    private GameObject _cullingManagerObject;

    public void Load(Config config)
    {
        IsDebug = config.Debug;
        _excludeCollidables = config.OptimizeOnlyNonCollidable;

        _excludedNames = [];
        if (config.ExcludeObjects != null)
        {
            foreach (string name in config.ExcludeObjects)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    _excludedNames.Add(name.ToLowerInvariant());
            }
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

        AnimOptimizationEnabled = config.AnimOptimizationEnabled;

        Exiled.Events.Handlers.Player.Verified += OnVerified;
        Exiled.Events.Handlers.Player.Spawned += OnSpawned;
        Exiled.Events.Handlers.Player.ChangingSpectatedPlayer += OnChangingSpectatedPlayer;
        Exiled.Events.Handlers.Player.Left += OnPlayerLeft;
        Server.WaitingForPlayers += OnWaitingForPlayers;

        Schematic.SchematicSpawned += OnSchematicSpawned;
        Schematic.SchematicDestroyed += OnSchematicDestroyed;
        AmertHandlers.AmertDisappeared += OnAmertDisappeared;
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
        AmertHandlers.AmertDisappeared -= OnAmertDisappeared;

        Clear();
    }

    public static void Debug(string message)
    {
        if (!IsDebug)
            return;

        Log.Debug(message);
    }

    private static string GetHierarchyPath(Transform target, Transform root)
    {
        if (target == null)
            return "<null>";

        List<string> parts = new();
        Transform current = target;
        while (current != null && current != root)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        if (root != null)
            parts.Add(root.name);

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string FormatSample(IEnumerable<string> items, int limit)
    {
        List<string> list = items.ToList();
        string joined = string.Join(", ", list.Take(limit));
        return list.Count > limit ? $"{joined}, ... (+{list.Count - limit} ещё)" : joined;
    }

    private static bool IsIntentionallyExcludedFromFreeze(Transform primTransform, Transform schematicRoot)
    {
        if (primTransform.childCount > 0)
            return true;

        if (primTransform.GetComponent<AMERTInteractable>() != null)
            return true;

        Transform current = primTransform.parent;
        while (current != null && current != schematicRoot)
        {
            if (current.GetComponent<HealthObject>() != null)
                return true;

            current = current.parent;
        }

        return false;
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
        HashSet<Transform> parentToExclude,
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
                if (_excludedNames.Contains(primLower))
                    continue;

                if (_excludeCollidables && primitive.PrimitiveFlags.HasFlag(PrimitiveFlags.Collidable))
                    continue;

                primitives.Add(primitive, childClusterChilds);
            }

            if (!_excludedNames.Contains(childLower))
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
            Log.Warn($"Skipping the optimisation of {ev.Schematic.name} because the plugin is dynamically disabled by command (mero.disable)");
            return;
        }

        if (!ev.ShouldBeOptimized)
        {
            Log.Warn($"Skipping the optimisation of {ev.Schematic.name} because it is spawned manually");
            return;
        }
        
        if (_excludedNames.Contains(ev.Schematic.Name.ToLowerInvariant()))
            return;

        Log.Debug($"MERO: SchematicSpawned received for {ev.Schematic.Name}, scheduling optimization");
        Timing.CallDelayed(0.15f, () => ProcessSchematicOptimization(ev));
    }

    private void ProcessSchematicOptimization(SchematicSpawnedEventArgs ev)
    {
        Log.Debug($"MERO: Starting optimization for {ev.Schematic.Name}");

        bool diag = ev.Schematic.Name == DiagSchematicName;

        if (diag)
            Log.Debug($"[MRPO-DIAG] ===== '{ev.Schematic.Name}': начало оптимизации =====");

        HashSet<Transform> parentsToExclude = new();
        HashSet<Transform> nonAnimatorParentsToExclude = new();
        HashSet<Transform> animGroupExclude = new();
        
        foreach (Animator anim in ev.Schematic.GetComponentsInChildren<Animator>(true))
        {
            if (anim == null || !anim.enabled || anim.runtimeAnimatorController == null)
                continue;

            Transform animTransform = anim.transform;

            parentsToExclude.Add(animTransform);
            animGroupExclude.Add(animTransform);
        }
        
        foreach (Rigidbody rb in ev.Schematic.GetComponentsInChildren<Rigidbody>(true))
        {
            if (diag)
                Log.Debug($"[MRPO-DIAG] Rigidbody на '{GetHierarchyPath(rb.transform, ev.Schematic.transform)}' isKinematic={rb.isKinematic}");

            Transform rbTransform = rb.transform;

            parentsToExclude.Add(rbTransform);

            if (!rb.isKinematic)
            {
                nonAnimatorParentsToExclude.Add(rbTransform);
                animGroupExclude.Add(rbTransform);
            }
        }
        
        foreach (PrimitiveObjectToy primitive in ev.Schematic.GetComponentsInChildren<PrimitiveObjectToy>(true))
        {
            string primLower = primitive.name.ToLowerInvariant();

            bool skip = _excludedNames.Contains(primLower) ||
                        (_excludeCollidables && primitive.PrimitiveFlags.HasFlag(PrimitiveFlags.Collidable));

            if (!skip)
                continue;

            Transform primTransform = primitive.transform;

            parentsToExclude.Add(primTransform);
            nonAnimatorParentsToExclude.Add(primTransform);
            animGroupExclude.Add(primTransform);
        }

        Dictionary<PrimitiveObjectToy, bool> primitivesToOptimize =
            GetPrimitivesToOptimize(ev.Schematic.transform, parentsToExclude);

        if (diag)
        {
            int totalPrims = ev.Schematic.GetComponentsInChildren<PrimitiveObjectToy>(true).Length;
            int animatorCount = ev.Schematic.GetComponentsInChildren<Animator>(true).Length;
            int amertCount = ev.Schematic.GetComponentsInChildren<AMERTInteractable>(true).Length;

            Log.Debug($"[MRPO-DIAG] Всего примитивов в схематике: {totalPrims} | аниматоров: {animatorCount} | AMERT компонентов: {amertCount}");
            Log.Debug($"[MRPO-DIAG] Общий пул на оптимизацию (вне аниматоров): {primitivesToOptimize?.Count ?? 0}");
        }

        foreach (AdminToyBase toy in ev.Schematic.GetComponentsInChildren<AdminToyBase>(true))
        {
            if (toy is PrimitiveObjectToy prim && primitivesToOptimize.ContainsKey(prim))
                continue;

            bool isExclude = false;
            Transform current = toy.transform;
            while (current != null && current != ev.Schematic.transform)
            {
                if (parentsToExclude.Contains(current))
                {
                    isExclude = true;
                    break;
                }
                
                current = current.parent;
            }

            if (isExclude || toy.GetComponent<Animator>() != null)
                continue;

            toy.NetworkIsStatic = true;
        }
        
        if (primitivesToOptimize == null || primitivesToOptimize.IsEmpty()) 
            return;

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
            bool hasChildren = primitive.transform.childCount > 0;
            if (isAmertObject || hasChildren)
            {
                primitivesToSoftDestroy.Add(primitive);
            }
            else
            {
                primitivesToDestroy.Add(primitive);
            }
        }

        if (diag)
            Log.Debug($"[MRPO-DIAG] Вне аниматоров: hard-destroy={primitivesToDestroy.Count}, soft-destroy={primitivesToSoftDestroy.Count}, client-side создано={clientSidePrimitive.Count}");

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
            if (primitive == null) 
                continue;
            
            try
            {
                NetworkServer.UnSpawn(primitive.gameObject);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error soft-destroying AMERT primitive: {ex.Message}");
            }
        }

        if (AnimOptimizationEnabled)
        {
            Dictionary<string, List<string>> animStatesDict = new Dictionary<string, List<string>>();
            string animStatesPath = System.IO.Path.Combine(ev.Schematic.DirectoryPath, ev.Schematic.Name + "-AnimStates.json");
            if (System.IO.File.Exists(animStatesPath))
            {
                try
                {
                    animStatesDict = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(System.IO.File.ReadAllText(animStatesPath)) ?? new();
                }
                catch
                {
                    animStatesDict = new();
                }
            }
            else
            {
                Debug($"MERO: '{ev.Schematic.Name}-AnimStates.json' not found — no animator in this schematic will be optimized " +
                      "(this is normal unless you added an 'AnimatedStateOptimizer' component to one of its animators).");

                if (diag)
                    Log.Debug($"[MRPO-DIAG] Файл '{ev.Schematic.Name}-AnimStates.json' НЕ НАЙДЕН ('{animStatesPath}'). Все аниматоры схематика останутся неоптимизированными.");
            }

            if (diag)
                Log.Debug($"[MRPO-DIAG] Ключи в AnimStates.json ({animStatesDict.Count}): [{FormatSample(animStatesDict.Keys, DiagSampleLimit)}]");

            ProcessAnimators(ev.Schematic, schematic, animStatesDict, animGroupExclude, nonAnimatorParentsToExclude, diag);

            if (diag)
            {
                HashSet<Transform> capturedAnimPrimitives = new();
                foreach (AnimatedPrimitiveGroup group in schematic.AnimatedPrimitiveGroups)
                {
                    foreach (PrimitiveObjectToy p in group.Primitives)
                    {
                        if (p != null)
                            capturedAnimPrimitives.Add(p.transform);
                    }
                }

                Dictionary<Transform, List<string>> orphansByAnimator = new();
                foreach (PrimitiveObjectToy p in ev.Schematic.GetComponentsInChildren<PrimitiveObjectToy>(true))
                {
                    if (p == null) continue;

                    Transform nearestAnimAncestor = null;
                    Transform cur = p.transform.parent;
                    while (cur != null && cur != ev.Schematic.transform)
                    {
                        if (cur.GetComponent<Animator>() != null) { nearestAnimAncestor = cur; break; }
                        cur = cur.parent;
                    }

                    if (nearestAnimAncestor == null)
                        continue;

                    if (capturedAnimPrimitives.Contains(p.transform))
                        continue;

                    if (IsIntentionallyExcludedFromFreeze(p.transform, ev.Schematic.transform))
                        continue;

                    if (!orphansByAnimator.TryGetValue(nearestAnimAncestor, out List<string> list))
                    {
                        list = new();
                        orphansByAnimator[nearestAnimAncestor] = list;
                    }
                    list.Add(GetHierarchyPath(p.transform, ev.Schematic.transform));
                }

                int totalOrphans = orphansByAnimator.Values.Sum(l => l.Count);

                if (totalOrphans > 0)
                {
                    Log.Debug($"[MRPO-DIAG] !!! ИТОГО {totalOrphans} примитив(ов) под аниматорами НИКОГДА не будут оптимизированы (нет записи в AnimStates.json, не совпал unityPath, или блокирует не-кинематический Rigidbody):");
                    foreach (KeyValuePair<Transform, List<string>> kvp in orphansByAnimator)
                    {
                        Log.Debug($"[MRPO-DIAG]   Аниматор '{GetHierarchyPath(kvp.Key, ev.Schematic.transform)}' -> {kvp.Value.Count} примитив(ов). Пример: [{FormatSample(kvp.Value, DiagSampleLimit)}]");
                    }
                }
                else
                {
                    Log.Debug("[MRPO-DIAG] Все примитивы под всеми аниматорами успешно захвачены в AnimatedPrimitiveGroup.");
                }

                Log.Debug($"[MRPO-DIAG] ===== '{ev.Schematic.Name}': конец оптимизации =====");
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

    private void ProcessAnimators(ProjectMER.Features.Objects.SchematicObject schematicObj, 
        OptimizedSchematic optimizedSchematic, Dictionary<string, List<string>> animStatesDict, 
        HashSet<Transform> parentsToExclude, HashSet<Transform> nonAnimatorParentsToExclude, bool diag)
    {
        List<Animator> animators = schematicObj.GetComponentsInChildren<Animator>(true).ToList();

        if (diag)
            Log.Debug($"[MRPO-DIAG] Найдено аниматоров: {animators.Count}");

        foreach (Animator anim in animators)
        {
            if (anim == null || !anim.enabled || anim.runtimeAnimatorController == null) continue;

            bool skip = false;
            Transform current = anim.transform;
            while (current != null && current != schematicObj.transform)
            {
                if (nonAnimatorParentsToExclude.Contains(current)) { skip = true; break; }
                current = current.parent;
            }
            if (skip)
            {
                if (diag)
                    Log.Debug($"[MRPO-DIAG] Аниматор '{GetHierarchyPath(anim.transform, schematicObj.transform)}' пропущен (под не-кинематическим Rigidbody/исключённым родителем)");
                continue;
            }

            string unityPath = GetUnityPath(anim.transform, schematicObj.transform);

            if (!animStatesDict.TryGetValue(unityPath, out List<string> stateAnims) || stateAnims == null || stateAnims.Count == 0)
            {
                Debug($"[ANIM] '{anim.gameObject.name}' (path '{unityPath}') in {schematicObj.Name} has no 'AnimatedStateOptimizer' " +
                      "configured on it (or its state list is empty) — this animator is left completely untouched and will never be optimized.");

                if (diag)
                {
                    List<PrimitiveObjectToy> unoptimizedUnderThisAnim = new();
                    List<AMERTInteractable> dummyAmert = new();
                    GetPrimitivesForAnimator(anim.transform, anim.transform, schematicObj.transform, parentsToExclude, unoptimizedUnderThisAnim, dummyAmert);

                    Log.Debug($"[MRPO-DIAG] НЕ НАЙДЕН в json: аниматор '{GetHierarchyPath(anim.transform, schematicObj.transform)}' (unityPath='{unityPath}') -> {unoptimizedUnderThisAnim.Count} примитив(ов) под ним останутся серверными навсегда");
                }

                continue;
            }

            List<PrimitiveObjectToy> primitivesUnderAnim = new();
            List<AMERTInteractable> amertUnderAnim = new();
            GetPrimitivesForAnimator(anim.transform, anim.transform, schematicObj.transform, parentsToExclude, primitivesUnderAnim, amertUnderAnim);

            if (diag)
                Log.Debug($"[MRPO-DIAG] Найден в json: аниматор '{GetHierarchyPath(anim.transform, schematicObj.transform)}' -> захвачено {primitivesUnderAnim.Count} примитив(ов), AMERT: {amertUnderAnim.Count}, states=[{string.Join(", ", stateAnims)}]");

            if (primitivesUnderAnim.Count == 0) continue;

            GameObject groupGo = new GameObject($"[MERO] AnimGroup_{anim.gameObject.name}");
            groupGo.transform.SetParent(schematicObj.transform, false);
            AnimatedPrimitiveGroup group = groupGo.AddComponent<AnimatedPrimitiveGroup>();
            group.Initialize(anim, optimizedSchematic, primitivesUnderAnim, amertUnderAnim, stateAnims, schematicObj.Name);
            optimizedSchematic.AnimatedPrimitiveGroups.Add(group);
        }
    }

    private void GetPrimitivesForAnimator(Transform current, Transform rootAnimator, Transform schematicRoot, 
        HashSet<Transform> parentsToExclude, List<PrimitiveObjectToy> results, List<AMERTInteractable> amertResults)
    {
        bool hasChildren = current.childCount > 0;

        if (current.TryGetComponent(out AMERTInteractable amert))
        {
            if (!amertResults.Contains(amert))
                amertResults.Add(amert);

            bool isHealthObject = current.GetComponent<HealthObject>() != null;

            if (isHealthObject)
                return;
        }
        else if (!hasChildren && current.TryGetComponent(out PrimitiveObjectToy prim))
        {
            results.Add(prim);
        }

        for (int i = 0; i < current.childCount; i++)
        {
            Transform child = current.GetChild(i);
            if (parentsToExclude.Contains(child))
                continue;

            if (child != rootAnimator && child.TryGetComponent<Animator>(out _))
                continue;

            GetPrimitivesForAnimator(child, rootAnimator, schematicRoot, parentsToExclude, results, amertResults);
        }
    }

    private string GetUnityPath(Transform target, Transform root)
    {
        List<string> path = new();
        Transform current = target;
        while (current != null && current != root)
        {
            Transform parent = current.parent;
            if (parent == null) break;
            int index = -1;
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i) == current) { index = i; break; }
            }
            if (index == -1) break;

            path.Add(index.ToString());
            current = parent;
        }
        return string.Join(" ", path);
    }
    
    private void OnAmertDisappeared(AmertDisappearEventArgs ev)
    {
        if (ev.Schematic == null) 
            return;

        foreach (OptimizedSchematic os in OptimizedSchematics.Where(s => s != null && s.Schematic == ev.Schematic).ToList())
        {
            if (ev.DestroyEntireSchematic)
            {
                os.Destroy();
                OptimizedSchematics.Remove(os);
            }
            else if (ev.TargetTransforms != null && ev.TargetTransforms.Count > 0)
            {
                os.RemovePrimitivesByTransforms(ev.TargetTransforms);
            }
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

    public static Collider CreateBestFitCollider(PrimitiveType primitiveType, GameObject colliderGo)
    {
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