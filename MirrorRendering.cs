using Assets.Scripts.Objects;
using HarmonyLib;
using UnityEngine;

namespace StationeersMirror;

internal static class MirrorRendering
{
    private static readonly AccessTools.FieldRef<RenderBatch, List<int>> BatchCounts =
        AccessTools.FieldRefAccess<RenderBatch, List<int>>("_batchCounts");
    private static readonly AccessTools.FieldRef<ThingRenderer, Renderer> UnityRenderer =
        AccessTools.FieldRefAccess<ThingRenderer, Renderer>("UnityRenderer");

    // Unity-null safe: the game destroys some MeshFilters (ThingRenderer.cs:233) while the
    // wrapper keeps the reference, and C# ?. does not see a destroyed Unity object, so the
    // game's own ThingRenderer.SharedMesh (MeshFilter?.sharedMesh) throws for those.
    internal static Mesh? MeshOf(ThingRenderer? renderer) => renderer == null ? null : MeshOf(renderer.MeshFilter);

    internal static Mesh? MeshOf(MeshFilter? filter) => filter ? filter!.sharedMesh : null;

    internal static Action CaptureRollback(Structure structure, MirrorRecord record)
    {
        var mode = structure.structureRenderMode;
        var list = structure.Renderers.ToArray();
        var states = structure.BuildStates.Concat(structure.BrokenBuildStates.Select(s => s.BuildState)).Distinct()
            .Select(s => (State: s, Wrapper: s.RendererInstance, Visualizer: s.Visualizer)).ToArray();
        var wrappers = list.Concat(states.Select(s => s.Wrapper)).Where(w => w != null).Distinct()
            .Select(w => (Wrapper: w, Renderer: w.GetRenderer(), Filter: w.MeshFilter)).ToArray();
        var enabled = structure.GetComponentsInChildren<Renderer>(true)
            .Select(r => (Renderer: r, Enabled: r.enabled)).ToArray();
        var batches = wrappers.Select(w => MeshOf(w.Wrapper)).Where(m => m != null)
            .Select(m => RenderBatch.Get(m!.GetInstanceID())).Where(b => b != null).Distinct()
            .Select(b => (Batch: b, Instances: b.Instances.Where(i => wrappers.Any(w => ReferenceEquals(w.Wrapper, i.Batchable))).ToArray())).ToArray();
        return () =>
        {
            var recovery = new MirrorRecovery();
            recovery.Try(() => RemoveFromBatches(structure));
            structure.structureRenderMode = mode;
            foreach (var entry in wrappers)
                recovery.Try(() => { UnityRenderer(entry.Wrapper) = entry.Renderer; entry.Wrapper.MeshFilter = entry.Filter; });
            foreach (var entry in states)
                recovery.Try(() => { entry.State.RendererInstance = entry.Wrapper; entry.State.Visualizer = entry.Visualizer; });
            recovery.Try(() => { structure.Renderers.Clear(); structure.Renderers.AddRange(list); });
            foreach (var entry in enabled)
                if (entry.Renderer != null)
                    recovery.Try(() => entry.Renderer.enabled = entry.Enabled);
            foreach (var child in record.CreatedRenderers)
                if (child != null)
                    recovery.Try(() => { child.SetActive(false); UnityEngine.Object.Destroy(child); });
            foreach (var entry in batches)
                recovery.Try(() =>
                {
                    // Restore only this structure's entries; other instances may
                    // have changed through callbacks while mirroring was running.
                    entry.Batch.Instances.RemoveAll(i => wrappers.Any(w => ReferenceEquals(w.Wrapper, i.Batchable)));
                    entry.Batch.Instances.AddRange(entry.Instances);
                    RebuildBatch(entry.Batch);
                });
            recovery.ThrowIfFailed();
        };
    }

