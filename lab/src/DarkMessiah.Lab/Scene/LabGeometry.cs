using System.Numerics;
using Rusty.Engine;

namespace DarkMessiah.Lab.Scene;

// Authored triangular-prism ramps. Engine owns meshing admission, queries and rendering.
internal static class LabGeometry
{
    internal static MeshResource Ramp(IEngineContext engine, RampDefinition ramp, Material material)
    {
        float height = MathF.Tan(ramp.AngleDegrees * MathF.PI / 180) * ramp.Length;
        Vector3[] points = [new(0, 0, 0), new(ramp.Width, 0, 0), new(0, 0, ramp.Length),
            new(ramp.Width, 0, ramp.Length), new(0, height, ramp.Length), new(ramp.Width, height, ramp.Length)];
        return Mesh(engine, points, [(0, 4, 5), (0, 5, 1), (0, 2, 4), (1, 5, 3), (2, 3, 5), (2, 5, 4), (0, 1, 3), (0, 3, 2)], material);
    }

    internal static MeshResource Cube(IEngineContext engine, Material material)
    {
        Vector3[] points = [new(-.5f,-.5f,-.5f),new(.5f,-.5f,-.5f),new(.5f,.5f,-.5f),new(-.5f,.5f,-.5f),
            new(-.5f,-.5f,.5f),new(.5f,-.5f,.5f),new(.5f,.5f,.5f),new(-.5f,.5f,.5f)];
        return Mesh(engine, points, [(0,3,2),(0,2,1),(4,5,6),(4,6,7),(0,4,7),(0,7,3),
            (1,2,6),(1,6,5),(3,7,6),(3,6,2),(0,1,5),(0,5,4)], material);
    }

    private static MeshResource Mesh(IEngineContext engine, Vector3[] points, (int A, int B, int C)[] faces, Material material)
    {
        List<Vector3> vertices = [], normals = [];
        List<uint> indices = [];
        foreach ((int a, int b, int c) in faces)
        {
            Vector3 normal = Vector3.Normalize(Vector3.Cross(points[b] - points[a], points[c] - points[a]));
            foreach (int index in new[] { a, b, c })
            {
                indices.Add((uint)vertices.Count);
                vertices.Add(points[index]);
                normals.Add(normal);
            }
        }
        return engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(vertices.ToArray(), normals.ToArray(),
            ReadOnlyMemory<Vector2>.Empty, indices.ToArray(), new[] { new MeshGroup(0, 0, (uint)indices.Count) },
            new[] { new MeshMaterialBinding(0, material) }));
    }
}
