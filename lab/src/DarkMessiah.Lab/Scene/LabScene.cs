using System.Numerics;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Sword;
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
    private readonly List<Puppet> puppets = [];
    private readonly List<AppearanceFact> fixtures = [];
    private readonly VoxelScenePresentation voxels;
    private readonly EntityDynamicsAdapter dynamics;
    private readonly DynamicsEntityBinding[] bindings;
    private readonly EntityTriggerProjection colliderProjection;

    internal LabScene(IEngineContext engine, LabDefinition definition, DonorContent? donor = null)
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
            // A donor prop (R10164-2): its box is its collision's, in its joint's frame, and the body turns as that joint;
            // its mass is its .phy's. The model is drawn back from the body through them (ModelFromBody).
            Puppet? puppet = null;
            Vector3 half = new(cube.Size / 2);
            float mass = cube.Mass;
            Quaternion turn = Quaternion.Identity;
            Matrix4x4 modelFromBody = Matrix4x4.Identity;
            if (cube.Model is { } model)
            {
                if (donor is null) throw new InvalidOperationException($"{LabDefinition.Path}: cube {cube.Label} names {model}, and the lab has no donor content to draw it.");
                var prop = donor.Model(model);
                if (prop.Info.CollisionBounds is not var (min, max)) throw new InvalidDataException($"{LabDefinition.Path}: {model} has no collision model to give its body.");
                half = (max - min) / 2;
                mass = prop.Info.Mass ?? cube.Mass;
                var joint = prop.Info.CollisionJoint;
                turn = joint.Rotation;
                // Model ← joint ← the box's centre in the joint's frame ← the body.
                var jointInModel = Matrix4x4.CreateFromQuaternion(joint.Rotation) * Matrix4x4.CreateTranslation(joint.Translation);
                var bodyInJoint = Matrix4x4.CreateTranslation((min + max) / 2);
                // The body turns as the joint does (its box is square to the joint), so the model stands unturned at spawn.
                Matrix4x4.Invert(bodyInJoint * jointInModel, out modelFromBody);
                puppet = new Puppet(engine, prop, entity.Value);
                puppets.Add(puppet);
            }
            Transform spawn = new(LabDefinition.Vector(cube.Center), turn, Vector3.One);
            DynamicsBodyProperties properties = new(mass, new DynamicsMassPolicy(DynamicsMassPolicyKind.DeriveFromShapeAndMass, default),
                Vector3.Zero, Vector3.Zero, default, .12f, .25f, 1, .6f, .12f, uint.MaxValue, uint.MaxValue, true, false, true);
            DynamicsBody body = engine.Dynamics.CreateCuboidBody(new DynamicsCreateCuboidBodyRequest(World,
                new DynamicsCuboidBodyConfig(spawn, half, properties)));
            Appearance appearance = puppet?.Appearance ?? engine.Graphics.CreatePrimitive(new PrimitiveAppearanceRequest(PrimitiveGeometry.Cube, false,
                LabDefinition.Color(cube.Color ?? [.6f, .45f, .3f])));
            if (puppet is null) appearances.Add(appearance);
            Cubes.Add(new LabCube(entity, cube, body, appearance, spawn) { Mass = mass, Half = half, Puppet = puppet, ModelFromBody = modelFromBody });
            Entities.Set(entity, EngineComponentTypes.Transform, spawn);
            Entities.Set(entity, EngineComponentTypes.DynamicsMotion, new DynamicsMotion(Vector3.Zero, Vector3.Zero, false));
            Entities.Set(entity, EngineComponentTypes.SpatialCollider, new SpatialCollider(-half, half, uint.MaxValue, uint.MaxValue, true, false, false));
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
        return new CharacterObstacle(cube.Entity.Value, Pose(cube), -cube.Half, cube.Half, true, motion.LinearVelocity, motion.AngularVelocity);
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

    /// <summary>
    /// How far a ray from <paramref name="origin"/> along <paramref name="direction"/> goes before it meets the arena (its
    /// voxels and fixtures, not the loose cubes or bodies), within <paramref name="length"/>; null when it meets nothing.
    /// </summary>
    internal float? Ray(Vector3 origin, Vector3 direction, float length)
    {
        var hit = engine.Spatial.CastRay(new SpatialRaycastRequest(Session, origin, direction, length, new SpatialQueryFilter(uint.MaxValue, uint.MaxValue),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
        return hit.Present ? (float)hit.Distance : null;
    }

    internal ReadOnlyMemory<SpatialEntityCollider> Colliders() => colliderProjection.ReconcileTriggers(SimulationStep, SpatialTriggerCause.Movement).Entities;

    /// <summary>Publishes the scene: fixtures, cubes and <paramref name="others"/> (the fight's arms, sword and orc).</summary>
    internal void Publish(IEnumerable<AppearanceFact> others)
    {
        List<AppearanceFact> facts = new(fixtures);
        facts.AddRange(others);
        facts.AddRange(Cubes.Select(cube => cube.Puppet is { } puppet ? puppet.Fact(cube.ModelAt(Pose(cube)))
            : new AppearanceFact(cube.Entity.Value, false, 0, Pose(cube) with { Scale = 2 * cube.Half }, cube.Appearance, true, RenderLayer.Scene)));
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
        foreach (Puppet puppet in puppets) puppet.Dispose();
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
        // The hazards (#10157): each spike rack a dark board, colliding, with a rack of spikes out of it; each fire a glowing
        // bed of embers with flames, drawn only.
        if (Definition.Hazards.Spikes.Length > 0 || Definition.Hazards.Fires.Length > 0)
        {
            (Appearance Look, uint Asset) Solid(Color color)
            {
                MeshResource mesh = LabGeometry.Cube(engine, Material(color));
                meshes.Add(mesh);
                Appearance appearance = engine.Graphics.CreateMeshAppearance(mesh);
                appearances.Add(appearance);
                uint asset = (uint)assets.Count + 100;
                assets.Add(new StaticMeshAsset(asset, new MeshResourceReference(mesh), 0, 0, 0, 0));
                return (appearance, asset);
            }
            void Draw(Appearance look, Vector3 centre, Vector3 size, uint? collide = null)
            {
                EntityId entity = Entities.Create();
                Transform pose = new(centre, Quaternion.Identity, size);
                fixtures.Add(new AppearanceFact(entity.Value, false, 0, pose, look, true, RenderLayer.Scene));
                if (collide is uint asset) instances.Add(new StaticMeshInstance(entity.Value, asset, pose));
            }
            if (Definition.Hazards.Spikes.Length > 0)
            {
                var (board, boardAsset) = Solid(new Color(.18f, .14f, .12f, 1));
                var iron = Solid(new Color(.55f, .55f, .58f, 1)).Look;
                foreach (SpikeDefinition rack in Definition.Hazards.Spikes)
                {
                    Vector3 min = LabDefinition.Vector(rack.Min), max = LabDefinition.Vector(rack.Max);
                    Vector3 facing = Vector3.Normalize(LabDefinition.Vector(rack.Facing) with { Y = 0 });
                    Draw(board, (min + max) / 2, max - min, boardAsset);
                    // Spikes every 0.3 m across its face, 0.25 m long, pointing out of it.
                    Vector3 across = new(facing.Z, 0, -facing.X);
                    float halfDepth = MathF.Abs(Vector3.Dot((max - min) / 2, facing)), width = MathF.Abs(Vector3.Dot(max - min, across));
                    Vector3 face = ((min + max) / 2) with { Y = 0 } + facing * halfDepth;
                    for (float y = min.Y + .2f; y < max.Y - .1f; y += .3f)
                        for (float a = -width / 2 + .15f; a < width / 2; a += .3f)
                            Draw(iron, face + across * a + Vector3.UnitY * y + facing * .125f,
                                new Vector3(MathF.Abs(facing.X) * .25f + MathF.Abs(across.X) * .04f, .04f, MathF.Abs(facing.Z) * .25f + MathF.Abs(across.Z) * .04f));
                }
            }
            if (Definition.Hazards.Fires.Length > 0)
            {
                Appearance embers = Solid(new Color(.9f, .3f, .05f, 1)).Look, flame = Solid(new Color(1f, .62f, .1f, 1)).Look;
                foreach (FireDefinition fire in Definition.Hazards.Fires)
                {
                    Vector3 centre = LabDefinition.Vector(fire.Center);
                    Draw(embers, centre + Vector3.UnitY * .05f, new Vector3(fire.Radius * 1.6f, .1f, fire.Radius * 1.6f));
                    for (int i = 0; i < 5; i++)
                    {
                        float turn = i * 2.4f, out_ = fire.Radius * .5f * (i % 2 == 0 ? .3f : .8f), tall = 1.2f - .15f * i;
                        Draw(flame, centre + new Vector3(MathF.Cos(turn) * out_, tall / 2, MathF.Sin(turn) * out_), new Vector3(.25f, tall, .25f));
                    }
                }
            }
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

internal sealed record LabCube(EntityId Entity, CubeDefinition Definition, DynamicsBody Body, Appearance Appearance, Transform Spawn)
{
    /// <summary>Its mass, kg: authored, or a donor prop's <c>.phy</c> mass (R10164-2).</summary>
    internal float Mass { get; init; } = Definition.Mass;
    /// <summary>Its box's half extents, metres.</summary>
    internal Vector3 Half { get; init; } = new(Definition.Size / 2);
    /// <summary>Its size, the box's longest side.</summary>
    internal float Size => 2 * MathF.Max(Half.X, MathF.Max(Half.Y, Half.Z));
    /// <summary>A donor prop's drawn model, or null for a coloured cube.</summary>
    internal Puppet? Puppet { get; init; }
    /// <summary>Where a donor prop's model stands for its body's pose: the body's frame to the model's.</summary>
    internal Matrix4x4 ModelFromBody { get; init; } = Matrix4x4.Identity;

    /// <summary>The model's placement for the body at <paramref name="pose"/>.</summary>
    internal Transform ModelAt(Transform pose)
    {
        var world = ModelFromBody * Matrix4x4.CreateFromQuaternion(pose.Rotation) * Matrix4x4.CreateTranslation(pose.Translation);
        Matrix4x4.Decompose(world, out _, out var rotation, out var translation);
        return new Transform(translation, rotation, Vector3.One);
    }
}
