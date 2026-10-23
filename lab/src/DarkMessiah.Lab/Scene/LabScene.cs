using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Entities;

namespace DarkMessiah.Lab.Scene;

internal sealed class LabScene : IDisposable
{
    private const int ChunkSize = 16;
    private readonly IEngineContext engine;
    private readonly List<Material> materials = [];
    private readonly List<MeshResource> meshes = [];
    private readonly List<Appearance> appearances = [];
    private readonly List<AppearanceFact> fixtures = [];
    private readonly VoxelScenePresentation voxels;
    private readonly EntityDynamicsAdapter dynamics;
    private readonly DynamicsEntityBinding[] bindings;
    private readonly EntityTriggerProjection colliderProjection;

    internal LabScene(IEngineContext engine, LabDefinition definition)
    {
        this.engine = engine;
        Definition = definition;
        Entities = new EntityStore([EngineComponentTypes.Transform, EngineComponentTypes.CharacterMotion,
            EngineComponentTypes.SpatialCollider, EngineComponentTypes.DynamicsMotion]);
        PlayerEntity = Entities.Create(); // The C# character proposal owns identity 1; objects begin after it.
        Session = engine.Spatial.CreateSession(new SpatialSessionConfig(definition.VoxelSize, ChunkSize, VoxelSurfaceMode.GreedyCubes));
        engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(Session,
            Enumerable.Range(1, 4).Select(id => new VoxelMaterialCollision((uint)id, true)).ToArray()));
        AdmitVoxels();
        Color[] colors = [new(.23f, .29f, .37f, 1), new(.37f, .43f, .52f, 1), new(.2f, .62f, .61f, 1), new(.37f, .42f, .75f, 1)];
        VoxelSceneMaterialBinding[] voxelMaterials = colors.Select((color, index) =>
            new VoxelSceneMaterialBinding((uint)index + 1, Material(color))).ToArray();
        voxels = engine.VoxelScenePresentation.ProjectScene(new ProjectVoxelSceneRequest(Session, voxelMaterials));
        AdmitFixtures();
        World = engine.Dynamics.CreateWorld(new DynamicsWorldConfig(new Vector3(0, -definition.Gravity, 0)));
        engine.Dynamics.BindWorldCollision(new DynamicsWorldCollisionBindingRequest(World, Session));
        foreach (CubeDefinition cube in definition.Cubes)
        {
            EntityId entity = Entities.Create();
            Transform spawn = new(LabDefinition.Vector(cube.Center), Quaternion.Identity, Vector3.One);
            DynamicsBodyProperties properties = new(cube.Mass, new DynamicsMassPolicy(DynamicsMassPolicyKind.DeriveFromShapeAndMass, default),
                Vector3.Zero, Vector3.Zero, default, .12f, .25f, 1, .6f, .12f, uint.MaxValue, uint.MaxValue, true, false, true);
            DynamicsBody body = engine.Dynamics.CreateCuboidBody(new DynamicsCreateCuboidBodyRequest(World,
                new DynamicsCuboidBodyConfig(spawn, new Vector3(cube.Size / 2), properties)));
            Appearance appearance = engine.Graphics.CreatePrimitive(new PrimitiveAppearanceRequest(PrimitiveGeometry.Cube, false, LabDefinition.Color(cube.Color)));
            appearances.Add(appearance);
            Cubes.Add(new LabCube(entity, cube, body, appearance, spawn));
            Entities.Set(entity, EngineComponentTypes.Transform, spawn);
            Entities.Set(entity, EngineComponentTypes.DynamicsMotion, new DynamicsMotion(Vector3.Zero, Vector3.Zero, false));
            Entities.Set(entity, EngineComponentTypes.SpatialCollider, new SpatialCollider(new Vector3(-cube.Size / 2),
                new Vector3(cube.Size / 2), uint.MaxValue, uint.MaxValue, true, false, false));
        }
        bindings = Cubes.Select(cube => new DynamicsEntityBinding(cube.Entity, cube.Body)).ToArray();
        dynamics = new EntityDynamicsAdapter(Entities, engine.Dynamics, World);
        colliderProjection = new EntityTriggerProjection(Entities, engine.Spatial, Session, EngineComponentTypes.SpatialCollider);
    }

    internal LabDefinition Definition { get; }
    internal EntityStore Entities { get; }
    internal EntityId PlayerEntity { get; }
    internal SpatialSession Session { get; }
    internal DynamicsWorld World { get; }
    internal List<LabCube> Cubes { get; } = [];
    internal Transform Pose(LabCube cube) => Entities.Get(cube.Entity, EngineComponentTypes.Transform);
    internal DynamicsMotion Motion(LabCube cube) => Entities.Get(cube.Entity, EngineComponentTypes.DynamicsMotion);
    internal ulong SimulationStep { get; set; }

    internal void Step(float delta, ReadOnlyMemory<DynamicsEntityAction> actions)
    {
        dynamics.Step(delta, 1, bindings, actions);
    }

    internal CharacterObstacle[] Obstacles(ulong? ignored = null) => Cubes.Where(cube => cube.Entity.Value != ignored).Select(cube =>
    {
        DynamicsMotion motion = Motion(cube);
        return new CharacterObstacle(cube.Entity.Value, Pose(cube), new Vector3(-cube.Definition.Size / 2),
            new Vector3(cube.Definition.Size / 2), true, motion.LinearVelocity, motion.AngularVelocity);
    }).ToArray();

    /// <summary>The cube the body stands on, at its current pose, so the Engine carries the body with it.</summary>
    internal CharacterSupport Support(CharacterMotion motion, ulong? ignored)
    {
        if (!motion.SupportEntityPresent) return default;
        LabCube? supported = Cubes.Find(cube => cube.Entity.Value == motion.SupportEntity && cube.Entity.Value != ignored);
        return supported is null
            ? new CharacterSupport(true, CharacterSupportLifecycle.Destroyed, motion.SupportEntity, default)
            : new CharacterSupport(true, CharacterSupportLifecycle.Active, motion.SupportEntity, Pose(supported));
    }

    internal EntityId? CubeEntity(ulong id) => Cubes.Find(cube => cube.Entity.Value == id)?.Entity;

    internal ReadOnlyMemory<SpatialEntityCollider> Colliders() => colliderProjection.ReconcileTriggers(SimulationStep, SpatialTriggerCause.Movement).Entities;

    /// <summary>Publishes the scene: fixtures, cubes and <paramref name="others"/> (the fight's arms, sword and orc).</summary>
    internal void Publish(IEnumerable<AppearanceFact> others)
    {
        List<AppearanceFact> facts = new(fixtures);
        facts.AddRange(others);
        facts.AddRange(Cubes.Select(cube => new AppearanceFact(cube.Entity.Value, false, 0,
            Pose(cube) with { Scale = new Vector3(cube.Definition.Size) }, cube.Appearance, true, RenderLayer.Scene)));
        engine.Graphics.PublishSnapshot(facts.ToArray());
    }

    internal void Reset()
    {
        foreach (LabCube cube in Cubes)
        {
            engine.Dynamics.Reset(new DynamicsResetRequest(cube.Body, cube.Spawn, Vector3.Zero, Vector3.Zero, false));
            Entities.Set(cube.Entity, EngineComponentTypes.Transform, cube.Spawn);
            Entities.Set(cube.Entity, EngineComponentTypes.DynamicsMotion, new DynamicsMotion(Vector3.Zero, Vector3.Zero, false));
        }
    }

    public void Dispose()
    {
        engine.Graphics.PublishSnapshot([]);
        foreach (LabCube cube in Cubes) cube.Body.Dispose();
        World.Dispose();
        voxels.Dispose();
        foreach (Appearance appearance in appearances) appearance.Dispose();
        foreach (MeshResource mesh in meshes) mesh.Dispose();
        foreach (Material material in materials) material.Dispose();
        Session.Dispose();
        Entities.Dispose();
    }

    private Material Material(Color color)
    {
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(color, default, .8f, new Color(1, 1, 1, 1), Vector3.Zero, 0, false));
        materials.Add(material);
        return material;
    }

    /// <summary>A ladder as the player climbs it.</summary>
    internal static Player.Ladder Ladder(LadderDefinition l) => new(l.Label, LabDefinition.Vector(l.Foot), l.Top, LabDefinition.Vector(l.Facing));

    private void AdmitFixtures()
    {
        List<StaticMeshAsset> assets = [];
        List<StaticMeshInstance> instances = [];
        MeshResource cubeMesh = LabGeometry.Cube(engine, Material(new Color(.35f, .62f, .76f, 1)));
        meshes.Add(cubeMesh);
        assets.Add(new StaticMeshAsset(1, new MeshResourceReference(cubeMesh), 0, 0, 0, 0));
        Appearance cubeAppearance = engine.Graphics.CreateMeshAppearance(cubeMesh);
        appearances.Add(cubeAppearance);
        foreach (BlockDefinition block in Definition.Blocks)
        {
            EntityId entity = Entities.Create();
            Transform pose = new(LabDefinition.Vector(block.Center), Quaternion.Identity, LabDefinition.Vector(block.Size));
            fixtures.Add(new AppearanceFact(entity.Value, false, 0, pose, cubeAppearance, true, RenderLayer.Scene));
            instances.Add(new StaticMeshInstance(entity.Value, 1, pose));
        }
        // The ladders, drawn only (R10163-2): their rails and rungs against the faces they stand on.
        if (Definition.Ladders.Length > 0)
        {
            MeshResource woodMesh = LabGeometry.Cube(engine, Material(new Color(.45f, .3f, .17f, 1)));
            meshes.Add(woodMesh);
            Appearance wood = engine.Graphics.CreateMeshAppearance(woodMesh);
            appearances.Add(wood);
            foreach (LadderDefinition definition in Definition.Ladders)
                foreach (var (centre, size) in Player.LadderRules.Pieces(Ladder(definition)))
                    fixtures.Add(new AppearanceFact(Entities.Create().Value, false, 0, new Transform(centre, Quaternion.Identity, size), wood, true, RenderLayer.Scene));
        }
        foreach (RampDefinition ramp in Definition.Ramps)
        {
            EntityId entity = Entities.Create();
            MeshResource mesh = LabGeometry.Ramp(engine, ramp, Material(LabDefinition.Color(ramp.Color)));
            meshes.Add(mesh);
            Appearance appearance = engine.Graphics.CreateMeshAppearance(mesh);
            appearances.Add(appearance);
            Transform pose = new(LabDefinition.Vector(ramp.Origin), Quaternion.Identity, Vector3.One);
            fixtures.Add(new AppearanceFact(entity.Value, false, 0, pose, appearance, true, RenderLayer.Scene));
            assets.Add(new StaticMeshAsset(entity.Value, new MeshResourceReference(mesh), 0, 0, 0, 0));
            instances.Add(new StaticMeshInstance(entity.Value, entity.Value, pose));
        }
        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(Session, assets.ToArray(),
            ReadOnlyMemory<Vector3>.Empty, ReadOnlyMemory<Triangle>.Empty, instances.ToArray()));
    }

    private void AdmitVoxels()
    {
        Dictionary<(long X, long Y, long Z), uint[]> chunks = [];
        foreach (VolumeDefinition volume in Definition.Volumes)
        {
            Vector3 min = LabDefinition.Vector(volume.Min) / Definition.VoxelSize;
            Vector3 max = LabDefinition.Vector(volume.Max) / Definition.VoxelSize;
            for (long z = (long)min.Z; z < max.Z; z++)
                for (long y = (long)min.Y; y < max.Y; y++)
                    for (long x = (long)min.X; x < max.X; x++)
                    {
                        var key = ((long)Math.Floor((double)x / ChunkSize), (long)Math.Floor((double)y / ChunkSize), (long)Math.Floor((double)z / ChunkSize));
                        if (!chunks.TryGetValue(key, out uint[]? chunk)) chunks.Add(key, chunk = new uint[ChunkSize * ChunkSize * ChunkSize]);
                        int lx = (int)(x - key.Item1 * ChunkSize), ly = (int)(y - key.Item2 * ChunkSize), lz = (int)(z - key.Item3 * ChunkSize);
                        chunk[lx + ChunkSize * (ly + ChunkSize * lz)] = volume.Material;
                    }
        }
        List<uint> materials = [];
        List<VoxelResidencyOperation> operations = [];
        foreach (var (key, chunk) in chunks)
        {
            operations.Add(new VoxelResidencyOperation(VoxelResidencyOperationKind.Admit,
                new VoxelChunkIdentity(key.X, key.Y, key.Z), (uint)materials.Count, (uint)chunk.Length));
            materials.AddRange(chunk);
        }
        engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(Session, operations.ToArray(), materials.ToArray()));
    }
}

internal sealed record LabCube(EntityId Entity, CubeDefinition Definition, DynamicsBody Body, Appearance Appearance, Transform Spawn);