    internal static void PrepareUnityRenderers(Structure structure, MirrorRecord record)
    {
        if (structure.structureRenderMode != StructureRenderMode.Batched || structure.IsCursor)
            return;

        RemoveFromBatches(structure);
        // Awake destroys a batched structure's root MeshRenderer before the mirror
        // flag is known on clients. Reconstruct missing DrawData renderers on this
        // instance. The reconstructed renderer inherits the root reflection too;
        // it must still bypass instancing for negative-determinant draw matrices.
        var replacements = new Dictionary<Renderer, Renderer>();
        var states = structure.BuildStates.Concat(structure.BrokenBuildStates.Select(state => state.BuildState));
        foreach (var state in states)
        {
            var wrapper = state.RendererInstance;
            var oldRenderer = wrapper?.GetRenderer();
            if (oldRenderer != null && oldRenderer.transform != structure.ThingTransform)
                continue;
            if (oldRenderer != null && replacements.TryGetValue(oldRenderer, out var replacement))
            {
                Bind(wrapper!, replacement);
                state.Visualizer = replacement;
                continue;
            }
            var data = state.InitialDrawData;
            var mesh = oldRenderer != null ? MeshOf(oldRenderer.GetComponent<MeshFilter>()) : data.mesh;
            var materials = oldRenderer != null ? oldRenderer.sharedMaterials : data.materials;
            if (mesh == null || materials == null || materials.Length == 0)
                continue;
            var child = new GameObject("StationeersMirror.Renderer") { layer = structure.gameObject.layer };
            record.CreatedRenderers.Add(child);
            child.transform.SetParent(structure.ThingTransform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = oldRenderer != null ? oldRenderer.shadowCastingMode : data.shadowMode;
            if (oldRenderer != null)
            {
                var properties = new MaterialPropertyBlock();
                oldRenderer.GetPropertyBlock(properties);
                renderer.SetPropertyBlock(properties);
                oldRenderer.enabled = false;
                replacements.Add(oldRenderer, renderer);
            }
            if (wrapper == null)
            {
                wrapper = new ThingRenderer(structure, renderer);
                structure.Renderers.Add(wrapper);
                state.RendererInstance = wrapper;
            }
            else
                Bind(wrapper, renderer);
            state.Visualizer = renderer;
        }
        // Keep existing wrappers so custom material mappings and build-state references
        // still refer to the same object. No shared prefab, mesh or material is modified.
        foreach (var wrapper in structure.Renderers)
            if (wrapper.GetRenderer() is { } oldRenderer && replacements.TryGetValue(oldRenderer, out var replacement))
                Bind(wrapper, replacement);
    }

    private static void Bind(ThingRenderer wrapper, Renderer renderer)
    {
        UnityRenderer(wrapper) = renderer;
        wrapper.MeshFilter = renderer.GetComponent<MeshFilter>();
        wrapper.RenderStateChanged();
    }

    internal static void UseUnityRenderers(Structure structure)
    {
        if (structure.structureRenderMode == StructureRenderMode.Batched)
        {
            RemoveFromBatches(structure);
            structure.structureRenderMode = StructureRenderMode.Standard;
        }
        foreach (var renderer in structure.GetComponentsInChildren<RocketRendererInstance>(true))
            renderer.DeregisterFromManager();
        foreach (var renderer in structure.Renderers)
            renderer.RenderStateChanged();
    }

    private static void RemoveFromBatches(Structure structure)
    {
        var batches = new HashSet<RenderBatch>();
        var renderers = structure.Renderers.Concat(structure.BuildStates.Select(state => state.RendererInstance))
            .Concat(structure.BrokenBuildStates.Select(state => state.BuildState.RendererInstance)).Distinct();
        foreach (var renderer in renderers)
        {
            var mesh = MeshOf(renderer);
            if (mesh == null)
                continue;
            var batch = RenderBatch.Get(mesh.GetInstanceID());
            if (batch != null)
                batches.Add(batch);
            RenderBatch.Deregister(renderer);
        }
        // Vanilla Deregister only edits Instances. Its Populate() also uses the
        // absolute index after 1023 and does not clear empty batch counts.
        foreach (var batch in batches)
            RebuildBatch(batch);
    }

    private static void RebuildBatch(RenderBatch batch)
    {
        var counts = BatchCounts(batch);
        for (int i = 0; i < counts.Count; i++)
            counts[i] = 0;
        for (int i = 0; i < batch.Instances.Count; i++)
            batch.Populate(i / RenderBatch.BATCH_SIZE, i % RenderBatch.BATCH_SIZE, batch.Instances[i]);
        batch.UpdatePropertyBlocks();
    }

    internal static void Restore(Structure structure, StructureRenderMode mode)
    {
        structure.structureRenderMode = mode;
        if (!structure.IsCursor && mode == StructureRenderMode.Batched && structure.CurrentBuildState?.RendererInstance != null)
            RenderBatch.Register(structure.CurrentBuildState.RendererInstance);
        foreach (var renderer in structure.GetComponentsInChildren<RocketRendererInstance>(true))
            if (renderer.isActiveAndEnabled)
                renderer.RegisterToManager();
    }
}
