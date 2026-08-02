using System;
using System.Collections.Generic;
using System.Linq;
using AdminToys;
using AdvancedMERTools.API.Core;
using Exiled.API.Features;
using Mirror;
using ProjectMER.Features.Components;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class AnimatedPrimitiveGroup : MonoBehaviour
{
    private const string DiagSchematicName = "HCZ_Sklad_Tech";

    public Animator Animator;
    public OptimizedSchematic Schematic;
    public List<PrimitiveObjectToy> Primitives = new();
    public List<AMERTInteractable> AmertComponents = new();
    public List<string> StateAnimations = new();

    public bool IsFrozen;

    private AnimationStateTracker _tracker;
    private HashSet<int> _stateHashes = new();
    private bool _diag;
    private string _diagLabel;
    private string _schematicName;

    private List<ClientSidePrimitive> _frozenClientPrimitives = new();
    private List<Collider> _proxies = new();

    public void Initialize(Animator animator, OptimizedSchematic schematic, List<PrimitiveObjectToy> primitives, List<AMERTInteractable> amertComponents, List<string> stateAnimations, string schematicName = null)
    {
        Animator = animator;
        Schematic = schematic;
        Primitives = primitives;
        AmertComponents = amertComponents ?? new();
        StateAnimations = stateAnimations;

        _schematicName = schematicName;
        _diag = schematicName == DiagSchematicName;
        _diagLabel = $"[MRPO-DIAG] AnimGroup '{animator.name}'";

        _stateHashes = new HashSet<int>(stateAnimations.Select(Animator.StringToHash));

        _tracker = AnimationStateTracker.GetOrCreate(animator);
        _tracker.OnTransitionStarted += OnTransitionStarted;
        _tracker.OnStateEntered += OnStateEntered;

        if (_diag)
            Log.Debug($"{_diagLabel}: init, primitives={Primitives.Count}, amert={AmertComponents.Count}, currentHash={_tracker.CurrentStateHash}, inTransition={animator.IsInTransition(0)}");

        if (_tracker.IsInitialized && !animator.IsInTransition(0))
            OnStateEntered(_tracker.CurrentStateHash);
    }

    private void OnTransitionStarted(int nextStateHash)
    {
        if (IsFrozen)
            Wake();
    }

    private void OnStateEntered(int stateHash)
    {
        bool matches = _stateHashes.Contains(stateHash);

        if (matches)
        {
            if (!IsFrozen)
                FreezeImmediate();
        }
        else if (IsFrozen)
        {
            Wake();
        }
    }

    public void Wake()
    {
        if (!IsFrozen)
            return;

        IsFrozen = false;

        if (_diag)
            Log.Debug($"{_diagLabel}: разморожено {_frozenClientPrimitives.Count} примитив(ов)");

        foreach (var clientPrim in _frozenClientPrimitives)
        {
            Schematic.NonClusteredPrimitives.Remove(clientPrim);
            clientPrim.DestroyForEveryone();
        }
        _frozenClientPrimitives.Clear();

        foreach (var proxy in _proxies)
        {
            if (proxy != null) Object.Destroy(proxy.gameObject);
        }
        _proxies.Clear();

        foreach (var prim in Primitives)
        {
            if (prim == null) continue;
            prim.enabled = true;
            prim.NetworkIsStatic = false;
            NetworkServer.Spawn(prim.gameObject);
        }
    }

    private void FreezeImmediate()
    {
        IsFrozen = true;

        if (_diag)
            Log.Debug($"{_diagLabel}: заморожено {Primitives.Count} примитив(ов)");

        foreach (var prim in Primitives)
        {
            if (prim == null) continue;

            Vector3 position = prim.transform.position;
            Quaternion rotation = prim.transform.rotation;
            Vector3 scale = prim.transform.lossyScale;
            PrimitiveType primitiveType = prim.PrimitiveType;
            Color color = prim.NetworkMaterialColor;
            PrimitiveFlags primitiveFlags = prim.PrimitiveFlags;

            var clientPrim = new ClientSidePrimitive(position, rotation, scale, primitiveType, color, primitiveFlags, prim.name, prim.transform);
            _frozenClientPrimitives.Add(clientPrim);
            Schematic.NonClusteredPrimitives.Add(clientPrim);
            clientPrim.SpawnForEveryone();

            if (primitiveFlags.HasFlag(PrimitiveFlags.Collidable))
            {
                Vector3 absScale = new(Math.Abs(scale.x), Math.Abs(scale.y), Math.Abs(scale.z));
                GameObject colliderGo = new($"[MERO_PROXY] {prim.name}")
                {
                    transform =
                    {
                        position = position,
                        rotation = rotation,
                        localScale = absScale
                    }
                };
                Collider col = MerOptimizer.CreateBestFitCollider(primitiveType, colliderGo);
                if (col != null) _proxies.Add(col);
                else Object.Destroy(colliderGo);
            }

            NetworkServer.UnSpawn(prim.gameObject);
            prim.enabled = false;
        }
    }

    public void DestroyGroup()
    {
        if (_tracker != null)
        {
            _tracker.OnTransitionStarted -= OnTransitionStarted;
            _tracker.OnStateEntered -= OnStateEntered;
        }

        foreach (var clientPrim in _frozenClientPrimitives)
        {
            if (Schematic != null) Schematic.NonClusteredPrimitives.Remove(clientPrim);
            if (clientPrim != null) clientPrim.DestroyForEveryone();
        }

        foreach (var proxy in _proxies)
        {
            if (proxy != null) Object.Destroy(proxy.gameObject);
        }

        Object.Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (_tracker != null)
        {
            _tracker.OnTransitionStarted -= OnTransitionStarted;
            _tracker.OnStateEntered -= OnStateEntered;
        }
    }
}