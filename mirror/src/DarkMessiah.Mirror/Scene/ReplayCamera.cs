using System.Numerics;
using DarkMessiah.Mirror.Replay;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Mirror.Scene;

internal enum CameraMode
{
    /// <summary>Behind and above the player, looking where they face.</summary>
    Chase,
    /// <summary>The player's own eye.</summary>
    Eye,
    /// <summary>High over the player, looking down at the fight around them.</summary>
    Overview,
}

/// <summary>The product camera, following the recorded player. Positions are worked out in Source units, then converted.</summary>
internal sealed class ReplayCamera : IDisposable
{
    /// <summary>Source's standing eye height for the player.</summary>
    const float EyeHeight = 64;

    readonly IEngineContext engine;
    readonly Camera camera;
    readonly EntityTrack? player;

    public CameraMode Mode { get; set; } = CameraMode.Chase;

    public ReplayCamera(IEngineContext engine, RecordedFight fight)
    {
        this.engine = engine;
        player = fight.Tracks.FirstOrDefault(t => t.ClassName == "player");
        camera = engine.CameraView.CreateCamera(Descriptor(Pose(Vector3.Zero, Vector3.UnitX)));
        engine.CameraView.SetActiveCamera(camera);
    }

    public void Next() => Mode = (CameraMode)(((int)Mode + 1) % 3);

    public void Follow(double time)
    {
        if (player is null || player.UpdateAt(time) < 0) return;
        var sample = player.Sample(time);
        var forward = Vector3.Transform(Vector3.UnitX, sample.Rotation);
        var flat = Vector3.Normalize(new Vector3(forward.X, forward.Y, 0) + new Vector3(1e-6f, 0, 0));
        var eye = sample.Origin + new Vector3(0, 0, EyeHeight);
        var (from, to) = Mode switch
        {
            CameraMode.Eye => (eye, eye + forward * 100),
            CameraMode.Overview => (sample.Origin - flat * 300 + new Vector3(0, 0, 500), sample.Origin + flat * 150),
            _ => (eye - flat * 110 + new Vector3(0, 0, 30), eye + flat * 120 - new Vector3(0, 0, 20)),
        };
        engine.CameraView.UpdateCamera(new(camera, Descriptor(Pose(from, to))));
    }

    static CameraPose Pose(Vector3 from, Vector3 to)
    {
        CameraQueries.TryLookAtPose(SourceSpace.Position(from), SourceSpace.Position(to), 0, out var pose);
        return pose;
    }

    static CameraDescriptor Descriptor(CameraPose pose) =>
        new(pose, CameraBasisMode.Derived, default, new(CameraProjectionKind.Perspective, 70, 0, .05f, 600), CameraViewports.Full);

    public void Dispose() => camera.Dispose();
}
